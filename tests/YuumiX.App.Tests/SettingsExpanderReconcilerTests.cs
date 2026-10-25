using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Xunit;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;

namespace YuumiX.App.Tests;

/// <summary>
/// SettingsExpander 自写调和的回归测试（R15）。
/// 决策层（复用/卸载/最小变更）是纯函数（<see cref="SettingsExpanderReconciler"/>），直接驱动；
/// 「挂载 → 展开 → 回收 → 再展开」生命周期用 <see cref="Simulator"/> 驱动多轮调和，
/// 并观测 ObservableCollection 事件——对已就位实例零触碰是展开态保住输入焦点、
/// 开关状态与展开动画的关键（ToolkitControls 自写调和的存在原因）。
/// </summary>
public sealed class SettingsExpanderReconcilerTests
{
    // ── Simulator：在纯托管环境里复刻 ToolkitControls.ReconcileItems 的驱动序列 ──────
    // 真实挂载走 MountContext.ReconcileChild（Reactor 内部协议，测试宿主无法构造），
    // 其行为契约在这里以「复用路径返回既有实例，全新挂载返回新实例」模拟。

    private sealed class Simulator
    {
        private List<SettingsExpanderReconciler.MountedItem> _state = [];

        public ObservableCollection<object> Live { get; } = [];

        /// <summary>经历过 Remove 事件的对象引用——从集合中消失即破坏交互状态。</summary>
        public HashSet<object> Removed { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>事件总数（Add/Remove/Reset）。首次挂载的 Add 是必要的，破坏性看增量。</summary>
        public int TotalEvents { get; private set; }

        public Simulator()
        {
            Live.CollectionChanged += (_, e) =>
            {
                TotalEvents++;
                if (e.Action == NotifyCollectionChangedAction.Remove)
                {
                    foreach (var old in e.OldItems!)
                        Removed.Add(old);
                }
            };
        }

        public IReadOnlyList<SettingsExpanderReconciler.MountedItem> Tick(
            IReadOnlyList<object> items, bool resetState = false)
        {
            // resetState 模拟 UnmountExpander：LiveItems 移除、Items 清空后从空状态重挂载。
            if (resetState)
            {
                _state = [];
                Live.Clear();
            }

            var plan = SettingsExpanderReconciler.Plan(_state, items);
            var next = new List<SettingsExpanderReconciler.MountedItem>(plan.Slots.Count);
            foreach (var slot in plan.Slots)
            {
                if (slot.Element is { } element)
                {
                    // ReconcileChild 契约：复用路径返回既有实例，全新挂载返回新实例。
                    var mounted = slot.Reuse is { } reuse ? reuse.Value : new object();
                    next.Add(new SettingsExpanderReconciler.MountedItem(element, mounted));
                }
                else
                    next.Add(new SettingsExpanderReconciler.MountedItem(null, slot.RawValue!));
            }

            var values = next.Select(item => item.Value).ToArray();
            SettingsExpanderReconciler.RemoveStale(Live, values);
            SettingsExpanderReconciler.SyncPositions(Live, values);

            _state = next;
            return plan.Unmounts;
        }
    }

    private static Element Item(string text, string? key = null) =>
        key is null ? TextBlock(text) : TextBlock(text).WithKey(key);

    // ── Plan：复用决策 ─────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_FreshMount_AllSlotsNew_NoUnmounts()
    {
        var plan = SettingsExpanderReconciler.Plan([], [Item("a"), Item("b")]);

        Assert.Equal(2, plan.Slots.Count);
        Assert.All(plan.Slots, slot => Assert.Null(slot.Reuse));
        Assert.Empty(plan.Unmounts);
    }

