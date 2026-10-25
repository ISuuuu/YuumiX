using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using static YuumiX.App.Controls.SettingsCardElement;
using static YuumiX.App.Controls.SettingsExpanderElement;
using YuumiX.App.Theming;

namespace YuumiX.App.Controls;

/// <summary>
/// 设置卡片的语义状态。渲染时才映射为中文文案与语义色，
/// 禁止拿显示文案反推状态（用字符串比较判断「是否启用」的写法一律不通过）。
/// </summary>
public enum SettingsStatus
{
    Enabled,
    Disabled,
    Unconfigured,
    Connected,
    Disconnected,
    Custom,
}

/// <summary>
/// 设置页与工具箱页共用的卡片构造入口，收敛四种形态：
/// <list type="bullet">
///   <item><see cref="Card"/>：单行卡片，右侧 content 槽位放操作控件。</item>
///   <item><see cref="Toggle"/>：单开关卡片，<c>ToggleSwitch</c> 放 content 槽位。</item>
///   <item><see cref="Stepper"/>：数值卡片，复用 <see cref="CompactStepperControl"/>。</item>
///   <item><see cref="Expander"/>：展开卡片，主控件（开关）在页头右侧，子项在展开区。</item>
/// </list>
/// <para>
/// 内边距、圆角、描边、hover/pressed/focus 与展开动画一律由 Toolkit 控件提供，
/// 这里不写死任何视觉数值；字号只走 Reactor 排版工厂。
/// </para>
/// </summary>
public static class SettingsCardKit
{
    /// <summary>状态的中文文案。</summary>
    public static string StatusText(SettingsStatus status) => status switch
    {
        SettingsStatus.Enabled => "已启用",
        SettingsStatus.Disabled => "未启用",
        SettingsStatus.Unconfigured => "未配置",
        SettingsStatus.Connected => "已连接",
        SettingsStatus.Disconnected => "未连接",
        _ => "自定义",
    };

    /// <summary>状态的语义色：成功/提醒两类状态用系统语义色，未启用与未配置退为次要文本色。</summary>
    public static ThemeRef StatusColor(SettingsStatus status) => status switch
    {
        SettingsStatus.Enabled or SettingsStatus.Connected => Theme.SystemSuccess,
        SettingsStatus.Disconnected => Theme.SystemCaution,
        SettingsStatus.Disabled or SettingsStatus.Unconfigured => Theme.DisabledText,
        _ => Theme.SecondaryText,
    };

    /// <summary>状态文案元素；<paramref name="text"/> 用于 <see cref="SettingsStatus.Custom"/> 的自定义文案。</summary>
    public static Element StatusLabel(SettingsStatus status, string? text = null) =>
        Caption(text ?? StatusText(status)).Foreground(StatusColor(status));

    /// <summary>单行卡片：标题与描述交给 SettingsCard，右侧 content 槽位放操作控件。</summary>
    public static Element Card(string header, string description, Element? control = null, Element? headerIcon = null) =>
        SettingsCard(
            header: header,
            description: description,
            headerIcon: headerIcon,
            isClickEnabled: false,
            isActionIconVisible: false,
            content: control);

    /// <summary>
    /// 承载整块自定义布局（如取色器组、路径列表、关于信息）的无标题子卡片。
    /// SettingsExpander 的 ItemContainerStyleSelector 要求每个 item 必须是 SettingsCard，
    /// 否则展开实例化时会因 Style.TargetType 不匹配抛出 COMException (0x800F1000)。
    /// </summary>
    public static Element ContentCard(Element content) =>
        SettingsCard(
            contentAlignment: ContentAlignment.Left,
            isClickEnabled: false,
            isActionIconVisible: false,
            content: content.HAlign(HorizontalAlignment.Stretch))
        .HAlign(HorizontalAlignment.Stretch);

    /// <summary>开关卡片：<c>ToggleSwitch</c> 进 content 槽位，状态由开关本身表达，不再写状态文本。</summary>
    public static Element Toggle(
        string header,
        string description,
        bool value,
        Action<bool> onChanged,
        string? automationName = null) =>
        Card(
            header,
            description,
            ToggleSwitch(value, next =>
                {
                    if (next == value)
                    {
                        return;
                    }

                    onChanged(next);
                }, header: null, onContent: null, offContent: null)
                .AutomationName(automationName ?? header));

    /// <summary>数值卡片：保留产品自定义的紧凑步进器（原生 NumberBox 更笨重）。</summary>
    public static Element Stepper(
        string header,
        string description,
        int value,
        int min,
        int max,
        Action<int> onChanged,
        YuumiPalette palette,
        int step = 1,
        string? automationName = null) =>
        Card(
            header,
            description,
            CompactStepperControl.Render(
                value,
                min,
                max,
                step,
                next =>
                {
                    if (next == value)
                    {
                        return;
                    }

                    onChanged(next);
                },
                palette,
                automationName ?? header));

    /// <summary>
    /// 展开卡片。
    /// <para>
    /// <paramref name="headerControl"/> 有值（开关类卡片）时它占据页头右侧，状态不再写成文本；
    /// 为 <c>null</c>（打开面板编辑类卡片）时页头右侧只留状态副标题。
    /// </para>
    /// <para>
    /// <paramref name="innerItems"/> 是**逐项**投递给 <c>SettingsExpander</c> 的展开区内容：
    /// Toolkit 要求每个 item 必须为 <c>SettingsCard</c>，非卡片项会被自动封装进 <see cref="ContentCard"/>。
    /// 多张子卡片应当拆成多个 item 传入；如需承载整块自定义表单，可直接传入或用 <see cref="ContentCard"/> 包裹。
    /// </para>
    /// </summary>
    public static Element Expander(
        string header,
        string description,
        SettingsStatus status,
        Element? headerControl,
        Element[] innerItems,
        Element? headerIcon = null,
        string? statusText = null) =>
        SettingsExpander(
            header: header,
            description: description,
            headerIcon: headerIcon,
            items: NormalizeItems(innerItems)) with
        {
            Content = headerControl ?? StatusLabel(status, statusText),
        };

    /// <summary>
    /// 单块内容的简写：适合"一组表单行收进同一张卡片"的场景（如取色器组、路径列表）。
    /// </summary>
    public static Element Expander(
        string header,
        string description,
        SettingsStatus status,
        Element? headerControl,
        Element innerContent,
        Element? headerIcon = null,
        string? statusText = null) =>
        Expander(header, description, status, headerControl, [innerContent], headerIcon, statusText);

    private static Element[] NormalizeItems(IEnumerable<Element> items)
    {
        var normalized = new List<Element>();
        foreach (var item in items)
        {
            if (item is null or EmptyElement)
            {
                continue;
            }

            normalized.Add(item is SettingsCardElement ? item : ContentCard(item));
        }

        return [.. normalized];
    }
}
