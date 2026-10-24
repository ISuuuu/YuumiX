using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using YuumiX.Core.Common;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Theming;

/// <summary>
/// Yuumi 的主题令牌桥接层。
///
/// 原项目使用 CSS 变量 + 动态主题色（PersonalizationConfig.ThemeColor，默认 #009faa）。
/// Reactor 的 <see cref="Theme"/> 是只读系统令牌，无法写入自定义值，
/// 因此需要跨页面共享的自定义色集中放在这里。
///
/// 这里刻意使用 ARGB 十六进制字符串而非 Color：
///   1. Reactor 的 Background/Foreground 修饰符只接受 Brush / ThemeRef / string，没有 Color 重载；
///   2. 原配置文件中这些值本来就是字符串，后续接入配置时可直接透传，无需转换。
///
/// 注意：命名空间取名为 Theming 而非 Theme，以免与本文件使用的 <see cref="Theme"/> 静态类冲突。
/// </summary>
public static class YuumiTheme
{
    /// <summary>品牌主题色默认值，对应 PersonalizationConfig.ThemeColor 默认值。</summary>
    public const string Accent = "#FF009FAA";

    /// <summary>胜利卡片底色默认值（对应 WinCardColor 默认 #3339b01b，含 alpha）。</summary>
    public const string WinCard = "#3339B01B";

    /// <summary>失败卡片底色默认值（对应 LoseCardColor 默认 #33d3190c，含 alpha）。</summary>
    public const string LoseCard = "#33D3190C";

    /// <summary>重赛卡片底色默认值（对应 RemakeCardColor 默认 #33a2a2a2，含 alpha）。</summary>
    public const string RemakeCard = "#33A2A2A2";

    /// <summary>浅色主题下的死亡数字颜色默认值（对应 LightDeathsNumberColor 默认 #ffb60000）。</summary>
    public const string LightDeath = "#FFB60000";

    /// <summary>深色主题下的死亡数字颜色默认值（对应 DarkDeathsNumberColor 默认 #ffff8d8d）。</summary>
    public const string DarkDeath = "#FFFF8D8D";

    /// <summary>毛玻璃背景类型。原项目 PersonalizationConfig.MicaEnabled 默认为 true。</summary>
    public const BackdropKind DefaultBackdrop = BackdropKind.Mica;

    /// <summary>无配置上下文时的兜底调色板（诊断页等拿不到 Provide 的场景使用）。</summary>
    public static YuumiPalette Default { get; } = YuumiPalette.FromConfig(new PersonalizationConfig());
}

/// <summary>
/// 运行时调色板：把配置里的个性化字段翻译成 UI 可直接消费的形态（ADR-12 决策三）。
///
/// 与 <see cref="YuumiTheme"/> 的常量不同，本类型是**值**——配置一变就产生新实例，
/// 由 <see cref="ThemeState"/> 推送给订阅者。记录类型让「值相同不重渲染」成为免费行为。
///
/// 所有颜色在构造时归一化为 8 位 <c>#AARRGGBB</c>：配置里 6 位与 8 位形态混用，
/// 直接透传会得到不确定的默认不透明度（见 <see cref="ColorHex"/>）。
/// </summary>
public sealed record YuumiPalette
{
    public required string Accent { get; init; }

    public required string WinCard { get; init; }

    public required string LoseCard { get; init; }

    public required string RemakeCard { get; init; }

    /// <summary>浅色主题下的死亡数字颜色（战绩详情里每个玩家的死亡数）。</summary>
    public required string LightDeath { get; init; }

    /// <summary>深色主题下的死亡数字颜色。</summary>
    public required string DarkDeath { get; init; }

    /// <summary>原始主题模式字符串：<c>Light</c> / <c>Dark</c> / 其它（按 Auto 处理）。</summary>
    public required string ThemeMode { get; init; }

    public required bool MicaEnabled { get; init; }

    /// <summary>当前是否呈现深色模式。</summary>
    public bool IsDark => ThemeMode switch
    {
        "Dark" => true,
        "Light" => false,
        _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
    };

    // ─── 1. 主色调 Alpha 阶梯（对应原版 --primary-color-alpha-10/15/20/30/40）───
    public string AccentHover => WithAlpha(Accent, 0xD9);   // 85%
    public string AccentPressed => WithAlpha(Accent, 0xA6); // 65%
    public string AccentAlpha10 => WithAlpha(Accent, 0x1A); // 10%
    public string AccentAlpha15 => WithAlpha(Accent, 0x26); // 15%
    public string AccentAlpha20 => WithAlpha(Accent, 0x33); // 20%
    public string AccentAlpha30 => WithAlpha(Accent, 0x4D); // 30%
    public string AccentAlpha40 => WithAlpha(Accent, 0x66); // 40%

