using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

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
    /// <summary>品牌主题色，对应 PersonalizationConfig.ThemeColor 默认值。</summary>
    public const string Accent = "#FF009FAA";

    /// <summary>胜利卡片底色（对应 WinCardColor 默认 #3339b01b，含 alpha）。</summary>
    public const string WinCard = "#3339B01B";

    /// <summary>失败卡片底色（对应 LoseCardColor 默认 #33d3190c，含 alpha）。</summary>
    public const string LoseCard = "#33D3190C";

    /// <summary>重赛卡片底色（对应 RemakeCardColor 默认 #33a2a2a2，含 alpha）。</summary>
    public const string RemakeCard = "#33A2A2A2";

    /// <summary>毛玻璃背景类型。原项目 PersonalizationConfig.MicaEnabled 默认为 true。</summary>
    public const BackdropKind DefaultBackdrop = BackdropKind.Mica;
}
