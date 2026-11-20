using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 保留对局快照的内存管理、防抖刷盘与恢复校验协作者（§4.9）。
/// </summary>
internal sealed class GameInfoSnapshotCoordinator : IDisposable
{
    private readonly Lock _gate = new();
    private readonly GameInfoSnapshotStore? _snapshotStore;
    private readonly ILogger? _logger;

    /// <summary>保留对局快照（内存权威副本；启动时从 <see cref="_snapshotStore"/> 恢复一次）。</summary>
    private ReservedSnapshot? _reservedSnapshot;

    /// <summary>快照自上次落盘后是否有变更（防抖合并突发写入）。</summary>
    private bool _snapshotDirty;

    /// <summary>后台刷盘循环是否在跑（0/1，Interlocked 维护）。</summary>
    private int _flushInFlight;

    private bool _disposed;

    /// <summary>落盘防抖间隔（毫秒），测试可注入缩短。</summary>
    internal int SnapshotFlushDelayMs { get; set; } = 1000;

    internal readonly record struct RestoredSnapshotPayload(
        IReadOnlyList<PremadePlayerLike> MyTeam,
        IReadOnlyList<PremadePlayerLike> TheirTeam,
        IReadOnlyDictionary<string, GameInfoPlayer> PlayerData,
        IReadOnlyDictionary<string, int> PremadeColorsMy,
        IReadOnlyDictionary<string, int> PremadeColorsTheir);

    public GameInfoSnapshotCoordinator(GameInfoSnapshotStore? snapshotStore, ILogger? logger)
    {
        _snapshotStore = snapshotStore;
        _logger = logger;

        // 启动即从磁盘恢复上一局快照（对应旧版 localStorage 跨启动语义）：
        // 之后的内存副本即权威，非活跃阶段的恢复路径才能把上一局内容重新展示出来
        _reservedSnapshot = snapshotStore?.Read();
    }

    internal bool HasReservedSnapshot
    {
        get { lock (_gate) { return _reservedSnapshot is not null; } }
    }

    internal long? ReservedSnapshotGameId
    {
        get { lock (_gate) { return _reservedSnapshot?.GameId; } }
    }

    internal int ReservedSnapshotTeamCount
    {
        get { lock (_gate) { return _reservedSnapshot?.TeamCount ?? 0; } }
    }

    internal int ReservedSnapshotLoadedCount
    {
        get { lock (_gate) { return _reservedSnapshot?.LoadedCount ?? 0; } }
    }

    internal IReadOnlyDictionary<string, GameInfoPlayer>? ReservedSnapshotPlayerData
    {
        get { lock (_gate) { return _reservedSnapshot?.PlayerData; } }
    }