    [Fact]
    public void Plan_UnkeyedItems_ReuseBySlotPosition()
    {
        // 两轮渲染的 Element 是值相等的新 record 实例——静态项按槽位而非按值复用。
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a"), new object()),
            new(Item("b"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a"), Item("b")]);

        Assert.Same(previous[0], plan.Slots[0].Reuse);
        Assert.Same(previous[1], plan.Slots[1].Reuse);
        Assert.Empty(plan.Unmounts);
    }

    [Fact]
    public void Plan_KeyedItems_ReuseByKeyAcrossReorder()
    {
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a", "ka"), new object()),
            new(Item("b", "kb"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("b", "kb"), Item("a", "ka")]);

        Assert.Same(previous[1], plan.Slots[0].Reuse);
        Assert.Same(previous[0], plan.Slots[1].Reuse);
        Assert.Empty(plan.Unmounts);
    }

    [Fact]
    public void Plan_RemovedItems_AreUnmounted()
    {
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a"), new object()),
            new(Item("b"), new object()),
            new(Item("c"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a")]);

        var slot = Assert.Single(plan.Slots);
        Assert.Same(previous[0], slot.Reuse);
        Assert.Equal(2, plan.Unmounts.Count);
        Assert.Contains(previous[1], plan.Unmounts);
        Assert.Contains(previous[2], plan.Unmounts);
    }

    [Fact]
    public void Plan_NeverLendsStateAcrossDifferentKeys()
    {
        // 安全底线：Key 变了就是不同的项，旧项的挂载状态绝不能借给新项。
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a", "old"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a", "new")]);

        Assert.Null(plan.Slots[0].Reuse);
        Assert.Same(previous[0], Assert.Single(plan.Unmounts));
    }

    [Fact]
    public void Plan_KeyedNewItem_DoesNotStealUnkeyedSlot()
    {
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a"), new object()), // 旧槽无 Key
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a", "k")]); // 新项带 Key

        Assert.Null(plan.Slots[0].Reuse);
        Assert.Same(previous[0], Assert.Single(plan.Unmounts));
    }

    [Fact]
    public void Plan_UnkeyedNewItem_DoesNotReuseKeyedSlot()
    {
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a", "k"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a")]);

