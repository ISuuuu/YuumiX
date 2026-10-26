using System.Text.RegularExpressions;

namespace YuumiX.SourceScan.Tests;

/// <summary>
/// 模式级 UI 规范门禁规则（AGENTS.md §1/§2/§3，docs/requirements-fluent-refactor.md R17）。
/// 符号级禁用（ScrollView、FontWeights、ContentDialog.ShowAsync、SyncWinUiAccentResources）
/// 由 BannedApiAnalyzers + BannedSymbols.txt 在构建期拦截，这里只负责"写法模式"类违规。
/// <see cref="CleanupBatch"/> 标注各规则存量的对应清理批次（对接 R11/R13/R16）。
/// </summary>
internal sealed record GateRule(
    string Id,
    string Description,
    string CleanupBatch,
    bool ExcludeTheming,
    Func<string, bool> IsViolation)
{
    // 正则字段必须声明在 All 之前：静态初始化按声明顺序进行，顺序写反会触发 CS8602
    // 字符串字面量形态的 8 位 ARGB（#AARRGGBB），与 R13 验收 grep 同口径
    private static readonly Regex Argb8Pattern = new("\"#[0-9A-Fa-f]{8}\"", RegexOptions.Compiled);

    // 与 §0 grep 同口径并覆盖属性赋值形态：FontSize(13) 与 with { FontSize = 13 }
    // 都是裸字号字面量（FontSize(x) 变量形态不拦）
    private static readonly Regex FontSizePattern = new("FontSize\\s*(\\(|=)\\s*[0-9]", RegexOptions.Compiled);

    // 整串就是 "Segoe Fluent Icons"（带收尾引号），带 MDL2 回退的长串天然不命中
    private static readonly Regex BareFluentFontPattern = new("\"Segoe Fluent Icons\"", RegexOptions.Compiled);

    private static readonly Regex StatusTextPattern = new("statusText != \"", RegexOptions.Compiled);

    public static IReadOnlyList<GateRule> All { get; } =
    [
        new(
            "Argb8",
            "8 位 ARGB 硬编码颜色：高对比度模式下失效并盖住 Mica（AGENTS.md §2）",
            CleanupBatch: "R13",
            ExcludeTheming: true,
            line => Argb8Pattern.IsMatch(line)),

        new(
            "FontSizeLiteral",
            "裸字号字面量：字号一律走排版工厂（AGENTS.md §1）",
            CleanupBatch: "无存量，纯拦截",
            ExcludeTheming: false,
            line => FontSizePattern.IsMatch(line)),

        new(
            "SpacingOff4Grid",
            "间距不在 4px 网格上（栈内 <8px 微间距除外）：工厂参数、with 赋值与 HStack/VStack 首参都必须是 4 的倍数（AGENTS.md §3）",
            CleanupBatch: "R16",
            ExcludeTheming: false,
            IsSpacingOffGrid),

        new(
            "BareFluentFontFamily",
            "裸字形字体名：缺 Segoe MDL2 Assets 回退，Win10 渲染成方框（AGENTS.md §5.14）",
            CleanupBatch: "R11",
            ExcludeTheming: false,
            line => BareFluentFontPattern.IsMatch(line)),

        new(
            "StatusTextInversion",
            "从显示文案反推状态：状态必须用强类型枚举（AGENTS.md §4）",
            CleanupBatch: "无存量，纯拦截",
            ExcludeTheming: false,
            line => StatusTextPattern.IsMatch(line)),
    ];

    // 工厂调用形态：.Padding(16, 8) / Margin(0, 0, 0, 4)
    private static readonly Regex SpacingCallPattern =
        new("\\b(Spacing|Padding|Margin|RowSpacing|ColumnSpacing)\\s*\\(", RegexOptions.Compiled);

    // with 赋值形态：with { ColumnSpacing = 16 }
    private static readonly Regex SpacingAssignPattern =
        new("\\b(Spacing|Padding|Margin|RowSpacing|ColumnSpacing)\\s*=\\s*(?<value>-?[0-9]+(\\.[0-9]+)?)",
            RegexOptions.Compiled);

    // 栈间距形态：HStack(12, ...) / VStack(spacing: 8, ...)
    private static readonly Regex StackGapPattern =
        new("\\b(HStack|VStack)\\s*\\(\\s*(?:spacing\\s*:\\s*)?(?<value>-?[0-9]+(\\.[0-9]+)?)",
            RegexOptions.Compiled);

    // 数字字面量：前后不吃标识符字符，避免把 CardGap2 里的 2 当间距
    private static readonly Regex NumberPattern =
        new("(?<![\\w.])-?[0-9]+(\\.[0-9]+)?(?![\\w.])", RegexOptions.Compiled);

    private static bool IsSpacingOffGrid(string line)
    {
        foreach (Match call in SpacingCallPattern.Matches(line))
        {
            if (HasOffGridNumber(ExtractCallArgs(line, call.Index + call.Length - 1)))
            {
                return true;
            }
        }

        foreach (Match assign in SpacingAssignPattern.Matches(line))
        {
            if (IsOffGrid(assign.Groups["value"].Value))
            {
                return true;
            }
        }

        foreach (Match stack in StackGapPattern.Matches(line))
        {
            if (!IsAllowedStackGap(stack.Groups["value"].Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>从 '(' 起做括号配平取实参段；跨行调用只扫本行可见部分（当前仓库间距调用均单行成句）。</summary>
    private static string ExtractCallArgs(string line, int openParenIndex)
    {
        var depth = 0;
        for (var i = openParenIndex; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return line[(openParenIndex + 1)..i];
                    }

                    break;
            }
        }

        return line[(openParenIndex + 1)..];
    }

    private static bool HasOffGridNumber(string text)
    {
        foreach (Match number in NumberPattern.Matches(text))
        {
            if (IsOffGrid(number.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOffGrid(string value) =>
        Math.Abs(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)) % 4 > 1e-9;

    // 栈内 <8px 是图标与文字的微对齐值，不参与 4 网格；≥8px 与布局间距同口径。
    private static bool IsAllowedStackGap(string value)
    {
        var gap = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        return gap % 4 == 0 || (gap >= 1 && gap <= 8);
    }
}