    /// <summary>清空内存快照并调度刷盘删除磁盘缓存。</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _reservedSnapshot = null;
        }

        ScheduleFlush();
    }

    /// <summary>
    /// 判断 InProgress 精简 session 场景是否应先从同局快照恢复整局数据。
    /// </summary>
    internal bool ShouldRestoreForActiveSession(
        long liveGameId,
        bool isCustomGame,
        int currentTotal,
        int currentTheirCount,
        int sessionTotal)
    {
        if (isCustomGame)
        {
            return false;
        }

        ReservedSnapshot? saved;
        lock (_gate)
        {
            saved = _reservedSnapshot;
        }

        return saved is { GameId: { } savedId } &&
               savedId == liveGameId &&
               (currentTotal == 0 ||
                currentTheirCount == 0 ||
                (saved.TeamCount > 0 && (sessionTotal < saved.TeamCount || currentTotal < saved.TeamCount)));
    }

    /// <summary>
    /// 写入保留快照。
    /// 规则：有我方队伍且至少 1 人已加载才写；loading 占位不入快照；
    /// 非 GameStart/InProgress 阶段新快照已加载数缩水不覆盖；
    /// 同一 gameId 下当前队伍总人数缩水不覆盖（防 InProgress 精简 session 毒化完整快照）。
    /// </summary>
    internal void WriteSnapshot(GameInfoState state, long? currentGameId)
    {
        if (state.MyTeam.Count == 0)
        {
            return;
        }

        // loading 占位不入快照：恢复后没有重拉逻辑，会永久停留在转圈态
        var persistable = new Dictionary<string, GameInfoPlayer>();
        var loadedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in state.PlayerData)
        {
            if (entry is not { Loading: false, Info: not null })
            {
                continue;
            }

            persistable[key] = entry;
            loadedIdentities.Add(!string.IsNullOrEmpty(entry.Info.Puuid)
                ? entry.Info.Puuid
                : entry.Info.SummonerId.ToString());
        }

        int loadedCount = loadedIdentities.Count;
        if (loadedCount == 0)
        {
            return;
        }

        int currentTotal = state.MyTeam.Count + state.TheirTeam.Count;
        bool inRealGame = state.Phase is GamePhase.GameStart or GamePhase.InProgress;

        lock (_gate)
        {
            ReservedSnapshot? saved = _reservedSnapshot;

            // 防缩水覆盖：真实对局阶段始终写入保证时效性；其余阶段仅在新快照数据规模不缩水时覆盖
            if (!inRealGame && saved is not null && loadedCount < saved.LoadedCount)
            {
                return;
            }

            // 同一局内禁止用更瘦的快照覆盖：InProgress 阶段 LCU 会把 gameflow 队伍精简到仅剩自己
            if (currentGameId is not null &&
                saved is { GameId: { } savedGameId } &&
                savedGameId == currentGameId &&
                saved.TeamCount > 0 &&
                currentTotal < saved.TeamCount)
            {
                return;
            }

            _reservedSnapshot = new ReservedSnapshot
            {
                MyTeam = state.MyTeam,
                TheirTeam = state.TheirTeam,
                PlayerData = persistable,
                PremadeColorsMy = state.PremadeColorsMy,
                PremadeColorsTheir = state.PremadeColorsTheir,
                GameId = currentGameId,
                TeamCount = currentTotal,
                LoadedCount = loadedCount,
            };
        }

        ScheduleFlush();
    }

    /// <summary>
    /// 校验并构造可恢复的快照载荷。
    /// 活跃阶段仅当快照 gameId 与当前 live gameId 一致才恢复（同局精简修复场景）；
    /// 非活跃阶段必须 EnableReserveGameinfo=true。
    /// </summary>
    internal bool TryPrepareRestore(
        GamePhase currentPhase,
        long? currentGameId,
        bool enableReserveGameinfo,
        out RestoredSnapshotPayload payload)
    {
        payload = default;

        ReservedSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _reservedSnapshot;
        }

        if (snapshot is null || snapshot.PlayerData.Count == 0)
        {
            return false;
        }

        if (currentPhase.IsGameActive())
        {
            // 活跃阶段仅同局精简修复场景允许恢复；跨局严禁
            if (snapshot.GameId is not { } savedId || currentGameId is not { } liveId || savedId != liveId)
            {
                return false;
            }
        }
        else if (!enableReserveGameinfo)
        {
            return false;
        }

        // 剔除 loading 占位（写入时已过滤，双保险）
        var restored = new Dictionary<string, GameInfoPlayer>();
        foreach (var (key, entry) in snapshot.PlayerData)
        {
            if (entry is not { Loading: true })
            {
                restored[key] = entry;
            }
        }

        if (restored.Count == 0)
        {
            return false;
        }

        // 组队色缺失时按队伍重算
        var colorsMy = snapshot.PremadeColorsMy.Count > 0
            ? snapshot.PremadeColorsMy
            : PremadeAnalyzer.ComputePremadeColors(snapshot.MyTeam);
        var colorsTheir = snapshot.PremadeColorsTheir.Count > 0
            ? snapshot.PremadeColorsTheir
            : PremadeAnalyzer.ComputePremadeColors(snapshot.TheirTeam);

        payload = new RestoredSnapshotPayload(
            snapshot.MyTeam,
            snapshot.TheirTeam,
            restored,
            colorsMy,
            colorsTheir);
        return true;
    }

    /// <summary>
    /// 内存快照变更后调度后台落盘。1s 防抖合并突发（每名玩家数据落位都会写一次内存快照），
    /// 语义对齐旧版 localStorage 的防抖写入；写入不受 EnableReserveGameinfo 约束
    /// （旧版亦如此），开关只在恢复/展示时校验。
    /// </summary>
    private void ScheduleFlush()
    {
        if (_snapshotStore is null)
        {
            return;
        }

        lock (_gate)
        {
            _snapshotDirty = true;
        }

        // 单飞 + 收尾补调：循环退出与置标志之间到达的变更由 finally 重新拉起，保证最后一态必达
        if (Interlocked.CompareExchange(ref _flushInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(FlushLoopAsync);
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (!Volatile.Read(ref _disposed))
            {
                int delay = SnapshotFlushDelayMs;
                if (delay > 0)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _disposed))
                {
                    return;
                }

                bool dirty;
                ReservedSnapshot? snapshot;
                lock (_gate)
                {
                    dirty = _snapshotDirty;
                    _snapshotDirty = false;
                    snapshot = _reservedSnapshot;
                }

                if (!dirty)
                {
                    return;
                }

                try
                {
                    _snapshotStore!.Write(snapshot);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "保留对局快照落盘失败");
                }
            }
        }
        finally
        {
            Volatile.Write(ref _flushInFlight, 0);

            bool pending;
            lock (_gate)
            {
                pending = !_disposed && _snapshotDirty;
            }

            if (pending && Interlocked.CompareExchange(ref _flushInFlight, 1, 0) == 0)
            {
                _ = Task.Run(FlushLoopAsync);
            }
        }
    }

    /// <summary>退出前同步兜底刷盘（对应旧版卸载前 flush，防抖中的最后一态不随进程退出丢失）。</summary>
    internal void FlushNow()
    {
        if (_snapshotStore is null)
        {
            return;
        }

        bool dirty;
        ReservedSnapshot? snapshot;
        lock (_gate)
        {
            dirty = _snapshotDirty;
            _snapshotDirty = false;
            snapshot = _reservedSnapshot;
        }

        if (!dirty)
        {
            return;
        }

        try
        {
            _snapshotStore.Write(snapshot);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "退出时兜底落盘保留快照失败");
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        FlushNow();
    }
}
