using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using YuumiX.Core.Common;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Theming;

/// <summary>
/// Yuumi 的主题令牌桥接层。
///
/// 强调色一律跟随系统（AGENTS.md §2，走 <see cref="Theme.Accent"/> 等语义令牌）；
/// 这里只承载原项目遗留的**业务色**配置（胜负面板、死亡数字）的默认值。
///
/// 这里刻意使用 ARGB 十六进制字符串而非 Color：
///   1. Reactor 的 Background/Foreground 修饰符只接受 Brush / ThemeRef / string，没有 Color 重载；
///   2. 原配置文件中这些值本来就是字符串，后续接入配置时可直接透传，无需转换。
///
/// 注意：命名空间取名为 Theming 而非 Theme，以免与本文件使用的 <see cref="Theme"/> 静态类冲突。
/// </summary>
public static class YuumiTheme
{
    /// <summary>控件级圆角（按钮、输入框、小徽标），对齐 WinUI ControlCornerRadius。</summary>
    public const double ControlRadius = 4;

    /// <summary>容器级圆角（卡片、对话框、浮层），对齐 WinUI OverlayCornerRadius。</summary>
    public const double OverlayRadius = 8;

    /// <summary>
    /// 胶囊档圆角（头像、等级徽标、全圆胶囊）。XAML 会把超过边长一半的圆角
    /// 自动收缩为半宽/半高，因此用大值表达「全圆」语义，代替各处手写的高度一半。
    /// </summary>
    public const double PillRadius = 999;

    /// <summary>方形（无圆角）：仅用于刻意做直角的控件，如 Tab 条按钮。</summary>
    public const double SquareRadius = 0;

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

    /// <summary>
    /// 为 <see cref="ContentDialogElement"/> 设置弹窗主题。
    /// </summary>
    /// <remarks>
    /// <c>ContentDialogElement</c> 在主视觉树中只挂载一个 <c>Visibility.Collapsed</c> 的占位 <c>StackPanel</c>，
    /// 真实 <c>ContentDialog</c> 宿主在 <c>XamlRoot</c> 的 Popup 中；
    /// <c>OverlayLifecycle.ShowContentDialogCore</c> / <c>UpdateContentDialog</c> 只把 <c>Setters</c>（<c>.Set(...)</c>）
    /// 转发给真实 <c>ContentDialog</c>，而 <c>Element.Modifiers</c>（<c>.RequestedTheme(...)</c>）只作用在不可见的占位容器上。
    /// 因此弹窗主题必须通过本辅助方法走 <c>.Set(control =&gt; control.RequestedTheme = theme)</c>。
    /// </remarks>
    public static ContentDialogElement DialogTheme(this ContentDialogElement dialog, ElementTheme theme) =>
        dialog.Set(control => control.RequestedTheme = theme);
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

    /// <summary>
    /// 根元素当前实际呈现的主题（AppShell 监听 ActualThemeChanged 写回，见 <see cref="ThemeState"/>）。
    /// 仅「跟随系统」时有意义：固定模式下深浅由 <see cref="ThemeMode"/> 直接决定，保持 null。
    /// </summary>
    public ElementTheme? ActualTheme { get; init; }

    /// <summary>当前是否呈现深色模式。</summary>
    public bool IsDark => ThemeMode switch
    {
        "Dark" => true,
        "Light" => false,
        _ => ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            // 首帧尚无 ActualTheme 回报：回退到应用解析的系统主题（启动时正确，运行期由事件链覆盖）。
            _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
        },
    };

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

    /// <summary>把配置翻译成运行时调色板。颜色统一归一化为 8 位 <c>#AARRGGBB</c>。</summary>
    /// <remarks>
    /// 本调色板只承载**业务色**（胜负面板、死亡数字）；强调色一律走
    /// <see cref="Theme"/> 的系统语义令牌（<c>Theme.Accent</c> 等），不在此配置。
    /// </remarks>
    public static YuumiPalette FromConfig(PersonalizationConfig personalization, ElementTheme? actualTheme = null)
    {
        ArgumentNullException.ThrowIfNull(personalization);

        return new YuumiPalette
        {
            WinCard = ColorHex.NormalizeArgb(personalization.WinCardColor, YuumiTheme.WinCard),
            LoseCard = ColorHex.NormalizeArgb(personalization.LoseCardColor, YuumiTheme.LoseCard),
            RemakeCard = ColorHex.NormalizeArgb(personalization.RemakeCardColor, YuumiTheme.RemakeCard),
            LightDeath = ColorHex.NormalizeArgb(personalization.LightDeathsNumberColor, YuumiTheme.LightDeath),
            DarkDeath = ColorHex.NormalizeArgb(personalization.DarkDeathsNumberColor, YuumiTheme.DarkDeath),
            ThemeMode = personalization.ThemeMode ?? "Auto",
            MicaEnabled = personalization.MicaEnabled,
            ActualTheme = actualTheme,
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
}
