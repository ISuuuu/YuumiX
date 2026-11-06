#requires -Version 7.0
<#
.SYNOPSIS
YuumiX 代码格式化与排版验证工具
.DESCRIPTION
格式化指定的工程或源码范围，或在 CI 中检查代码是否符合统一排版规范（无未提交的格式变更）。
借鉴 Tonarink 实践，支持按工程 Scope 与具体文件 Include 过滤，避免无意义的全局改动。
.EXAMPLE
# 本地格式化 Core 模块代码
./tools/Format-Code.ps1 -Scope Core

# 本地仅格式化指定修改的文件
./tools/Format-Code.ps1 -Include src/YuumiX.Core/Common/ColorHex.cs

# CI 中验证格式化（若有未按规范格式化的文件则报错退出）
./tools/Format-Code.ps1 -Scope Core,App -Verify
#>
[CmdletBinding()]
param(
    [string[]]$Scope = @('Core', 'App'),

    [string[]]$Include,

    [ValidateSet('all', 'whitespace', 'style', 'analyzers')]
    [string]$Type = 'all',

    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location -LiteralPath $repo

try {
    # 解析当前 global.json 选定的 .NET SDK，防止多 SDK 环境下子命令路径错位
    $sdkVersion = (dotnet --version).Trim()
    $sdkPattern = '^' + [regex]::Escape($sdkVersion) + ' \[(.+)\]$'
    $sdkLine = dotnet --list-sdks | Where-Object { $_ -match $sdkPattern } | Select-Object -First 1
    $sdkCli = if ($sdkLine) {
        $sdkRoot = [regex]::Match($sdkLine, $sdkPattern).Groups[1].Value
        Join-Path $sdkRoot "$sdkVersion/dotnet.dll"
    } else {
        $null
    }

    $validScopes = @('All', 'Core', 'App', 'Tests')
    $normalizedScopes = @()
    foreach ($s in $Scope) {
        if ($s.Contains(',')) {
            $normalizedScopes += ($s -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        } else {
            $normalizedScopes += $s.Trim()
        }
    }

    foreach ($s in $normalizedScopes) {
        if ($s -notin $validScopes) {
            throw "无效的 Scope: '$s'。支持的 Scope 为: $($validScopes -join ', ')"
        }
    }

    # 当传入 -Include 且未显式指定 -Scope 时，根据文件路径智能缩小 Scope
    if ($Include -and $Include.Count -gt 0 -and -not $PSBoundParameters.ContainsKey('Scope')) {
        $allCore = $true
        $allApp = $true
        foreach ($inc in $Include) {
            $normalizedInc = $inc -replace '\\', '/'
            if ($normalizedInc -notmatch 'YuumiX\.Core') { $allCore = $false }
            if ($normalizedInc -notmatch 'YuumiX\.App') { $allApp = $false }
        }
        if ($allCore) {
            $normalizedScopes = @('Core')
        } elseif ($allApp) {
            $normalizedScopes = @('App')
        }
    }

    $workspaces = [ordered]@{
        Core  = 'src/YuumiX.Core/YuumiX.Core.csproj'
        App   = 'src/YuumiX.App/YuumiX.App.csproj'
        Tests = 'YuumiX.slnx'
    }

    $targets = [System.Collections.Generic.List[string]]::new()
    if ($normalizedScopes -contains 'All') {
        $targets.Add($workspaces.Core)
        $targets.Add($workspaces.App)
    } else {
        if ($normalizedScopes -contains 'Core') { $targets.Add($workspaces.Core) }
        if ($normalizedScopes -contains 'App') { $targets.Add($workspaces.App) }
        if ($normalizedScopes -contains 'Tests') { $targets.Add($workspaces.Tests) }
    }

    $previousPlatform = $env:Platform
    $failed = [System.Collections.Generic.List[string]]::new()

    try {
        # WinUI 3 工程依赖 Platform=x64 属性，通过环境变量传递给 MSBuild 上下文
        $env:Platform = 'x64'

        foreach ($target in $targets) {
            Write-Host "正在处理格式化: $target (类型: $Type, 验证模式: $Verify)..." -ForegroundColor Cyan

            $argsList = [System.Collections.Generic.List[string]]::new()

            if ($sdkCli -and (Test-Path $sdkCli)) {
                $cmd = 'dotnet'
                $argsList.Add('exec')
                $argsList.Add($sdkCli)
                $argsList.Add('format')
            } else {
                $cmd = 'dotnet'
                $argsList.Add('format')
            }

            if ($Type -ne 'all') {
                $argsList.Add($Type)
            }

            $argsList.Add($target)
            $argsList.Add('--no-restore')
            $argsList.Add('--verbosity')
            $argsList.Add('minimal')

            if ($Include -and $Include.Count -gt 0) {
                $argsList.Add('--include')
                foreach ($inc in $Include) {
                    $argsList.Add($inc)
                }
            }

            if ($Verify) {
                $argsList.Add('--verify-no-changes')
            }

            & $cmd @argsList
            if ($LASTEXITCODE -ne 0) {
                $failed.Add($target)
            }
        }
    }
    finally {
        $env:Platform = $previousPlatform
    }

    if ($failed.Count -gt 0) {
        throw "代码格式化检查未通过：$($failed -join ', ') 存在格式不符合规范的文件。请在本地运行 ./tools/Format-Code.ps1 格式化后重新提交。"
    }

    Write-Host "代码格式化检查全部通过！" -ForegroundColor Green
}
finally {
    Pop-Location
}
