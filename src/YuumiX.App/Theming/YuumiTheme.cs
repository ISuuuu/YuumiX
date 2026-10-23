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

    /// <summary>
    /// 胜利色的**实色**形态，用于文字与 1px 描边。
    ///
    /// <see cref="WinCard"/> 是带 alpha 的底色（<c>#33…</c>），直接当文字色会得到几乎透明的字。
    /// 战绩页的「胜利 / 失败」标题、列表卡片描边、KDA 里的死亡数都需要同色系但不透明的取值，
    /// 因此统一在这里由底色派生：只把 alpha 换成 <c>FF</c>，色相与配置保持一致。
    /// </summary>
    public string WinAccent => ToSolid(WinCard);

    /// <summary>失败色的实色形态，语义同 <see cref="WinAccent"/>。</summary>
    public string LoseAccent => ToSolid(LoseCard);

    /// <summary>重开色的实色形态，语义同 <see cref="WinAccent"/>。</summary>
    public string RemakeAccent => ToSolid(RemakeCard);

    public required bool MicaEnabled { get; init; }

    /// <summary>
    /// 把配置翻译成运行时调色板。颜色统一归一化为 8 位 <c>#AARRGGBB</c>。
    /// </summary>
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
}
