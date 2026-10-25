using Microsoft.UI.Reactor.Core;

namespace YuumiX.App.Controls;

/// <summary>
/// SettingsExpander Items 增量调和的纯决策层（R15 回归保护）。
/// 不依赖 XAML 运行时与 Reactor 挂载上下文，可在测试宿主中直接单测；
/// 挂载/卸载等副作用（ReconcileChild、ObservableCollection 通知）由 ToolkitControls 的调用方执行。
/// 升级 Reactor 时的回归锚点，见 docs/architecture.md 的 SettingsExpander 自写调和 ADR。
/// </summary>
internal static class SettingsExpanderReconciler
{
    /// <summary>已挂载项。Element 为 null 表示裸值（非 Reactor 元素直传进 Items）。</summary>
    internal sealed record MountedItem(Element? Element, object Value);

    /// <summary>单个新槽位的决策：Reuse 指向被复用的旧项（null = 全新挂载）；Element 为 null 表示裸值槽。</summary>
    internal sealed record SlotPlan(Element? Element, object? RawValue, MountedItem? Reuse);

    /// <summary>一轮调和的完整决策：新槽位序列 + 需卸载的旧项（未复用且为 Reactor 元素）。</summary>
    internal sealed record ReconcilePlan(IReadOnlyList<SlotPlan> Slots, IReadOnlyList<MountedItem> Unmounts);

    /// <summary>
    /// 决策每个新槽位复用哪个旧项：
    /// 带 Key 的动态项按 Key 复用（与位置无关）；静态项按槽位复用（旧槽必须同样无 Key）；
    /// 裸值按引用复用。绝不把已移除项的组件状态借给另一项——Key 不同即视为不同项。
    /// </summary>
    internal static ReconcilePlan Plan(IReadOnlyList<MountedItem> previous, IReadOnlyList<object> items)
    {
        var used = new HashSet<MountedItem>(ReferenceEqualityComparer.Instance);
        var slots = new List<SlotPlan>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is Element element)
            {
                var existing = element.Key is { } key
                    ? previous.FirstOrDefault(item => !used.Contains(item) && item.Element?.Key == key)
                    : index < previous.Count && previous[index].Element is { Key: null }
                        && !used.Contains(previous[index]) ? previous[index] : null;
                if (existing is not null)
                    used.Add(existing);
                slots.Add(new SlotPlan(element, null, existing));
            }
            else if (items[index] is { } value)
            {
                var existing = previous.FirstOrDefault(item => !used.Contains(item)
                    && item.Element is null && ReferenceEquals(item.Value, value));
                if (existing is not null)
                    used.Add(existing);
                slots.Add(new SlotPlan(null, value, existing));
            }
        }

        return new ReconcilePlan(
            slots,
            previous.Where(item => !used.Contains(item) && item.Element is not null).ToArray());
    }

    /// <summary>倒序移除 liveItems 中 desired 里不再存在的项。desired 之外的存活项不被触碰。</summary>
    internal static void RemoveStale(IList<object> liveItems, IReadOnlyList<object> desired)
    {
        for (var index = liveItems.Count - 1; index >= 0; index--)
        {
            if (!desired.Any(item => ReferenceEquals(item, liveItems[index])))
                liveItems.RemoveAt(index);
        }
    }

    /// <summary>
    /// 逐槽位把 liveItems 调整为 desired 的顺序与内容（最小 remove+insert 变更）。
    /// 已在目标位置的实例原位不动——这是「仅属性变更时不触碰 Items」的基石，
    /// 展开态里输入框焦点、开关状态、动画播放器全靠它保住。
    /// </summary>
    internal static void SyncPositions(IList<object> liveItems, IReadOnlyList<object> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var value = desired[index];
            if (index < liveItems.Count && ReferenceEquals(liveItems[index], value))
                continue;
            var oldIndex = liveItems.IndexOf(value);
            if (oldIndex >= 0)
                liveItems.RemoveAt(oldIndex);
            liveItems.Insert(index, value);
        }
    }
}
