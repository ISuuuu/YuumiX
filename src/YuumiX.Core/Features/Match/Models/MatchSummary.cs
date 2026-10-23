using System.Globalization;

namespace YuumiX.Core.Features.Match.Models;

/// <summary>
/// 战绩列表项——搜索页列表的渲染单元，也是原项目 <c>MatchDisplay</c>（<c>match_parser.rs</c>）的 C# 形态。
///
/// 两条设计边界：
/// <list type="number">
///   <item><b>只放 ID，不放图标 URL。</b>原 <c>MatchDisplay</c> 里有一整套
///         <c>championIconUrl</c> / <c>spellXIconUrl</c> / <c>itemIconUrls</c> 字段，
///         那是 Tauri 双进程时代的产物：Rust 侧拼好路径，WebView 直接当 &lt;img src&gt; 用。
///         YuumiX 单进程下图标由 <c>Controls/LcuImage</c> + <c>AssetService</c> 按 ID 解析
///         （ADR-01 / ADR-11），模型里再存 URL 等于让解析器依赖资源表、并把
///         <c>file://</c> 与缓存路径泄漏进领域模型。</item>
///   <item><b>不做格式化以外的加工。</b>时间与时长以原始单位保存
///         （<see cref="GameCreation"/> 为 Unix 毫秒、<see cref="DurationSeconds"/> 为秒），
///         展示字符串由本类型的计算属性统一给出。放在这里而不是解析器里，是为了让
///         "KDA 为 0 死亡时写作 Perfect" 这类规则只有一个实现点——解析器与界面各写一份
///         时，回归测试断言的到底是哪一份就说不清了。</item>
/// </list>
/// </summary>
public sealed record MatchSummary
{
    /// <summary>对局 ID（LCU <c>gameId</c>），详情页据此回查。</summary>
    public required long GameId { get; init; }

    /// <summary>队列 ID（原 <c>queue_id</c>），决定队列名与地图名。</summary>
    public required int QueueId { get; init; }

    /// <summary>队列名（如"排位单双排"）。由解析器按 <see cref="QueueId"/> 映射，界面不再查表。</summary>
    public required string QueueName { get; init; }

    /// <summary>地图名（如"召唤师峡谷"）。</summary>
    public required string MapName { get; init; }

    /// <summary>开局时间，Unix 毫秒（原 <c>game_creation</c>，注意不是秒）。</summary>
    public required long GameCreation { get; init; }

    /// <summary>对局时长（秒）。</summary>
    public required int DurationSeconds { get; init; }

    /// <summary>胜负。</summary>
    public required bool Win { get; init; }

    /// <summary>是否三分钟重开（原 <c>gameEndedInEarlySurrender</c>），重开局不参与胜率统计。</summary>
    public required bool Remake { get; init; }

    /// <summary>斗魂竞技场名次；其余模式为 <c>null</c>。</summary>
    public int? Placement { get; init; }

    /// <summary>英雄 ID。</summary>
    public required int ChampionId { get; init; }

    /// <summary>召唤师技能 1（ID）。</summary>
    public required int Spell1Id { get; init; }

    /// <summary>召唤师技能 2（ID）。</summary>
    public required int Spell2Id { get; init; }

    /// <summary>英雄等级。</summary>
    public required int ChampionLevel { get; init; }

    /// <summary>击杀。</summary>
    public required int Kills { get; init; }

    /// <summary>死亡。</summary>
    public required int Deaths { get; init; }

    /// <summary>助攻。</summary>
    public required int Assists { get; init; }

    /// <summary>
    /// 装备栏 7 格（原 <c>item0..item6</c>），未出装为 <c>0</c>。
    /// 第 7 格（索引 6）是守卫，界面上单独渲染，不在六格装备区内。
    /// </summary>
    public required IReadOnlyList<int> ItemIds { get; init; }

    /// <summary>基石符文 ID（原 <c>perk0</c>）。</summary>
    public required int RuneId { get; init; }

    /// <summary>补刀数（原 <c>totalMinionsKilled + neutralMinionsKilled</c>）。</summary>
    public required int CreepScore { get; init; }

    /// <summary>获得金币。</summary>
    public required int Gold { get; init; }

    /// <summary>对英雄造成的伤害。</summary>
    public required int DamageToChampions { get; init; }

    /// <summary>总治疗量。</summary>
    public required int TotalHeal { get; init; }

    /// <summary>
    /// 海克斯强化 ID，已去重、去零、最多 5 个（原 <c>dedupe_augment_ids</c> 的语义）。
    /// 名称与图标由界面按 ID 从 <c>GameDataCatalog</c> 解析。
    /// </summary>
    public required IReadOnlyList<int> AugmentIds { get; init; }

    /// <summary>分路；娱乐模式（大乱斗、斗魂）与无法判定时为 <c>null</c>。</summary>
    public MatchPosition? Position { get; init; }

    /// <summary>分路任务装备 ID（原 <c>roleBoundItem</c>），分路判定最权威的依据，保留供排障。</summary>
    public int? RoleBoundItem { get; init; }

    /// <summary>KDA 文本：零死亡写作 <c>Perfect</c>，否则两位小数（如 <c>3.50</c>）。</summary>
    public string KdaText => Deaths == 0
        ? "Perfect"
        : ((Kills + Assists) / (double)Deaths).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>时长文本 <c>mm:ss</c>（见 <see cref="MatchTimeText.FromSeconds"/>）。</summary>
    public string DurationText => MatchTimeText.FromSeconds(DurationSeconds);

    /// <summary>开局时间文本 <c>yyyy-MM-dd HH:mm</c>（本地时区）。</summary>
    public string TimeText =>
        MatchTimeText.FromUnixMilliseconds(GameCreation, "yyyy-MM-dd HH:mm", "1970-01-01 00:00");

    /// <summary>开局时间短文本 <c>MM-dd HH:mm</c>（本地时区）。</summary>
    public string ShortTimeText =>
        MatchTimeText.FromUnixMilliseconds(GameCreation, "MM-dd HH:mm", "01-01 00:00");
}
