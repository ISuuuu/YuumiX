namespace YuumiX.Core.Features.Match.Models;

/// <summary>
/// 单局详情：10 名玩家 + 两支队伍，对应原项目由 <c>Search.vue</c> 的 <c>gameDetails</c>
/// 计算属性产出的 <c>GameDetail</c>。
///
/// 与原实现的差别只有一处，但很关键：<b>原版是前端拿 LCU 原始响应自己拼</b>
/// （<c>/lol-match-history/v1/games/{gameId}</c> 的 <c>participants</c> +
/// <c>participantIdentities</c> 按 <c>participantId</c> 关联，再按 <c>teamId</c> 分蓝红），
/// YuumiX 把这段拼装下沉到 Core 的 <c>MatchParser</c>，界面拿到的是已经分好队的结构。
/// 理由：关联逻辑（身份缺失时回落"未知"、按 100/200 分队、队伍击杀数求和）是纯函数，
/// 放在 Vue 里既无法单测也无法被其他页面复用；而它最容易出错的地方正是
/// <c>participantIdentities</c> 与 <c>participants</c> 长度不一致的稀疏载荷。
/// </summary>
public sealed record MatchDetail
{
    /// <summary>对局 ID。</summary>
    public required long GameId { get; init; }

    /// <summary>队列 ID。</summary>
    public required int QueueId { get; init; }

    /// <summary>地图 ID（11 召唤师峡谷 / 12 嚎哭深渊 / 30 斗魂竞技场）。</summary>
    public required int MapId { get; init; }

    /// <summary>队列名。</summary>
    public required string QueueName { get; init; }

    /// <summary>地图名。</summary>
    public required string MapName { get; init; }

    /// <summary>开局时间，Unix 毫秒。</summary>
    public required long GameCreation { get; init; }

    /// <summary>对局时长（秒）。</summary>
    public required int DurationSeconds { get; init; }

    /// <summary>蓝方（<c>teamId = 100</c>）。</summary>
    public required MatchDetailTeam Blue { get; init; }

    /// <summary>红方（<c>teamId = 200</c>）。</summary>
    public required MatchDetailTeam Red { get; init; }

    /// <summary>
    /// 被查询玩家的胜负。<c>null</c> 表示请求未携带 puuid，或该 puuid 不在本局 10 人内
    /// （观战/他人战绩分享等场景会出现）。用可空布尔而不是默认 <c>false</c>：
    /// 把"不知道"渲染成"失败"是详情页最容易被用户当真的错误。
    /// </summary>
    public bool? QueriedPlayerWin { get; init; }

    /// <summary>开局时间文本 <c>yyyy-MM-dd HH:mm</c>（UTC，见 <see cref="MatchTimeText"/>）。</summary>
    public string DateText =>
        MatchTimeText.FromUnixMilliseconds(GameCreation, "yyyy-MM-dd HH:mm", "1970-01-01 00:00");

    /// <summary>时长文本 <c>mm:ss</c>。</summary>
    public string DurationText => MatchTimeText.FromSeconds(DurationSeconds);
}

/// <summary>详情页的单支队伍。</summary>
public sealed record MatchDetailTeam
{
    /// <summary>队伍 ID：<c>100</c> 蓝方，<c>200</c> 红方。</summary>
    public required int TeamId { get; init; }

    /// <summary>该队玩家，保持 LCU 下发顺序。</summary>
    public required IReadOnlyList<MatchDetailPlayer> Players { get; init; }

    /// <summary>队伍总击杀（由玩家击杀求和，不用 LCU 的 teamStats 口径）。</summary>
    public required int Kills { get; init; }

    /// <summary>该队是否获胜。</summary>
    public required bool Win { get; init; }

    /// <summary>推塔数。</summary>
    public int TowerKills { get; init; }

    /// <summary>推水晶数。</summary>
    public int InhibitorKills { get; init; }

    /// <summary>男爵击杀数。</summary>
    public int BaronKills { get; init; }

    /// <summary>小龙击杀数。</summary>
    public int DragonKills { get; init; }

    /// <summary>峡谷先锋击杀数。</summary>
    public int RiftHeraldKills { get; init; }
}

/// <summary>详情页的单名玩家。</summary>
public sealed record MatchDetailPlayer
{
    /// <summary>本局内的参与者序号，用于关联 <c>participantIdentities</c>。</summary>
    public required int ParticipantId { get; init; }

    /// <summary>队伍 ID。</summary>
    public required int TeamId { get; init; }

    /// <summary>英雄 ID。</summary>
    public required int ChampionId { get; init; }

    /// <summary>召唤师技能 1（ID）。</summary>
    public required int Spell1Id { get; init; }

    /// <summary>召唤师技能 2（ID）。</summary>
    public required int Spell2Id { get; init; }

    /// <summary>
    /// 显示名。有 <c>tagLine</c> 时为 <c>gameName#tagLine</c>，否则退回
    /// <c>gameName</c> / <c>summonerName</c>，两者都缺时写作"未知"（与原实现一致）。
    /// </summary>
    public required string Name { get; init; }

    /// <summary>玩家 puuid。身份段缺失时为空字符串——界面据此判断能否点进玩家页。</summary>
    public required string Puuid { get; init; }

    /// <summary>召唤师 ID。身份段缺失时为 <c>0</c>。</summary>
    public required long SummonerId { get; init; }

    /// <summary>英雄等级。</summary>
    public required int ChampionLevel { get; init; }

    /// <summary>击杀。</summary>
    public required int Kills { get; init; }

    /// <summary>死亡。</summary>
    public required int Deaths { get; init; }

    /// <summary>助攻。</summary>
    public required int Assists { get; init; }

    /// <summary>补刀数（小兵 + 野怪）。</summary>
    public required int CreepScore { get; init; }

    /// <summary>获得金币。</summary>
    public required int Gold { get; init; }

    /// <summary>对英雄造成的伤害。</summary>
    public required int DamageToChampions { get; init; }

    /// <summary>装备栏 7 格，未出装为 <c>0</c>；索引 6 为守卫。</summary>
    public required IReadOnlyList<int> ItemIds { get; init; }

    /// <summary>基石符文 ID。</summary>
    public required int RuneId { get; init; }

    /// <summary>该玩家是否获胜。</summary>
    public required bool Win { get; init; }

    /// <summary>海克斯强化 ID（已去重、最多 5 个）。</summary>
    public required IReadOnlyList<int> AugmentIds { get; init; }

    /// <summary>段位（如 <c>GOLD</c>）。后台渐进加载，未加载完成时为 <c>null</c>。</summary>
    public string? Tier { get; init; }

    /// <summary>段位显示文本（如 <c>黄金 II</c>）。与 <see cref="Tier"/> 同生命周期。</summary>
    public string? RankText { get; init; }
}
