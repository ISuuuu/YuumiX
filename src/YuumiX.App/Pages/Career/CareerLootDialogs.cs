using YuumiX.Core.Features.Career.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>战利品批量操作确认弹窗的明细计算与构造（自 <c>CareerPage</c> 拆出，R14 纯函数）。</summary>
internal static class CareerLootDialogs
{
    /// <summary>升级/解锁计划：通过前置校验时返回明细；校验不过时经 <paramref name="reject"/> 发拒绝文案并返回 null。</summary>
    internal sealed record UpgradePlan(
        IReadOnlyList<(string LootId, int Count)> Items,
        string Title,
        string Message,
        IReadOnlyList<(string Label, string Value)> Details);

    /// <summary>
    /// 升级前置校验（对齐旧版 useLoot.ts:592-697）：
    /// 无未拥有项 → 提示中止；SKIN 未拥有且对应英雄也未拥有 → 提示名单中止；
    /// 总费用（英雄→蓝、其他→橙，单价×数量）超过余额 → 提示「需要 N / 拥有 M」中止。
    /// </summary>
    internal static UpgradePlan? PlanUpgrade(
        PlayerLootOverview loot,
        IReadOnlySet<string> selectedLootIds,
        Action<string> reject)
    {
        var unowned = loot.InventoryItems
            .Where(i => selectedLootIds.Contains(i.LootId) && !i.IsOwned)
            .ToList();

        if (unowned.Count == 0)
        {
            reject("没有选中任何未拥有的战利品进行操作！");
            return null;
        }

        var missingChampionSkins = unowned
            .Where(i => string.Equals(i.DisplayCategories, "SKIN", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(i.ParentItemStatus, "OWNED", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (missingChampionSkins.Count > 0)
        {
            string names = string.Join("、", missingChampionSkins.Select(i => i.ItemDesc));
            reject($"未拥有以下皮肤对应的英雄：[{names}]，请先在游戏内解锁或购买该英雄！");
            return null;
        }

        int totalBlue = unowned
            .Where(i => i.UpgradeEssenceCost > 0 && i.IsChampion)
            .Sum(i => i.UpgradeEssenceCost * i.Count);
        int totalOrange = unowned
            .Where(i => i.UpgradeEssenceCost > 0 && !i.IsChampion)
            .Sum(i => i.UpgradeEssenceCost * i.Count);

        if (totalBlue > loot.BlueEssence)
        {
            reject($"蓝色精粹不足！升级需要 🔷 {totalBlue}，当前拥有 🔷 {loot.BlueEssence}");
            return null;
        }

        if (totalOrange > loot.OrangeEssence)
        {
            reject($"橙色精粹不足！升级需要 🔶 {totalOrange}，当前拥有 🔶 {loot.OrangeEssence}");
            return null;
        }

        bool hasUpgrade = unowned.Any(i => i.UpgradeEssenceCost > 0);
        bool hasUnlock = unowned.Any(i => i.UpgradeEssenceCost == 0);
        string description = hasUpgrade && hasUnlock
            ? "消耗精粹升级部分碎片，其余永久道具将免费解锁"
            : hasUpgrade
                ? "将消耗对应精粹并解锁为永久版"
                : "将免费解锁/激活永久道具并添加到收藏";
        string title = hasUpgrade && !hasUnlock ? "升级选中项" : hasUnlock && !hasUpgrade ? "解锁选中项" : "升级 / 解锁选中项";

        var details = new List<(string, string)> { ("待处理战利品种类", $"{unowned.Count} 种") };
        if (totalBlue > 0)
        {
            details.Add(("预计消耗蓝色精粹", $"🔷 {totalBlue}"));
        }
        if (totalOrange > 0)
        {
            details.Add(("预计消耗橙色精粹", $"🔶 {totalOrange}"));
        }
        details.Add(("操作说明", description));

        return new UpgradePlan(
            unowned.Select(i => (i.LootId, i.Count)).ToList(),
            title,
            $"您确定要将选中的 {unowned.Count} 个未拥有项目进行升级或解锁吗？",
            details);
    }

    /// <summary>分解计划：所选种数 + 预计蓝/橙精粹回报。</summary>
    internal static (IReadOnlyList<(string LootId, int Count)> Items, int Kinds, int Blue, int Orange) PlanDisenchant(
        PlayerLootOverview loot,
        IReadOnlySet<string> selectedLootIds)
    {
        var items = loot.InventoryItems
            .Where(i => selectedLootIds.Contains(i.LootId))
            .Select(i => (i.LootId, i.Count))
            .ToList();

        int blue = loot.InventoryItems
            .Where(i => selectedLootIds.Contains(i.LootId) && i.IsChampion)
            .Sum(i => i.DisenchantValue * i.Count);
        int orange = loot.InventoryItems
            .Where(i => selectedLootIds.Contains(i.LootId) && !i.IsChampion)
            .Sum(i => i.DisenchantValue * i.Count);

        return (items, items.Count, blue, orange);
    }

    /// <summary>批量分解确认弹窗（裁决：明细行 = 所选种类数 + 预计蓝/橙精粹回报）。</summary>
    internal static LootConfirmDialog DisenchantDialog(int kinds, int blue, int orange, Action onConfirm) => new(
        "分解选中项",
        $"您确定要分解当前选中的 {kinds} 种碎片吗？此操作无法撤销。",
        new (string, string)[]
        {
            ("所选碎片种类", $"{kinds} 种"),
            ("🔷 预计蓝色精粹回报", $"+ {blue}"),
            ("🔶 预计橙色精粹回报", $"+ {orange}"),
        },
        onConfirm);

    /// <summary>重铸确认弹窗（裁决 A3）：按组数/消耗/保留构造。</summary>
    internal static LootConfirmDialog RerollDialog(int groups, int consumed, int remaining, Action onConfirm) => new(
        "重铸选中项",
        "您确定要将选中的碎片进行三合一重随吗？系统将按同类 3 个一组进行重随，余数保留。",
        new (string, string)[]
        {
            ("重随合成组数", $"{groups} 组"),
            ("消耗碎片总数", $"{consumed} 个"),
            ("保留未配对数", $"{remaining} 个"),
        },
        onConfirm);
}
