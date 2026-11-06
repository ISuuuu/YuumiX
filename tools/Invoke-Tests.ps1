#requires -Version 7.0
<#
.SYNOPSIS
YuumiX 统一分级测试执行器
.DESCRIPTION
集中调度 YuumiX 各层测试套件（架构与模式门禁、Native AOT 无反射验证、Core 业务测试、App UI 逻辑测试），
统一管理测试输出和 .trx 测试结果，供本地开发与 CI 流程复用。
.EXAMPLE
./tools/Invoke-Tests.ps1 -Suite Gate
./tools/Invoke-Tests.ps1 -Suite Aot
./tools/Invoke-Tests.ps1 -Suite All
#>
[CmdletBinding()]
param(
    [string[]]$Suite = @('All'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ResultsDirectory = 'artifacts/test-results',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$validSuites = @('All', 'Gate', 'Aot', 'Core', 'App')

# 展开逗号分隔参数以兼容不同 shell 调用习惯
$normalizedInput = @()
foreach ($s in $Suite) {
    if ($s.Contains(',')) {
        $normalizedInput += ($s -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    } else {
        $normalizedInput += $s.Trim()
    }
}

foreach ($s in $normalizedInput) {
    if ($s -notin $validSuites) {
        throw "无效测试套件: '$s'。支持的套件为: $($validSuites -join ', ')"
    }
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location -LiteralPath $repo

try {
    $resolvedResultsDir = [System.IO.Path]::GetFullPath((Join-Path $repo $ResultsDirectory))
    if (-not (Test-Path $resolvedResultsDir)) {
        New-Item -ItemType Directory -Path $resolvedResultsDir -Force | Out-Null
    }

    $suiteList = if ($normalizedInput -contains 'All' -or $normalizedInput.Count -eq 0) {
        @('Gate', 'Aot', 'Core', 'App')
    } else {
        $normalizedInput | Select-Object -Unique
    }

    $summary = [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Configuration = $Configuration
        Suites = @()
    }

    $failedSuites = [System.Collections.Generic.List[string]]::new()
    $totalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    foreach ($s in $suiteList) {
        Write-Host "`n========================================================" -ForegroundColor Cyan
        Write-Host " [Suite] 正在运行测试套件: $s ($Configuration)" -ForegroundColor Cyan
        Write-Host "========================================================`n" -ForegroundColor Cyan

        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $status = 'Passed'
        $errorMsg = $null

        try {
            switch ($s) {
                'Gate' {
                    # 模式级与源码级规范门禁扫描
                    $proj = 'tests/YuumiX.SourceScan.Tests/YuumiX.SourceScan.Tests.csproj'
                    $args = @('test', $proj, '-c', $Configuration, '-p:Platform=x64',
                              '--logger', 'trx;LogFileName=gate.trx',
                              '--results-directory', $resolvedResultsDir)
                    if ($NoBuild) { $args += '--no-build' }

                    dotnet @args
                    if ($LASTEXITCODE -ne 0) { throw "SourceScan 测试未通过 (退出码: $LASTEXITCODE)" }
                }

                'Aot' {
                    # 反射关闭模式下的序列化与 SignalR 契约运行时验证
                    $proj = 'tests/YuumiX.AotVerification/YuumiX.AotVerification.csproj'
                    $args = @('run', '--project', $proj, '-c', $Configuration, '-p:Platform=x64')
                    if ($NoBuild) { $args += '--no-build' }

                    dotnet @args
                    if ($LASTEXITCODE -ne 0) { throw "AotVerification 验收未通过 (退出码: $LASTEXITCODE)" }
                }

                'Core' {
                    # Core 核心业务、LCU、自动化与算法逻辑
                    $proj = 'tests/YuumiX.Core.Tests/YuumiX.Core.Tests.csproj'
                    $args = @('test', $proj, '-c', $Configuration, '-p:Platform=x64',
                              '--logger', 'trx;LogFileName=core.trx',
                              '--results-directory', $resolvedResultsDir)
                    if ($NoBuild) { $args += '--no-build' }

                    dotnet @args
                    if ($LASTEXITCODE -ne 0) { throw "Core 单元测试未通过 (退出码: $LASTEXITCODE)" }
                }

                'App' {
                    # WinUI / Reactor 纯托管决策与组件行为测试
                    $proj = 'tests/YuumiX.App.Tests/YuumiX.App.Tests.csproj'
                    $args = @('test', $proj, '-c', $Configuration, '-p:Platform=x64',
                              '--logger', 'trx;LogFileName=app.trx',
                              '--results-directory', $resolvedResultsDir)
                    if ($NoBuild) { $args += '--no-build' }

                    dotnet @args
                    if ($LASTEXITCODE -ne 0) { throw "App 决策测试未通过 (退出码: $LASTEXITCODE)" }
                }
            }
        }
        catch {
            $status = 'Failed'
            $errorMsg = $_.Exception.Message
            $failedSuites.Add($s)
            Write-Host "[Error] 套件 $s 失败: $errorMsg" -ForegroundColor Red
        }
        finally {
            $sw.Stop()
            $summary.Suites += [ordered]@{
                Name = $s
                Status = $status
                DurationSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
                Error = $errorMsg
            }
        }
    }

    $totalStopwatch.Stop()
    $summary.TotalDurationSeconds = [Math]::Round($totalStopwatch.Elapsed.TotalSeconds, 2)

    # 写入测试运行摘要
    $summaryPath = Join-Path $resolvedResultsDir 'summary.json'
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding utf8

    Write-Host "`n---------------- 测试执行摘要 ----------------" -ForegroundColor Cyan
    foreach ($res in $summary.Suites) {
        $color = if ($res.Status -eq 'Passed') { 'Green' } else { 'Red' }
        Write-Host "  - $($res.Name.PadRight(6)): $($res.Status) ($($res.DurationSeconds)s)" -ForegroundColor $color
    }
    Write-Host "总耗时: $($summary.TotalDurationSeconds)s" -ForegroundColor Cyan
    Write-Host "结果已输出至: $resolvedResultsDir" -ForegroundColor Cyan

    if ($failedSuites.Count -gt 0) {
        throw "测试执行失败：套件 [$($failedSuites -join ', ')] 未通过验证。"
    }
}
finally {
    Pop-Location
}