        Assert.Null(plan.Slots[0].Reuse);
        Assert.Same(previous[0], Assert.Single(plan.Unmounts));
    }

    [Fact]
    public void Plan_DuplicateKeys_FirstUnusedPreviousWins()
    {
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a", "k"), new object()),
            new(Item("a2", "k"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a", "k")]);

        Assert.Same(previous[0], plan.Slots[0].Reuse);
        Assert.Same(previous[1], Assert.Single(plan.Unmounts));
    }

    [Fact]
    public void Plan_NullEntries_AreSkipped()
    {
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(Item("a"), new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [Item("a"), null!]);

        var slot = Assert.Single(plan.Slots); // null 槽位被跳过，不产生任何决策
        Assert.Same(previous[0], slot.Reuse);
        Assert.Empty(plan.Unmounts);
    }

    [Fact]
    public void Plan_RawValues_ReuseByReference_NeverUnmounted()
    {
        var raw = new object();
        var previous = new List<SettingsExpanderReconciler.MountedItem>
        {
            new(null, raw),
            new(null, new object()),
        };

        var plan = SettingsExpanderReconciler.Plan(previous, [raw, new object()]);

        Assert.Null(plan.Slots[0].Element);
        Assert.Same(raw, plan.Slots[0].RawValue);
        Assert.Same(previous[0], plan.Slots[0].Reuse);
        // 裸值不是 Reactor 元素，没有可卸载的子树。
        Assert.Empty(plan.Unmounts);
    }

    // ── RemoveStale / SyncPositions：Items 集合的最小变更 ──────────────────────────

    [Fact]
    public void Apply_IdenticalSequence_TouchesNothing()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var sim = new Simulator();
        sim.Live.Add(a);
        sim.Live.Add(b);
        sim.Live.Add(c);
        var eventsBefore = sim.TotalEvents;

        SettingsExpanderReconciler.RemoveStale(sim.Live, [a, b, c]);
        SettingsExpanderReconciler.SyncPositions(sim.Live, [a, b, c]);

        Assert.Equal(0, sim.TotalEvents - eventsBefore); // 「仅属性变更时不触碰 Items」的直接体现
        Assert.Empty(sim.Removed);
        Assert.Equal(new object[] { a, b, c }, sim.Live);
    }

    [Fact]
    public void SyncPositions_ReordersWithMinimalMoves()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var live = new List<object> { a, b, c };

        SettingsExpanderReconciler.SyncPositions(live, [c, a, b]);

        Assert.Equal(new object[] { c, a, b }, live);
        Assert.Same(c, live[0]); // 实例保持，只是位置调整
    }

    [Fact]
    public void SyncPositions_InsertsNewItemAtSlot()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var live = new List<object> { a, c };

        SettingsExpanderReconciler.SyncPositions(live, [a, b, c]);

        Assert.Equal(new object[] { a, b, c }, live);
    }

    [Fact]
    public void RemoveStale_DropsOnlyMissingItems()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var live = new List<object> { a, b, c };

        SettingsExpanderReconciler.RemoveStale(live, [a, c]);

        Assert.Equal(new object[] { a, c }, live);
    }

    // ── 生命周期循环：挂载 → 展开 → 收起 → 回收 → 再展开 ──────────────────────────

    [Fact]
    public void Cycle_MountExpandCollapseRemount_StabilityInvariantsHold()
    {
        var sim = new Simulator();
        var slot1 = Item("a");
        var slot2 = Item("b");
        var keyed = Item("dyn", "k1");

        // 挂载：两项全新。
        var unmounts = sim.Tick([slot1, slot2]);
        Assert.Empty(unmounts);
        var firstPass = sim.Live.ToArray();

        // 展开：追加带 Key 的动态项。既有两项必须零触碰（焦点/开关状态保持）。
        var eventsBefore = sim.TotalEvents;
        unmounts = sim.Tick([slot1, slot2, keyed]);
        Assert.Empty(unmounts);
        Assert.Equal(1, sim.TotalEvents - eventsBefore); // 仅追加新项的一个 Add
        Assert.Equal(3, sim.Live.Count);
        Assert.Same(firstPass[0], sim.Live[0]);
        Assert.Same(firstPass[1], sim.Live[1]);
        Assert.DoesNotContain(firstPass[0], sim.Removed);
        Assert.DoesNotContain(firstPass[1], sim.Removed);
        var keyedMounted = sim.Live[2];

        // 收起：动态项移除，静态两项仍在位零触碰。
        eventsBefore = sim.TotalEvents;
        unmounts = sim.Tick([slot1, slot2]);
        var unmounted = Assert.Single(unmounts);
        Assert.Same(keyed, unmounted.Element);
        Assert.Same(keyedMounted, unmounted.Value);
        Assert.Equal(1, sim.TotalEvents - eventsBefore); // 仅移除项的一个 Remove
        Assert.Same(firstPass[0], sim.Live[0]);
        Assert.Same(firstPass[1], sim.Live[1]);
        Assert.Contains(keyedMounted, sim.Removed);
        Assert.DoesNotContain(firstPass[0], sim.Removed);
        Assert.DoesNotContain(firstPass[1], sim.Removed);

        // 回收（UnmountExpander 清 LiveItems）→ 再展开：从空状态重新挂载，
        // 不复用任何旧组件状态（与 Plan_NeverLendsStateAcrossDifferentKeys 同一安全底线）。
        sim.Tick([slot1], resetState: true);
        Assert.Single(sim.Live);
        Assert.NotSame(firstPass[0], sim.Live[0]);
    }

    [Fact]
    public void Cycle_KeyedItemKeepsMountedInstanceAcrossReorder()
    {
        var sim = new Simulator();
        sim.Tick([Item("a", "ka"), Item("b", "kb")]);
        var mountedA = sim.Live[0];
        var mountedB = sim.Live[1];

        // 动态列表重排（如按状态排序）：Key 命中即复用，已挂载实例跨位置保持；
        // 最小变更 = 一个 remove+insert 对（ObservableCollection 无 Move 通知时的移动实现）。
        var eventsBefore = sim.TotalEvents;
        sim.Tick([Item("b", "kb"), Item("a", "ka")]);

        Assert.Same(mountedB, sim.Live[0]);
        Assert.Same(mountedA, sim.Live[1]);
        Assert.Equal(2, sim.TotalEvents - eventsBefore);
        Assert.DoesNotContain(mountedA, sim.Removed); // 未移动的槽位零触碰
    }
}
