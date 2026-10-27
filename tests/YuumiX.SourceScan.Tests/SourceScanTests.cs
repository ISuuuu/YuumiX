using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace YuumiX.SourceScan.Tests;

/// <summary>
/// 模式级门禁（requirements-fluent-refactor.md R17）：扫描 src/YuumiX.App 全部 .cs，
/// 违规必须被 GateWhitelist.json 精确承接。白名单是「文件 × 规则 × 违规行数」的存量清单，
/// 每个条目都挂着对应清理批次（R11/R13/R16，见 GateRules）：
///   - 白名单外出现违规 / 行数超过登记 → 红（新代码禁止新增违规）；
///   - 清理批次修掉存量后 → 把条目行数改小或删除条目，保持清单与现状一致。
/// 重新生成整份清单：设置环境变量 YUUMIX_GATE_REGENERATE_WHITELIST=1 后运行
///   dotnet test --filter FullyQualifiedName~SourceScanTests.Whitelist_RegeneratesWhenEnvSet
/// （会覆盖 tests/YuumiX.SourceScan.Tests/GateWhitelist.json，仅用于重建基线，日常勿用）。
/// </summary>
public sealed class SourceScanTests
{
    private sealed record Violation(string File, int Line, string RuleId, string Text);

    private sealed record WhitelistEntry(
        string Path,
        string Rule,
        int Count,
        string Batch);

    private sealed record WhitelistFile([property: JsonPropertyName("entries")] List<WhitelistEntry> Entries);

    // ── 门禁断言：每条规则一个 Fact，红时给出文件/行与处置指引 ──────────────────

    [Fact]
    public void Argb8_NoViolationsOutsideWhitelist() => AssertRuleConformance("Argb8");

    [Fact]
    public void FontSizeLiteral_NoViolationsOutsideWhitelist() => AssertRuleConformance("FontSizeLiteral");

    [Fact]
    public void Spacing_AllConformOrWhitelisted() => AssertRuleConformance("SpacingOff4Grid");

    [Fact]
    public void BareFluentFontFamily_NoViolationsOutsideWhitelist() => AssertRuleConformance("BareFluentFontFamily");

    [Fact]
    public void StatusTextInversion_NoViolationsOutsideWhitelist() => AssertRuleConformance("StatusTextInversion");

