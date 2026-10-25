using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>单件开启数量弹窗的请求载荷：页面持有弹窗状态渲染遮罩，确认时回传数量（R14 拆分）。</summary>
internal sealed record LootQuantityDialog(OpenableLootItem Item, int MaxQuantity, Action<int> OnConfirm);

/// <summary>
/// 战利品弹窗遮罩渲染（页面级调用）：单件数量弹窗 / 批量确认弹窗，覆盖在正文之上
///（就近遮罩，不用新窗口）。弹窗必须盖整个正文区域，因此渲染留在页面、实现集中在此。
/// </summary>
internal static class CareerLootOverlay
{
    internal static Element? Render(
        LootQuantityDialog? quantityDialog,
        int openQuantity,
        LootConfirmDialog? confirmDialog,
        YuumiPalette palette,
        Action<int> applyQuantity,
        Action<LootQuantityDialog?> applyQuantityDialog,
        Action<LootConfirmDialog?> applyConfirmDialog)
    {
        if (quantityDialog is { } dialogItem)
        {
            return LootManagerView.RenderOpenQuantityDialog(
                dialogItem.Item,
                openQuantity,
                palette,
                onQuantityChanged: applyQuantity,
                onConfirm: () =>
                {
                    applyQuantityDialog(null);
                    dialogItem.OnConfirm(openQuantity);
                },
                onCancel: () => applyQuantityDialog(null));
        }

        if (confirmDialog is { } dialog)
        {
            return LootManagerView.RenderConfirmDialog(
                dialog,
                palette,
                onCancel: () => applyConfirmDialog(null));
        }

        return null;
    }
}

/// <summary>战利品页签子组件 props。</summary>
internal sealed record CareerLootSectionProps(
    PlayerLootOverview? Loot,
    YuumiPalette Palette,
    Action OnRefresh,
    Action<LootQuantityDialog> OnRequestQuantityDialog,
    Action<LootConfirmDialog> OnRequestConfirmDialog);

