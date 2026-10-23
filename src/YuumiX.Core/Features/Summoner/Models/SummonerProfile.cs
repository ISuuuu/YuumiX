namespace YuumiX.Core.Features.Summoner.Models;

/// <summary>
/// 当前登录召唤师，对应原项目 <c>summoner.rs</c> 的 <c>SummonerDisplay</c>。
///
/// 与原实现的两处有意差异：
/// <list type="bullet">
///   <item>去掉 <c>profileIconUrl</c>——图标由 <c>LcuImage</c> 按
///         <see cref="ProfileIconId"/> 解析（ADR-01 / ADR-11），模型不再携带路径；</item>
///   <item>必填字段保持必填。<c>LcuSummoner::to_display</c> 用 <c>?</c> 把
///         <c>accountId</c> / <c>displayName</c> / <c>puuid</c> / <c>summonerId</c> 缺失
///         变成 <c>None</c>（调用方报"召唤师数据不完整"），这里用 <c>required</c> 表达同一约束：
///         缺字段时解析器返回 <c>null</c>，而不是产出一个 ID 为 0 的假对象。</item>
/// </list>
/// </summary>
public sealed record SummonerProfile
{
    /// <summary>账号 ID。</summary>
    public required long AccountId { get; init; }

    /// <summary>显示名（LCU <c>displayName</c>，可能带旧版标签）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>游戏名（Riot ID 的前半段）。</summary>
    public required string GameName { get; init; }

    /// <summary>标签行（Riot ID 的后半段，不含 <c>#</c>）。</summary>
    public required string TagLine { get; init; }

    /// <summary>玩家 puuid，查询战绩的唯一入参。</summary>
    public required string Puuid { get; init; }

    /// <summary>召唤师 ID。</summary>
    public required long SummonerId { get; init; }

    /// <summary>召唤师等级。</summary>
    public required int SummonerLevel { get; init; }

    /// <summary>头像 ID（缺省 <c>29</c> 的兜底由界面决定，模型保留原始值）。</summary>
    public required int ProfileIconId { get; init; }

    /// <summary>距离下一级的进度百分比（0–100）。</summary>
    public required int PercentCompleteForNextLevel { get; init; }

    /// <summary>当前等级已获得经验。</summary>
    public required long XpSinceLastLevel { get; init; }

    /// <summary>升到下一级所需总经验。</summary>
    public required long XpUntilNextLevel { get; init; }

    /// <summary>隐私设置（LCU <c>privacy</c>，如 <c>PUBLIC</c>）；未下发时为 <c>null</c>。</summary>
    public string? Privacy { get; init; }

    /// <summary>
    /// Riot ID 显示文本。<see cref="TagLine"/> 为空时只返回 <see cref="GameName"/>，
    /// 不补 <c>#</c>——补了会得到 <c>名字#</c> 这种残形。
    /// </summary>
    public string RiotId => string.IsNullOrEmpty(TagLine) ? GameName : $"{GameName}#{TagLine}";
}
