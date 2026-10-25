using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;

// SettingsExpander 把 Items 转发给内部原生 ItemsRepeater，这个间接边界对 CsWinRT 的
// 调用点探测不可见，需显式生成向量/集合通知 ABI（Native AOT）。
[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(ObservableCollection<object>))]

namespace YuumiX.App.Controls;

/// <summary>
/// CommunityToolkit SettingsCard 的 Reactor 包装。
/// 设置项的标准容器：左侧标题/描述，右侧 content 槽位，内置 hover/pressed/focus/描边/圆角。
/// </summary>
[GenerateReactorWrapper(typeof(SettingsCard), RegisterAssembly = false)]
[WrapElementSlot("HeaderIcon")]
public partial record SettingsCardElement;

/// <summary>
/// CommunityToolkit SettingsExpander 的 Reactor 包装。
/// <list type="bullet">
///   <item><c>content</c>：页头右侧的主控件（开关等），**不再**用静态状态文本占位。</item>
///   <item><c>items</c>：展开后的子卡片集合，由 <see cref="SettingsExpanderElement.ReconcileItems"/> 逐项调和。</item>
///   <item><c>Content</c>：页头区域的自定义内容（与 content 槽位互斥，按需取用）。</item>
/// </list>
/// </summary>
[GenerateReactorWrapper(typeof(SettingsExpander), RegisterAssembly = false)]
[WrapElementSlot("HeaderIcon")]
[WrapManual("Content")]
public partial record SettingsExpanderElement
{
    public Element? Content { get; init; }

    public static SettingsExpanderElement SettingsExpander(
        Element? content,
        Element? headerIcon = null,
        params object[] items) =>
        SettingsExpander(headerIcon: headerIcon, items: items) with { Content = content };

    // Items 是子项的唯一宿主：生成的扁平 ItemsHost 会在每次渲染时整批重建，
    // 导致展开态里的输入框失焦、开关回弹、动画中断。这里改为按元素身份增量调和。
    private static partial ControlDescriptor<SettingsExpanderElement, SettingsExpander> Customize(
        ControlDescriptor<SettingsExpanderElement, SettingsExpander> d)
    {
        d.Children = new Imperative<SettingsExpanderElement, SettingsExpander>(
            static (context, _, element, control) => ReconcileItems(context, control, element.Items));
        return d.ImperativeBridged(
            mount: static (context, control, element) =>
            {
                if (element.Content is not null)
                    control.Content = context.MountChild(element.Content)!;
            },
            update: static (context, control, oldElement, newElement) =>
            {
                if (oldElement.Content is null && newElement.Content is null)
                    return;

                var existing = control.Content as UIElement;
                var next = context.ReconcileChild(oldElement.Content, newElement.Content, existing);
                if (!ReferenceEquals(existing, next))
                    control.Content = next!;
            }).WithUnmount(UnmountExpander);
    }

    private sealed class MountedItems
    {
        public List<SettingsExpanderReconciler.MountedItem> Items { get; set; } = [];
    }

    private static readonly ConditionalWeakTable<SettingsExpander, MountedItems> LiveItems = new();

    private static void ReconcileItems(MountContext context, SettingsExpander control, IReadOnlyList<object> items)
    {
        if (!LiveItems.TryGetValue(control, out var state))
        {
            state = new MountedItems();
            LiveItems.Add(control, state);
            // Toolkit 默认的 Items 是 List<object>，模板创建后其 ItemsRepeater 观察不到增删。
            control.Items = new ObservableCollection<object>();
        }

        // 复用/卸载/最小变更的决策逻辑在 SettingsExpanderReconciler（纯函数，可单测）。
        var plan = SettingsExpanderReconciler.Plan(state.Items, items);
        var next = new List<SettingsExpanderReconciler.MountedItem>(plan.Slots.Count);
        foreach (var slot in plan.Slots)
        {
            if (slot.Element is { } element)
            {
                var mounted = context.ReconcileChild(slot.Reuse?.Element, element, slot.Reuse?.Value as UIElement);
                if (mounted is not null)
                    next.Add(new(element, mounted));
            }
            else
                next.Add(new(null, slot.RawValue!));
        }

        // 仅属性变更时不触碰 Items，以保留实例化状态、输入焦点、指针处理器与动画播放器。
        var values = next.Select(item => item.Value).ToArray();
        SettingsExpanderReconciler.RemoveStale(control.Items, values);
        foreach (var removed in plan.Unmounts)
        {
            if (removed.Value is UIElement child)
                context.ReconcileChild(removed.Element!, null, child);
        }
        SettingsExpanderReconciler.SyncPositions(control.Items, values);

        state.Items = next;
    }

    private static void UnmountExpander(in UnmountContext context, SettingsExpander control)
    {
        if (LiveItems.TryGetValue(control, out var state))
        {
            foreach (var item in state.Items)
            {
                if (item.Element is not null && item.Value is UIElement child)
                    context.Reconciler.UnmountChild(child);
            }
            LiveItems.Remove(control);
        }

        control.Items.Clear();
        if (control.Content is UIElement content)
            context.Reconciler.UnmountChild(content);
        if (control.HeaderIcon is UIElement icon)
            context.Reconciler.UnmountChild(icon);
    }
}