/// <summary>
/// 战利品页签（自 <c>CareerPage</c> 拆出，R14）：过滤/选中交互状态、五个批量操作
///（UseMutation，R7）与操作进度提示都在本组件；确认弹窗与数量弹窗由页面统一遮罩，
/// 经 props 回调请求、回调闭包随载荷传回。
/// </summary>
internal sealed class CareerLootSection : Component<CareerLootSectionProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var careerService = services.Services.GetRequiredService<ICareerService>();

        // 战利品交互与过滤状态
        var (selectedLootIds, setSelectedLootIds) = UseState(CareerLoaders.EmptyLootSelection(), threadSafe: true);
        var (lootTypeFilter, setLootTypeFilter) = UseState(0, threadSafe: true);
        var (lootOwnedFilter, setLootOwnedFilter) = UseState(0, threadSafe: true);
        var (lootValueType, setLootValueType) = UseState(0, threadSafe: true);
        var (lootOperator, setLootOperator) = UseState(0, threadSafe: true);
        var (lootThreshold, setLootThreshold) = UseState(string.Empty, threadSafe: true);
        var (lootProgress, setLootProgress) = UseState<LootOperationProgress?>(null, threadSafe: true);
        var (lootNotice, setLootNotice) = UseState<string?>(null, threadSafe: true);

        // R7：五个批量操作全部 UseMutation 化（原 Task.Run + 手写 notice/progress）。
        // Progress 回调由框架封送回 UI 线程；成功后统一清进度、发提示、踢页面刷新。
        var openMutation = UseMutation<(OpenableLootItem Item, int Quantity), LootOperationResult>(
            mutator: (input, ct) => careerService.OpenLootAsync(
                input.Item.IsKeyFragment ? "MATERIAL_key_fragment_FORGE" : input.Item.RecipeName,
                !input.Item.IsKeyFragment && input.Item.NeedKey
                    ? new[] { input.Item.LootId, input.Item.KeyLootId ?? "MATERIAL_key" }
                    : new[] { input.Item.LootId },
                input.Quantity,
                new Progress<LootOperationProgress>(p => setLootProgress(p)),
                ct),
            options: new MutationOptions<(OpenableLootItem Item, int Quantity), LootOperationResult>(
                OnSuccess: (res, input) =>
                {
                    setLootProgress(null);
                    setLootNotice(res.Success
                        ? (input.Item.IsKeyFragment
                            ? $"合成成功：{string.Join(", ", res.Rewards)}"
                            : $"开启成功：{string.Join(", ", res.Rewards)}")
                        : (input.Item.IsKeyFragment ? $"合成失败：{res.ErrorMessage}" : $"开启失败：{res.ErrorMessage}"));
                    Props.OnRefresh();
                }));

        var smartOpenAllMutation = UseMutation<bool, LootOperationResult>(
            mutator: (_, ct) => careerService.SmartOpenAllLootsAsync(
                new Progress<LootOperationProgress>(p => setLootProgress(p)), ct),
            options: new MutationOptions<bool, LootOperationResult>(
                OnSuccess: (res, _) =>
                {
                    setLootProgress(null);
                    setLootNotice(res.Success ? $"全部开启完成：成功 {res.SucceededCount} 个" : $"开启失败：{res.ErrorMessage}");
                    Props.OnRefresh();
                }));

        var disenchantMutation = UseMutation<IReadOnlyList<(string LootId, int Count)>, LootOperationResult>(
            mutator: (items, ct) => careerService.DisenchantLootsAsync(
                items, new Progress<LootOperationProgress>(p => setLootProgress(p)), ct),
            options: new MutationOptions<IReadOnlyList<(string LootId, int Count)>, LootOperationResult>(
                OnSuccess: (res, _) =>
                {
                    setLootProgress(null);
                    setSelectedLootIds(CareerLoaders.EmptyLootSelection());
                    setLootNotice(res.Success ? $"批量分解完成：成功 {res.SucceededCount} 种" : $"分解失败：{res.ErrorMessage}");
                    Props.OnRefresh();
                }));

        var rerollMutation = UseMutation<IReadOnlyList<(string LootId, int Count)>, LootOperationResult>(
            mutator: (items, ct) => careerService.RerollLootsAsync(
                items, new Progress<LootOperationProgress>(p => setLootProgress(p)), ct),
            options: new MutationOptions<IReadOnlyList<(string LootId, int Count)>, LootOperationResult>(
                OnSuccess: (res, _) =>
                {
                    setLootProgress(null);
                    setSelectedLootIds(CareerLoaders.EmptyLootSelection());
                    setLootNotice(res.Success ? $"重铸完成：获得 {string.Join(", ", res.Rewards)}" : $"重铸失败：{res.ErrorMessage}");
                    Props.OnRefresh();
                }));

        var upgradeMutation = UseMutation<IReadOnlyList<(string LootId, int Count)>, LootOperationResult>(
            mutator: (items, ct) => careerService.UpgradeLootsAsync(
                items, new Progress<LootOperationProgress>(p => setLootProgress(p)), ct),
            options: new MutationOptions<IReadOnlyList<(string LootId, int Count)>, LootOperationResult>(
                OnSuccess: (res, _) =>
                {
                    setLootProgress(null);
                    setSelectedLootIds(CareerLoaders.EmptyLootSelection());
                    setLootNotice(res.Success ? $"升级/解锁完成：成功 {res.SucceededCount} 种" : $"升级失败：{res.ErrorMessage}");
                    Props.OnRefresh();
                }));

        // 点击卡片弹数量选择弹窗（旧版 openLootModal），初始数量 = 最大可开启数；
        // 弹窗渲染在页面遮罩层，确认时把数量传回本组件的 openMutation。
        void HandleOpenLootItem(OpenableLootItem item) =>
            Props.OnRequestQuantityDialog(new LootQuantityDialog(
                item,
                LootManagerView.MaxOpenQuantity(item),
                quantity => _ = openMutation.RunAsync((item, Math.Clamp(quantity, 1, Math.Max(LootManagerView.MaxOpenQuantity(item), 1))))));

        void HandleSmartOpenAll() => _ = smartOpenAllMutation.RunAsync(true);

        void HandleToggleSelect(string lootId)
        {
            var next = new HashSet<string>(selectedLootIds, StringComparer.Ordinal);
            if (!next.Add(lootId))
            {
                next.Remove(lootId);
            }
            setSelectedLootIds(next);
        }

        // 切换类型筛选时清空已选（裁决：防误操作分解，对齐旧版 watch(filterType)）
        void HandleTypeFilterChanged(int index)
        {
            if (index != lootTypeFilter)
            {
                setLootTypeFilter(index);
                setSelectedLootIds(CareerLoaders.EmptyLootSelection());
            }
        }

        void HandleSelectAllFiltered()
        {
            if (Props.Loot is not { } lootData)
            {
                return;
            }

            var filtered = LootManagerView.FilterInventory(
                lootData.InventoryItems,
                lootTypeFilter,
                lootOwnedFilter,
                lootValueType,
                lootOperator,
                lootThreshold);
            var next = new HashSet<string>(selectedLootIds, StringComparer.Ordinal);
            foreach (var item in filtered)
            {
                next.Add(item.LootId);
            }
            setSelectedLootIds(next);
        }

        // 批量分解（裁决）：明细行 = 所选种类数 + 预计蓝/橙精粹回报
        void HandleDisenchant()
        {
            if (Props.Loot is not { } lootData)
            {
                return;
            }

            var (items, kinds, blue, orange) = CareerLootDialogs.PlanDisenchant(lootData, selectedLootIds);
            if (items.Count == 0) return;

            Props.OnRequestConfirmDialog(CareerLootDialogs.DisenchantDialog(
                kinds, blue, orange, () => _ = disenchantMutation.RunAsync(items)));
        }

        // 重铸（裁决 A3）：按 lootId 分组、每组凑 3 的倍数、余数保留；
        // 一组都凑不齐时给「无法重随」提示，不再静默 return（裁决 D6 条目）。
        void HandleReroll()
        {
            if (Props.Loot is not { } lootData)
            {
                return;
            }

            var (items, groups, consumed, remaining) = LootManagerView.PlanReroll(lootData.InventoryItems, selectedLootIds);
            if (groups == 0)
            {
                setLootNotice("所选战利品凑不齐一组重随（每类至少 3 个）");
                return;
            }

            Props.OnRequestConfirmDialog(CareerLootDialogs.RerollDialog(
                groups, consumed, remaining, () => _ = rerollMutation.RunAsync(items)));
        }

        // 升级/解锁：前置校验（明细在 CareerLootDialogs.PlanUpgrade）不过只发提示
        void HandleUpgrade()
        {
            if (Props.Loot is not { } lootData)
            {
                return;
            }

            var plan = CareerLootDialogs.PlanUpgrade(lootData, selectedLootIds, setLootNotice);
            if (plan is null) return;

            Props.OnRequestConfirmDialog(new LootConfirmDialog(
                plan.Title,
                plan.Message,
                plan.Details,
                () => _ = upgradeMutation.RunAsync(plan.Items)));
        }

        return LootManagerView.Render(
            Props.Loot ?? PlayerLootOverview.Empty,
            Props.Palette,
            onRefresh: Props.OnRefresh,
            onOpenAll: HandleSmartOpenAll,
            onOpenItem: HandleOpenLootItem,
            typeFilterIndex: lootTypeFilter,
            onTypeFilterChanged: HandleTypeFilterChanged,
            ownedFilterIndex: lootOwnedFilter,
            onOwnedFilterChanged: setLootOwnedFilter,
            valueTypeIndex: lootValueType,
            onValueTypeChanged: setLootValueType,
            operatorIndex: lootOperator,
            onOperatorChanged: setLootOperator,
            valueThreshold: lootThreshold,
            onValueThresholdChanged: setLootThreshold,
            selectedLootIds: selectedLootIds,
            onToggleSelect: HandleToggleSelect,
            onSelectAllFiltered: HandleSelectAllFiltered,
            onClearSelection: () => setSelectedLootIds(CareerLoaders.EmptyLootSelection()),
            onDisenchant: HandleDisenchant,
            onReroll: HandleReroll,
            onUpgrade: HandleUpgrade,
            progress: lootProgress,
            operationNotice: lootNotice);
    }
}
