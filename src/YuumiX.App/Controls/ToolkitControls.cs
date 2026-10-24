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

    private sealed record MountedItem(Element? Element, object Value);

    private sealed class MountedItems
    {
        public List<MountedItem> Items { get; set; } = [];
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

        var previous = state.Items;
        var used = new HashSet<MountedItem>(ReferenceEqualityComparer.Instance);
        var next = new List<MountedItem>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is Element element)
            {
                // 带 Key 的动态项按 Key 复用；静态项按槽位复用。
                // 绝不把已移除项的组件状态借给另一项。
                var existing = element.Key is { } key
                    ? previous.FirstOrDefault(item => !used.Contains(item) && item.Element?.Key == key)
                    : index < previous.Count && previous[index].Element is { Key: null }
                        && !used.Contains(previous[index]) ? previous[index] : null;
                if (existing is not null)
                    used.Add(existing);

                var mounted = context.ReconcileChild(existing?.Element, element, existing?.Value as UIElement);
                if (mounted is not null)
                    next.Add(new(element, mounted));
            }
            else if (items[index] is { } value)
            {
                var existing = previous.FirstOrDefault(item => !used.Contains(item)
                    && item.Element is null && ReferenceEquals(item.Value, value));
                if (existing is not null)
                    used.Add(existing);
                next.Add(new(null, value));
            }
        }

        // 仅属性变更时不触碰 Items，以保留实例化状态、输入焦点、指针处理器与动画播放器。
        for (var index = control.Items.Count - 1; index >= 0; index--)
        {
            if (!next.Any(item => ReferenceEquals(item.Value, control.Items[index])))
                control.Items.RemoveAt(index);
        }

        foreach (var removed in previous.Where(item => !used.Contains(item)))
        {
            if (removed.Element is not null && removed.Value is UIElement child)
                context.ReconcileChild(removed.Element, null, child);
        }

        for (var index = 0; index < next.Count; index++)
        {
            var value = next[index].Value;
            if (index < control.Items.Count && ReferenceEquals(control.Items[index], value))
                continue;
            var oldIndex = control.Items.IndexOf(value);
            if (oldIndex >= 0)
                control.Items.RemoveAt(oldIndex);
            control.Items.Insert(index, value);
        }

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