    // ─── 2. 胜/负/平局（重开）文字色、底色、悬停色、描边色 ───
    public bool IsDefaultWinCard => string.Equals(WinCard, YuumiTheme.WinCard, StringComparison.OrdinalIgnoreCase);
    public bool IsDefaultLoseCard => string.Equals(LoseCard, YuumiTheme.LoseCard, StringComparison.OrdinalIgnoreCase);
    public bool IsDefaultRemakeCard => string.Equals(RemakeCard, YuumiTheme.RemakeCard, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 胜利文字/主色。默认配置下走原版自适应翠绿色，自定义时走自定义 RGB 实色。
    /// </summary>
    public string WinText => IsDefaultWinCard
        ? (IsDark ? "#FF10B981" : "#FF065F46")
        : ToSolid(WinCard);

    /// <summary>
    /// 失败文字/主色。默认配置下走原版自适应玫瑰红，自定义时走自定义 RGB 实色。
    /// </summary>
    public string LoseText => IsDefaultLoseCard
        ? (IsDark ? "#FFF43F5E" : "#FF9F1239")
        : ToSolid(LoseCard);

    /// <summary>
    /// 重开文字/主色。默认配置下走原版自适应中性灰，自定义时走自定义 RGB 实色。
    /// </summary>
    public string RemakeText => IsDefaultRemakeCard
        ? (IsDark ? "#FF94A3B8" : "#FF64748B")
        : ToSolid(RemakeCard);

    /// <summary>兼容旧属性：胜利实色形态。</summary>
    public string WinAccent => WinText;

    /// <summary>兼容旧属性：失败实色形态。</summary>
    public string LoseAccent => LoseText;

    /// <summary>兼容旧属性：重开实色形态。</summary>
    public string RemakeAccent => RemakeText;

    /// <summary>卡片底色。</summary>
    public string WinBg => IsDefaultWinCard
        ? (IsDark ? "#2910B981" : "#3339B01B")
        : WinCard;

    public string LoseBg => IsDefaultLoseCard
        ? (IsDark ? "#29F43F5E" : "#33D3190C")
        : LoseCard;

    public string RemakeBg => IsDefaultRemakeCard
        ? "#2494A3B8"
        : RemakeCard;

    /// <summary>卡片悬停底色（Alpha 乘以 1.5）。</summary>
    public string WinBgHover => ScaleAlpha(WinBg, 1.5);
    public string LoseBgHover => ScaleAlpha(LoseBg, 1.5);
    public string RemakeBgHover => ScaleAlpha(RemakeBg, 1.5);

    /// <summary>卡片描边色（默认走原版描边，自定义走 Alpha * 2.0）。</summary>
    public string WinBorder => IsDefaultWinCard
        ? (IsDark ? "#5910B981" : "#6639B01B")
        : ScaleAlpha(WinCard, 2.0);

    public string LoseBorder => IsDefaultLoseCard
        ? (IsDark ? "#59F43F5E" : "#66D3190C")
        : ScaleAlpha(LoseCard, 2.0);

    public string RemakeBorder => IsDefaultRemakeCard
        ? "#4D94A3B8"
        : ScaleAlpha(RemakeCard, 2.0);

    // ─── 3. 死亡数字颜色（严格响应设置页的浅色/深色死亡数字配置）───
    public string DeathText => IsDark ? DarkDeath : LightDeath;

    // ─── 4. 全局表面与文本色（对齐原版水晶极光 / 暗黑海克斯主题）───
    public string PageBg => IsDark ? "#FF0B0F19" : "#FFF8FAFC";
    public string SidebarBg => IsDark
        ? (MicaEnabled ? "#D9111827" : "#FF111827")
        : (MicaEnabled ? "#BFFFFFFF" : "#FFFFFFFF");
    public string CardBg => IsDark
        ? (MicaEnabled ? "#BF1A263B" : "#FF1A263B")
        : (MicaEnabled ? "#CCFFFFFF" : "#FFFFFFFF");
    public string CardBgHover => IsDark
        ? (MicaEnabled ? "#E622324D" : "#FF22324D")
        : "#F2FFFFFF";
    public string CardSubRowBg => IsDark ? "#0AFFFFFF" : "#73FFFFFF";
    public string BorderSubtle => IsDark ? "#14FFFFFF" : "#0F000000";
    public string Separator => IsDark ? "#0DFFFFFF" : "#0A000000";
    public string HoverBg => IsDark ? "#0FFFFFFF" : "#08000000";
    public string HoverBgStrong => IsDark ? "#1AFFFFFF" : "#0F000000";

    // 三级文本色
    public string TextPrimary => IsDark ? "#FFF8FAFC" : "#FF0F172A";
    public string TextSecondary => IsDark ? "#FF94A3B8" : "#FF475569";
    public string TextMuted => IsDark ? "#FF64748B" : "#FF64748B";

    /// <summary>把配置翻译成运行时调色板。颜色统一归一化为 8 位 <c>#AARRGGBB</c>。</summary>
    /// <remarks>
    /// 强调色刻意**不**通过覆盖 WinUI 的 <c>AccentFillColorDefaultBrush</c> 来全局生效：
    /// 已实测（阶段 5 后半）在根元素上覆盖该资源对原生控件无效——开关、选中指示器仍用系统强调色。
    /// 因此约定为「谁用谁取」：消费方从 <see cref="ThemeState.Current"/> 取 <see cref="Accent"/>，
    /// 直接作为 <c>Background/Foreground</c> 的字符串传入（ADR-03）。
    /// </remarks>
    public static YuumiPalette FromConfig(PersonalizationConfig personalization)
    {
        ArgumentNullException.ThrowIfNull(personalization);

        return new YuumiPalette
        {
            Accent = ColorHex.NormalizeArgb(personalization.ThemeColor, YuumiTheme.Accent),
            WinCard = ColorHex.NormalizeArgb(personalization.WinCardColor, YuumiTheme.WinCard),
            LoseCard = ColorHex.NormalizeArgb(personalization.LoseCardColor, YuumiTheme.LoseCard),
            RemakeCard = ColorHex.NormalizeArgb(personalization.RemakeCardColor, YuumiTheme.RemakeCard),
            LightDeath = ColorHex.NormalizeArgb(personalization.LightDeathsNumberColor, YuumiTheme.LightDeath),
            DarkDeath = ColorHex.NormalizeArgb(personalization.DarkDeathsNumberColor, YuumiTheme.DarkDeath),
            ThemeMode = personalization.ThemeMode ?? "Auto",
            MicaEnabled = personalization.MicaEnabled,
        };
    }

    /// <summary>主题模式的 UI 呈现。<c>Auto</c> 映射到 <see cref="ElementTheme.Default"/>（跟随系统）。</summary>
    public ElementTheme ElementTheme => ThemeMode switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Mica 开关对应的背景类型。</summary>
    public BackdropKind Backdrop => MicaEnabled ? BackdropKind.Mica : BackdropKind.None;

    /// <summary>
    /// 去掉 8 位 <c>#AARRGGBB</c> 的 alpha。形如 <c>#33D3190C</c> → <c>#FFD3190C</c>。
    /// 输入已在 <see cref="FromConfig"/> 归一化过，这里只做长度兜底，不重复校验合法性。
    /// </summary>
    private static string ToSolid(string argb) =>
        argb.Length == 9 && argb[0] == '#' ? string.Concat("#FF", argb.AsSpan(3)) : argb;

    /// <summary>调整 ARGB 颜色的 Alpha 分量。</summary>
    public static string WithAlpha(string color, byte alpha) =>
        ColorHex.TryParseArgb(color, out _, out byte r, out byte g, out byte b)
            ? ColorHex.ToArgb(alpha, r, g, b)
            : color;

    /// <summary>按倍率调整 ARGB 颜色的 Alpha 分量（上限 255）。</summary>
    public static string ScaleAlpha(string color, double factor)
    {
        if (!ColorHex.TryParseArgb(color, out byte a, out byte r, out byte g, out byte b))
        {
            return color;
        }

        var newAlpha = (byte)Math.Clamp((int)Math.Round(a * factor), 0, 255);
        return ColorHex.ToArgb(newAlpha, r, g, b);
    }

    /// <summary>
    /// 同步 WinUI 原生资源字典强调色，使 ToggleSwitch、Checkbox 等原生控件跟随主题色。
    /// </summary>
    public static void SyncWinUiAccentResources(string accentArgb)
    {
        if (Application.Current?.Resources is not { } res) return;
        if (!ColorHex.TryParseArgb(accentArgb, out _, out byte r, out byte g, out byte b)) return;

        var color = Windows.UI.Color.FromArgb(0xFF, r, g, b);
        var hoverColor = Windows.UI.Color.FromArgb(0xD9, r, g, b);
        var pressedColor = Windows.UI.Color.FromArgb(0xA6, r, g, b);

        res["SystemAccentColor"] = color;
        res["SystemAccentColorLight1"] = color;
        res["SystemAccentColorLight2"] = color;
        res["SystemAccentColorDark1"] = color;

        SetBrushColor(res, "AccentFillColorDefaultBrush", color);
        SetBrushColor(res, "AccentFillColorSecondaryBrush", hoverColor);
        SetBrushColor(res, "AccentFillColorTertiaryBrush", pressedColor);
        SetBrushColor(res, "AccentTextFillColorPrimaryBrush", color);
    }

    private static void SetBrushColor(ResourceDictionary res, string key, Windows.UI.Color color)
    {
        if (res.TryGetValue(key, out var obj) && obj is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            res[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        }
    }
}