    private static void AssertRuleConformance(string ruleId)
    {
        var rule = GateRule.All.Single(r => r.Id == ruleId);
        var violations = ScanSource().Where(v => v.RuleId == ruleId).ToList();
        var allowed = LoadWhitelist().Where(e => e.Rule == ruleId)
            .GroupBy(e => e.Path)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));
        var actual = violations.GroupBy(v => v.File).ToDictionary(g => g.Key, g => g.Count());

        var problems = new List<string>();
        foreach (var (file, count) in actual)
        {
            if (!allowed.TryGetValue(file, out var registered))
            {
                problems.Add($"白名单外新增违规 {count} 处：{file}\n{Detail(violations, file)}");
            }
            else if (count > registered)
            {
                problems.Add($"超过白名单登记（实际 {count} > 登记 {registered}）：{file}\n{Detail(violations, file)}");
            }
        }

        foreach (var (file, registered) in allowed)
        {
            if (!actual.TryGetValue(file, out var count))
            {
                problems.Add($"存量已清零，请删除白名单条目：{file}");
            }
            else if (count < registered)
            {
                problems.Add($"存量已减少，请把登记从 {registered} 收缩到 {count}：{file}");
            }
        }

        Assert.True(problems.Count == 0,
            $"{rule.Description}（清理批次：{rule.CleanupBatch}）\n{string.Join("\n", problems)}\n" +
            $"真实违规请就地修复；确属误判请检查 GateRules 正则；存量调整请同步更新 GateWhitelist.json。");
    }

    private static string Detail(List<Violation> violations, string file) =>
        string.Join("\n", violations.Where(v => v.File == file).Select(v => $"  L{v.Line}: {v.Text}"));

    // ── 清单自身的一致性 ────────────────────────────────────────────────────────

    [Fact]
    public void Whitelist_EntriesPointToExistingFilesAndKnownRules()
    {
        var appDir = Path.Combine(FindRepoRoot(), "src", "YuumiX.App");
        var knownRules = GateRule.All.Select(r => r.Id).ToHashSet();
        var unknown = LoadWhitelist()
            .Where(e => !knownRules.Contains(e.Rule) || !File.Exists(Path.Combine(appDir, e.Path)))
            .Select(e => $"{e.Rule} → {e.Path}")
            .ToList();

        Assert.True(unknown.Count == 0, $"白名单含失效条目（文件已删或规则未知）：\n{string.Join("\n", unknown)}");
    }

    [Fact]
    public void Whitelist_NoDuplicateEntriesForSameFileAndRule()
    {
        var duplicated = LoadWhitelist()
            .GroupBy(e => (e.Path, e.Rule))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Rule} → {g.Key.Path}")
            .ToList();

        Assert.True(duplicated.Count == 0, $"同文件同规则存在重复条目（行数必须合并登记）：\n{string.Join("\n", duplicated)}");
    }

    // ── 规则自检：正则坏了先在这里红，而不是放走违规 ────────────────────────────

    [Theory]
    [InlineData("Argb8", ".Background(\"#FF115588\")", true)]
    [InlineData("Argb8", ".Background(\"#115588\")", false)]
    [InlineData("Argb8", ".Background(Theme.CardBackground)", false)]
    [InlineData("FontSizeLiteral", "TextBlock(\"x\").FontSize(13)", true)]
    [InlineData("FontSizeLiteral", "TextBlock(\"x\") with { FontSize = 13 }", true)]
    [InlineData("FontSizeLiteral", "Caption(\"x\").ApplyStyle(\"CaptionTextBlockStyle\")", false)]
    [InlineData("SpacingOff4Grid", ".Margin(0, 10, 0, 0)", true)]
    [InlineData("SpacingOff4Grid", ".Padding(3, 0.5)", true)]
    [InlineData("SpacingOff4Grid", ".Margin(0, 0, isTenCol ? 4 : 9, 0)", true)]
    [InlineData("SpacingOff4Grid", ".Margin(0, 8, 0, 16)", false)]
    [InlineData("SpacingOff4Grid", ".Padding(left: 8)", false)]
    [InlineData("SpacingOff4Grid", ".Padding(4, 2)", false)]
    [InlineData("SpacingOff4Grid", ".Padding(8, 5, 8, 6)", false)]
    [InlineData("SpacingOff4Grid", ".Margin(6, 1)", false)]
    [InlineData("SpacingOff4Grid", ".Padding(9, 0)", true)]
    [InlineData("SpacingOff4Grid", ".Spacing(6)", false)]
    [InlineData("SpacingOff4Grid", "VStack(6, blocks)", false)]
    [InlineData("SpacingOff4Grid", "VStack(6.5, blocks)", true)]
    [InlineData("SpacingOff4Grid", "VStack(10, blocks)", true)]
    [InlineData("SpacingOff4Grid", ") with { ColumnSpacing = 12 }", false)]
    [InlineData("SpacingOff4Grid", ") with { ColumnSpacing = 10 }", true)]
    [InlineData("SpacingOff4Grid", ".Margin(0, 0, 0, CardGap2)", false)]
    [InlineData("BareFluentFontFamily", ".FontFamily(\"Segoe Fluent Icons\")", true)]
    [InlineData("BareFluentFontFamily", ".FontFamily(\"Segoe Fluent Icons, Segoe MDL2 Assets\")", false)]
    [InlineData("StatusTextInversion", "if (statusText != \"未启用\")", true)]
    [InlineData("StatusTextInversion", "if (status != SettingStatus.Disabled)", false)]
    public void Rules_MatchKnownBadAndGoodLines(string ruleId, string line, bool expectViolation)
    {
        var rule = GateRule.All.Single(r => r.Id == ruleId);
        Assert.Equal(expectViolation, rule.IsViolation(StripLineComment(line)));
    }

    // ── 基线再生成（环境变量门控，见类头注释）───────────────────────────────────

    [Fact]
    public void Whitelist_RegeneratesWhenEnvSet()
    {
        if (Environment.GetEnvironmentVariable("YUUMIX_GATE_REGENERATE_WHITELIST") != "1")
        {
            return;
        }

        var entries = ScanSource()
            .GroupBy(v => (v.File, v.RuleId))
            .Select(g => new WhitelistEntry(
                g.Key.File,
                g.Key.RuleId,
                g.Count(),
                GateRule.All.Single(r => r.Id == g.Key.RuleId).CleanupBatch))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Rule, StringComparer.Ordinal)
            .ToList();
        var target = Path.Combine(FindRepoRoot(), "tests", "YuumiX.SourceScan.Tests", "GateWhitelist.json");
        File.WriteAllText(target, JsonSerializer.Serialize(new WhitelistFile(entries), JsonOptions));
    }

    // ── 扫描与基础设施 ──────────────────────────────────────────────────────────

    private static List<WhitelistEntry> LoadWhitelist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "GateWhitelist.json");
        if (!File.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<WhitelistFile>(File.ReadAllText(path), JsonOptions)!.Entries;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static List<Violation> ScanSource()
    {
        var appDir = Path.Combine(FindRepoRoot(), "src", "YuumiX.App");
        var violations = new List<Violation>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(appDir, file).Replace('\\', '/');
            var inTheming = relative.StartsWith("Theming/", StringComparison.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(file);
            foreach (var rule in GateRule.All)
            {
                if (rule.ExcludeTheming && inTheming)
                {
                    continue;
                }

                for (var i = 0; i < lines.Length; i++)
                {
                    if (rule.IsViolation(StripLineComment(lines[i])))
                    {
                        violations.Add(new Violation(relative, i + 1, rule.Id, lines[i].Trim()));
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>剥离 // 行注释后再匹配，避免文档注释里的历史举例被当作违规。</summary>
    private static string StripLineComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
            }
            else if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && dir is not null; depth++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "YuumiX.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "src", "YuumiX.App")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未定位到仓库根（按 YuumiX.slnx + src/YuumiX.App 逐级上溯）");
    }
}
