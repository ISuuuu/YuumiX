namespace YuumiX.Core.Features.Career.Models;

/// <summary>
/// 可开启的材料战利品（宝箱、法球、胶囊、钥匙碎片等），
/// 对应原版 <c>loot.rs</c> 的 <c>OpenableLoot</c>。
/// </summary>
public sealed record OpenableLootItem(
    string LootId,
    string Name,
    int Count,
    string RecipeName,
    bool NeedKey,
    int KeyCount,
    string? TilePath)
{
    /// <summary>是否为钥匙碎片（钥匙碎片走 3合1 合成配方）。</summary>
    public bool IsKeyFragment =>
        LootId.Contains("KEY_FRAGMENT", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("钥匙碎片", StringComparison.Ordinal);
}

/// <summary>
/// 碎片库存物品（英雄碎片、皮肤碎片、表情、守卫等），
/// 对应原版 <c>loot.rs</c> 的 <c>LootItem</c>。
/// </summary>
public sealed record LootInventoryItem(
    string LootId,
    string ItemDesc,
    string DisplayCategories,
    int Count,
    int Value,
    int DisenchantValue,
    string ItemStatus,
    string? TilePath)
{
    /// <summary>是否已拥有。</summary>
    public bool IsOwned => string.Equals(ItemStatus, "OWNED", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为英雄碎片。</summary>
    public bool IsChampion => string.Equals(DisplayCategories, "CHAMPION", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 战利品概览数据汇总：材料列表、碎片库存、钥匙总数与精萃余额。
/// </summary>
public sealed record PlayerLootOverview(
    IReadOnlyList<OpenableLootItem> OpenableItems,
    IReadOnlyList<LootInventoryItem> InventoryItems,
    int TotalKeys,
    int BlueEssence,
    int OrangeEssence)
{
    public static readonly PlayerLootOverview Empty = new(
        [],
        [],
        0,
        0,
        0);

    /// <summary>
    /// 与 docs/pic/生涯-战利品.png 截图完全对齐的样例数据（未连接客户端时提供高保真预览）。
    /// </summary>
    public static readonly PlayerLootOverview Sample = new(
        [
            new OpenableLootItem("CHEST_hextech", "海克斯科技...", 5, "CHEST_hextech_OPEN", true, 13, "/lol-game-data/assets/v1/champion-icons/266.png"),
            new OpenableLootItem("CHAMPION_CAPSULE", "英雄魔法引擎", 1, "CHAMPION_CAPSULE_OPEN", false, 0, "/lol-game-data/assets/v1/champion-icons/157.png"),
            new OpenableLootItem("HONOR_ORB_3", "3级荣誉法球", 1, "HONOR_ORB_3_OPEN", false, 0, "/lol-game-data/assets/v1/champion-icons/777.png"),
            new OpenableLootItem("MATERIAL_key_fragment", "钥匙碎片", 1, "MATERIAL_key_fragment_FORGE", false, 0, "/lol-game-data/assets/v1/champion-icons/238.png"),
        ],
        [
            new LootInventoryItem("CHAMPION_RENTAL_777", "封魔剑魂", "CHAMPION", 1, 6300, 1575, "OWNED", "/lol-game-data/assets/v1/champion-icons/777.png"),
            new LootInventoryItem("CHAMPION_RENTAL_157", "疾风剑豪", "CHAMPION", 1, 4800, 960, "OWNED", "/lol-game-data/assets/v1/champion-icons/157.png"),
            new LootInventoryItem("CHAMPION_RENTAL_222", "暴走萝莉", "CHAMPION", 1, 4800, 960, "NOT_OWNED", "/lol-game-data/assets/v1/champion-icons/222.png"),
            new LootInventoryItem("SKIN_RENTAL_266001", "仲裁圣骑 亚托克斯", "SKIN", 1, 6900, 1050, "NOT_OWNED", "/lol-game-data/assets/v1/champion-icons/266.png"),
        ],
        13,
        12128,
        5624);
}
