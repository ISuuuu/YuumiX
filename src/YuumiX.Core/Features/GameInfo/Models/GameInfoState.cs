namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 对局信息模块完整状态快照。
/// </summary>
public sealed record GameInfoState
{
    /// <summary>全局加载状态</summary>
    public bool Loading { get; init; }

    /// <summary>错误信息</summary>
    public string? Error { get; init; }

    /// <summary>当前阶段</summary>
    public GamePhase Phase { get; init; } = GamePhase.None;

    /// <summary>是否为云顶之弈模式</summary>
    public bool IsTftMode { get; init; }

    /// <summary>当前队列 ID</summary>
    public int? CurrentQueueId { get; init; }

    /// <summary>我方队伍</summary>
    public IReadOnlyList<PremadePlayerLike> MyTeam { get; init; } = [];

    /// <summary>敌方队伍</summary>
    public IReadOnlyList<PremadePlayerLike> TheirTeam { get; init; } = [];

    /// <summary>
    /// 多 key 索引的玩家字典（key 可为 puuid、summonerId、cellId）。
    /// </summary>
    public IReadOnlyDictionary<string, GameInfoPlayer> PlayerData { get; init; } = new Dictionary<string, GameInfoPlayer>();

    /// <summary>我方预组队颜色映射</summary>
    public IReadOnlyDictionary<string, int> PremadeColorsMy { get; init; } = new Dictionary<string, int>();

    /// <summary>敌方预组队颜色映射</summary>
    public IReadOnlyDictionary<string, int> PremadeColorsTheir { get; init; } = new Dictionary<string, int>();

    /// <summary>当前登录玩家召唤师 ID</summary>
    public long CurrentSummonerId { get; init; }

    /// <summary>当前登录玩家 PUUID</summary>
    public string? CurrentSummonerPuuid { get; init; }

    /// <summary>路人集标记映射（puuid → 精简标记），当前登录玩家视角（§4.8）</summary>
    public IReadOnlyDictionary<string, SavedPlayerMarker> SavedPlayerMap { get; init; } = new Dictionary<string, SavedPlayerMarker>();

    /// <summary>我方阵营："blue"（蓝方） | "red"（红方） | null</summary>
    public string? MapSide { get; init; }

    /// <summary>5列模式下的当前选中标签："my"（我方） | "their"（敌方）</summary>
    public string ActiveTab { get; init; } = "my";

    /// <summary>视图模式：5列 或 10列</summary>
    public int ViewColumns { get; init; } = 5;

    /// <summary>正在读取队伍信息且队伍尚无数据</summary>
    public bool IsLoadingTeams { get; init; }

    /// <summary>是否应该显示对局透视内容（对局中或保留快照）</summary>
    public bool ShouldShowContent { get; init; }

    /// <summary>
    /// 当前内容是否来自 <c>LoadDemoState()</c> 注入的演示数据。
    ///
    /// 演示数据不经过 LCU，所以「客户端没开」时也要照常渲染；若没有这个标记，
    /// 界面只能靠连接态判断，离线时点「预览演示对局」会被下一轮渲染的离线分支拦回去。
    /// 除 <see cref="Empty"/> 与演示态外恒为 false——真实状态一律经 UpdateState 写入。
    /// </summary>
    public bool IsDemo { get; init; }

    /// <summary>当前展示的是「预览演示」的示例数据：puuid 为占位值，禁止跳转生涯/战绩查询。</summary>
    public bool IsDemoData { get; init; }

    public static readonly GameInfoState Empty = new();
}
