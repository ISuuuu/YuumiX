namespace YuumiX.App.Theming;

/// <summary>
/// 字形与字体族集中表（R11 / AGENTS §5.14）：<c>\uE...</c> 字形字面量与图标字体族字符串
/// 只允许出现在本类，页面/控件一律引用这里的语义常量。
/// </summary>
public static class Icons
{
    /// <summary>
    /// 图标字体族。Win10 未随系统安装 Segoe Fluent Icons，只写前者会渲染成方框，
    /// 因此必须带 Segoe MDL2 Assets 回退（本串是唯一出处，禁止再写字面量）。
    /// </summary>
    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>
    /// 等宽字体族。WinUI 无 CSS 式字体回退链：CSS 关键字不是合法字体名（静默丢弃），
    /// SF Mono 也不存在于 Windows，一律走 Cascadia Mono → Consolas。
    /// </summary>
    public const string MonoFontFamily = "Cascadia Mono, Consolas";

    // —— 侧边栏导航（沿用本项目既有字形，RouteCatalog / SidebarAction 共用）——

    /// <summary>主页。</summary>
    public const string Home = "\uE80F";

    /// <summary>生涯。</summary>
    public const string Career = "\uE81E";

    /// <summary>战绩查询。</summary>
    public const string Search = "\uE721";

    /// <summary>对局信息。</summary>
    public const string GameInfo = "\uE7FC";

    /// <summary>云顶之弈。</summary>
    public const string Tft = "\uE8B4";

    /// <summary>人群：路人集入口 / 对局信息「按队伍」视图。</summary>
    public const string People = "\uE716";

    /// <summary>工具箱。</summary>
    public const string Tools = "\uE90F";

    /// <summary>设置。</summary>
    public const string Settings = "\uE713";

    /// <summary>OP.GG 独立窗口入口。</summary>
    public const string Opgg = "\uE737";

    /// <summary>循环箭头：「修复无限加载」动作项。</summary>
    public const string Refresh = "\uE72C";

    /// <summary>消息：公告入口 / 公告弹窗标题。</summary>
    public const string Notice = "\uE7E7";

    // —— 通用状态 ——

    /// <summary>对勾：成功 / 空态占位。</summary>
    public const string CheckMark = "\uE73E";

    /// <summary>错误：加载失败 / 败方 / 下载失败。</summary>
    public const string Error = "\uE783";

    /// <summary>警告：离线横幅 / 风险提示。</summary>
    public const string Warning = "\uE7BA";

    /// <summary>取消：关闭按钮 / 不可用状态。</summary>
    public const string Cancel = "\uE711";

    // —— 操作 ——

    /// <summary>播放：观看回放。</summary>
    public const string Play = "\uE768";

    /// <summary>回放：回放入口默认态。</summary>
    public const string Replay = "\uE896";

    /// <summary>左箭头：上一个。</summary>
    public const string ChevronLeft = "\uE76B";

    /// <summary>右箭头：下一个 / 收起。</summary>
    public const string ChevronRight = "\uE76C";

    /// <summary>下箭头：展开。</summary>
    public const string ChevronDown = "\uE70D";

    /// <summary>复制：对局 ID / 资料卡复制按钮。</summary>
    public const string Copy = "\uE8C8";

    /// <summary>眼睛：战绩已隐藏提示。</summary>
    public const string View = "\uE72E";

    /// <summary>人形占位：无头像时的兜底图形。</summary>
    public const string PersonPlaceholder = "\uE77B";

    /// <summary>列表：「全部战绩」视图切换。</summary>
    public const string AllMatches = "\uE80A";
}
