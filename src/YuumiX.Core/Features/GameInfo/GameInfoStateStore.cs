using YuumiX.Core.Common;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 对局信息内存状态、局号/代数上下文与 <see cref="GameInfoState.PlayerData"/> 原子更新容器。
/// </summary>
internal sealed class GameInfoStateStore
{
    private readonly Lock _gate = new();
    private readonly EventStream<GameInfoState> _stateStream;

    private GameInfoState _state = GameInfoState.Empty;

    /// <summary>当前对局 gameId（InProgress 精简 session 恢复时校验快照归属）。</summary>
    private long? _currentGameId;

    /// <summary>对局上下文代数（每次清内存或切换全新对局递增，用于异步写回守卫）。</summary>
    private long _gameEpoch;

    public GameInfoStateStore(Action<Exception>? onStreamError)
    {
        _stateStream = new EventStream<GameInfoState>(onStreamError);
    }

    internal EventStream<GameInfoState> StateStream => _stateStream;

    internal GameInfoState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal long? CurrentGameId
    {
        get
        {
            lock (_gate)
            {
                return _currentGameId;
            }
        }
        set
        {
            lock (_gate)
            {
                _currentGameId = value;
            }
        }
    }

    internal (long? GameId, long Epoch) CaptureGameContext()
    {
        lock (_gate)
        {
            return (_currentGameId, _gameEpoch);
        }
    }

    internal bool IsSameGameContext(long? capturedGameId, long capturedEpoch)
    {
        lock (_gate)
        {
            return IsSameGameContextLocked(capturedGameId, capturedEpoch);
        }
    }

    /// <summary>
    /// 检测是否切换到全新对局（未经历本局选人且 gameId 变化）；若是则递增代数、清空内存视图并更新 currentGameId。
    /// </summary>
    internal (bool IsNewGame, bool IsSameLiveGame) HandleLiveGameIdTransition(
        long liveGameId,
        long? savedGameId,
        bool champSelectSeenForGame)
    {
        lock (_gate)
        {
            bool isNewGame = !champSelectSeenForGame &&
                ((savedGameId is not null && savedGameId != liveGameId) ||
                 (_currentGameId is not null && _currentGameId != liveGameId));
            bool isSameLiveGame = false;

            if (isNewGame)
            {
                _gameEpoch++;
                _state = _state with
                {
                    MyTeam = [],
                    TheirTeam = [],
                    PlayerData = new Dictionary<string, GameInfoPlayer>(),
                    PremadeColorsMy = new Dictionary<string, int>(),
                    PremadeColorsTheir = new Dictionary<string, int>(),
                    ShouldShowContent = true,
                    IsLoadingTeams = false,
                };
                _stateStream.Publish(_state);
            }
            else
            {
                isSameLiveGame = _currentGameId == liveGameId;
            }

            _currentGameId = liveGameId;
            return (isNewGame, isSameLiveGame);
        }
    }

