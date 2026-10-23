namespace YuumiX.Core.Parsers;

/// <summary>队列的展示信息。</summary>
/// <param name="Name">队列名，如"排位单双排"。</param>
/// <param name="MapName">该队列惯常所在地图名，如"召唤师峡谷"。</param>
public sealed record MatchQueueInfo(string Name, string MapName);

/// <summary>
/// 队列 / 地图 ID → 名称的映射表，对应原实现 <c>match_parser.rs</c> 的 <c>get_queue_info</c>
/// 与 <c>Search.vue</c> 里的 <c>queueNames</c> / <c>mapNames</c>。
///
/// 为什么放在契约之外、由解析器拥有：这两张表是**数据**而不是规则，改动频率远高于模型字段
/// （新队列、新模式名字调整）。放进契约模型会逼着所有分支跟着升版本；
/// 而它们又必须只有一份——原项目里列表用"队列→地图"、详情用"地图 ID→地图名"，
/// 两套表对同一场对局可能给出不同答案（例如人机 820 在列表里显示"嚎哭深渊"，
/// 详情按 mapId=12 也得到"嚎哭深渊"；但 3100 自定义局在列表里是"自定义"，详情可能拿到 mapId=11），
/// 迁移时把两套合并到本类型，详情优先按 mapId 查、查不到再回落队列惯常地图。
/// </summary>
public static class MatchQueueCatalog
{
    /// <summary>未知队列的统一展示名，与原实现 <c>_ =&gt; "自定义模式"</c> 一致。</summary>
    public const string UnknownQueueName = "自定义模式";

    /// <summary>未知地图的统一展示名，与原实现 <c>mapNames[g.mapId] || "未知地图"</c> 一致。</summary>
    public const string UnknownMapName = "未知地图";

    /// <summary>队列 ID → 队列名与惯常地图。</summary>
    public static MatchQueueInfo Resolve(int queueId) => queueId switch
    {
        // 召唤师峡谷
        400 => new("征召模式", "召唤师峡谷"),
        420 => new("排位单双排", "召唤师峡谷"),
        430 => new("匹配模式", "召唤师峡谷"),
        440 => new("排位灵活组排", "召唤师峡谷"),
        480 or 490 => new("快速模式", "召唤师峡谷"),
        // 嚎哭深渊
        450 => new("极地大乱斗", "嚎哭深渊"),
        2400 => new("海克斯大乱斗", "嚎哭深渊"),
        2450 => new("经典海斗", "嚎哭深渊"),
        // 人机
        800 or 810 or 830 or 840 or 850 => new("人机对战", "召唤师峡谷"),
        820 => new("人机对战", "嚎哭深渊"),
        // 限时 / 特殊模式
        900 => new("无限火力", "召唤师峡谷"),
        1010 => new("随机无限火力", "嚎哭深渊"),
        1020 => new("克隆模式", "召唤师峡谷"),
        1300 => new("极限闪击", "极限闪击"),
        1700 or 1710 => new("斗魂竞技场", "斗魂竞技场"),
        1810 or 1820 or 1830 or 1840 => new("捉鬼模式", "捉鬼模式"),
        4300 or 4310 => new("经典模式", "召唤师峡谷"),
        0 => new("自定义模式", "自定义"),
        _ => new(UnknownQueueName, "自定义"),
    };

    /// <summary>
    /// 地图 ID → 地图名（详情页口径）。查不到时返回 <see cref="UnknownMapName"/>。
    /// </summary>
    public static string ResolveMapName(int mapId) => mapId switch
    {
        11 => "召唤师峡谷",
        12 => "嚎哭深渊",
        21 => "极限闪击",
        22 => "对战大厅",
        30 => "斗魂竞技场",
        453 => "经典峡谷",
        _ => UnknownMapName,
    };

    /// <summary>
    /// 队列 ID → OP.GG 游戏模式标识（原 <c>queue_id_to_opgg_mode</c>），供自动选人等调用方复用。
    /// </summary>
    public static string ToOpggMode(int queueId) => queueId switch
    {
        450 or 2400 or 2450 => "aram",
        1700 or 1710 => "arena",
        1300 => "nexus_blitz",
        900 or 1900 => "urf",
        _ => "ranked",
    };
}