    /// <summary>所有来自 LCU 的状态变更统一清除演示标记并发布。</summary>
    internal void UpdateState(Func<GameInfoState, GameInfoState> updater)
    {
        lock (_gate)
        {
            _state = updater(_state) with { IsDemo = false };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>直接修改状态（不自动清 IsDemo）；<paramref name="publish"/> 为 true 时仅在状态引用变化时发布。</summary>
    internal void MutateRaw(Func<GameInfoState, GameInfoState> updater, bool publish = true)
    {
        lock (_gate)
        {
            GameInfoState prev = _state;
            GameInfoState next = updater(prev);
            _state = next;
            if (publish && !ReferenceEquals(next, prev))
            {
                _stateStream.Publish(_state);
            }
        }
    }

    internal void SetStateUnconditional(Func<GameInfoState, GameInfoState> updater)
    {
        lock (_gate)
        {
            _state = updater(_state);
            _stateStream.Publish(_state);
        }
    }

    internal void PublishCurrentState()
    {
        lock (_gate)
        {
            _stateStream.Publish(_state);
        }
    }

    /// <summary>清内存态（队伍/玩家数据/组队色），递增代数以拦截晚到异步写回。</summary>
    internal void ClearInGameMemory()
    {
        lock (_gate)
        {
            _gameEpoch++;
            _state = _state with
            {
                MyTeam = [],
                TheirTeam = [],
                PlayerData = new Dictionary<string, GameInfoPlayer>(),
                PremadeColorsMy = new Dictionary<string, int>(),
                PremadeColorsTheir = new Dictionary<string, int>(),
                // 阵营随每局重新判定，残留值会在下一局脱敏场景下造成双方整体错位
                MapSide = null,
                IsLoadingTeams = false,
                ShouldShowContent = false,
                IsDemo = false,
                IsDemoData = false,
            };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>TFT 模式清全部内存态并重置 currentGameId。</summary>
    internal void ApplyTftReset(int queueId)
    {
        lock (_gate)
        {
            _currentGameId = null;
            _state = _state with
            {
                MyTeam = [],
                TheirTeam = [],
                PlayerData = new Dictionary<string, GameInfoPlayer>(),
                PremadeColorsMy = new Dictionary<string, int>(),
                PremadeColorsTheir = new Dictionary<string, int>(),
                IsLoadingTeams = false,
                ShouldShowContent = false,
                CurrentQueueId = queueId,
                IsTftMode = true,
            };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>应用已校验的恢复快照载荷。</summary>
    internal void ApplyRestoredSnapshot(GameInfoSnapshotCoordinator.RestoredSnapshotPayload restored)
    {
        lock (_gate)
        {
            if (!_state.Phase.IsGameActive())
            {
                _gameEpoch++;
            }

            _state = _state with
            {
                MyTeam = restored.MyTeam,
                TheirTeam = restored.TheirTeam,
                PlayerData = restored.PlayerData,
                PremadeColorsMy = restored.PremadeColorsMy,
                PremadeColorsTheir = restored.PremadeColorsTheir,
                IsLoadingTeams = false,
                ShouldShowContent = true,
            };
            _stateStream.Publish(_state);
        }
    }

    /// <summary>移除 PlayerData 中 0..9 的 cellId 数字键（保留 summonerId 大数字键）。</summary>
    internal void RemoveCellIdKeysFromPlayerData()
    {
        lock (_gate)
        {
            if (_state.PlayerData.Count == 0)
            {
                return;
            }

            var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
            bool changed = false;
            for (int i = 0; i <= 9; i++)
            {
                changed |= dict.Remove(i.ToString());
            }

            if (!changed)
            {
                return;
            }

            _state = _state with { PlayerData = dict };
            _stateStream.Publish(_state);
        }
    }

    internal bool IsPlayerEntryCurrent(string key, GameInfoPlayer expected, long? capturedGameId, long capturedEpoch)
    {
        lock (_gate)
        {
            return IsSameGameContextLocked(capturedGameId, capturedEpoch) &&
                   _state.PlayerData.TryGetValue(key, out var current) &&
                   ReferenceEquals(current, expected);
        }
    }

    internal bool TryUpdatePlayerEntryByReference(
        string key,
        GameInfoPlayer expected,
        long? capturedGameId,
        long capturedEpoch,
        Func<GameInfoPlayer, GameInfoPlayer> updater)
    {
        lock (_gate)
        {
            if (!IsSameGameContextLocked(capturedGameId, capturedEpoch) ||
                !_state.PlayerData.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, expected))
            {
                return false;
            }

            ReplacePlayerReferenceLocked(expected, updater(current));
            return true;
        }
    }

    internal bool TryUpdatePlayerEntryByReferenceNoEpoch(
        string key,
        GameInfoPlayer expected,
        Func<GameInfoPlayer, GameInfoPlayer> updater)
    {
        lock (_gate)
        {
            if (!_state.PlayerData.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, expected))
            {
                return false;
            }

            ReplacePlayerReferenceLocked(expected, updater(current));
            return true;
        }
    }

    internal void UpdatePlayerEntriesByReferences(
        IReadOnlyList<GameInfoPlayer> targets,
        Func<GameInfoPlayer, GameInfoPlayer> updater)
    {
        lock (_gate)
        {
            var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
            foreach (var key in dict.Keys.ToList())
            {
                if (!targets.Any(t => ReferenceEquals(t, dict[key])))
                {
                    continue;
                }

                dict[key] = updater(dict[key]);
            }

            _state = _state with { PlayerData = dict };
            _stateStream.Publish(_state);
        }
    }

    internal bool TryApplyFateUpdate(
        string puuid,
        long summonerId,
        long? capturedGameId,
        long capturedEpoch,
        Func<GameInfoPlayer, GameInfoPlayer> updater)
    {
        lock (_gate)
        {
            if (!IsSameGameContextLocked(capturedGameId, capturedEpoch))
            {
                return false;
            }

            if (_state.PlayerData.TryGetValue(puuid, out var latest))
            {
                SavePlayerToDictionaryCoreLocked(0, summonerId, puuid, updater(latest));
                return true;
            }

            return false;
        }
    }

    internal bool TrySavePlayerToDictionary(
        int cellId,
        long summonerId,
        string? puuid,
        GameInfoPlayer player,
        long? capturedGameId,
        long capturedEpoch)
    {
        lock (_gate)
        {
            if (!IsSameGameContextLocked(capturedGameId, capturedEpoch))
            {
                return false;
            }

            SavePlayerToDictionaryCoreLocked(cellId, summonerId, puuid, player);
            return true;
        }
    }

    internal void SavePlayerToDictionary(int cellId, long summonerId, string? puuid, GameInfoPlayer player)
    {
        lock (_gate)
        {
            SavePlayerToDictionaryCoreLocked(cellId, summonerId, puuid, player);
        }
    }

    internal bool TryGetSameGameMissingPlayers(
        int teamOneCount,
        int teamTwoCount,
        out List<PremadePlayerLike> missingPlayers)
    {
        missingPlayers = [];
        lock (_gate)
        {
            if (!_state.ShouldShowContent || _state.IsLoadingTeams)
            {
                return false;
            }

            int myCount = _state.MyTeam.Count;
            int theirCount = _state.TheirTeam.Count;
            if (myCount == 0 && theirCount == 0)
            {
                return false;
            }

            bool teamsComplete =
                (myCount >= teamOneCount && theirCount >= teamTwoCount) ||
                (myCount >= teamTwoCount && theirCount >= teamOneCount);
            if (!teamsComplete)
            {
                return false;
            }

            foreach (var p in _state.MyTeam.Concat(_state.TheirTeam))
            {
                bool isBot = p.Bot || p.IsBot || p.BotChampionId > 0 || p.IsHumanoid;
                GameInfoPlayer? existing = FindPlayerDataLocked(p);

                if (isBot)
                {
                    if (existing is null)
                    {
                        missingPlayers.Add(p);
                    }
                    continue;
                }

                if (existing is null || existing.Loading)
                {
                    missingPlayers.Clear();
                    return false;
                }
            }

            return true;
        }
    }

    internal (GameInfoState State, long? CurrentGameId) CaptureSnapshotInputs()
    {
        lock (_gate)
        {
            return (_state, _currentGameId);
        }
    }

    internal void CompleteStream()
    {
        _stateStream.Complete();
    }

    private bool IsSameGameContextLocked(long? capturedGameId, long capturedEpoch)
    {
        if (capturedEpoch != _gameEpoch)
        {
            return false;
        }

        if (capturedGameId is not null && _currentGameId != capturedGameId)
        {
            return false;
        }

        return true;
    }

    private void ReplacePlayerReferenceLocked(GameInfoPlayer expected, GameInfoPlayer updated)
    {
        var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData);
        foreach (var k in dict.Keys.ToList())
        {
            if (ReferenceEquals(dict[k], expected))
            {
                dict[k] = updated;
            }
        }

        _state = _state with { PlayerData = dict };
        _stateStream.Publish(_state);
    }

    private GameInfoPlayer? FindPlayerDataLocked(PremadePlayerLike p)
    {
        if (!string.IsNullOrEmpty(p.Puuid) && _state.PlayerData.TryGetValue(p.Puuid, out var byPuuid))
        {
            return byPuuid;
        }

        if (p.SummonerId != 0 && _state.PlayerData.TryGetValue(p.SummonerId.ToString(), out var bySid))
        {
            return bySid;
        }

        if (_state.PlayerData.TryGetValue(p.CellId.ToString(), out var byCell))
        {
            return byCell;
        }

        return null;
    }

    private void SavePlayerToDictionaryCoreLocked(int cellId, long summonerId, string? puuid, GameInfoPlayer player)
    {
        var dict = new Dictionary<string, GameInfoPlayer>(_state.PlayerData)
        {
            [cellId.ToString()] = player,
        };

        if (summonerId != 0)
        {
            dict[summonerId.ToString()] = player;
        }

        if (!string.IsNullOrEmpty(puuid))
        {
            dict[puuid] = player;
        }

        _state = _state with { PlayerData = dict };
        _stateStream.Publish(_state);
    }
}
