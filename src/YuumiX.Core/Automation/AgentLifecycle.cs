namespace YuumiX.Core.Automation;

/// <summary>
/// 生命周期代际失效异常。当试图在已失效或已变迁的对局会话中执行写操作时抛出。
/// </summary>
public sealed class StaleGenerationException : Exception
{
    public StaleGenerationException(string message) : base(message) { }

    public static StaleGenerationException EpochMismatch(long expected, long actual) =>
        new($"对局代际不匹配: 期望 epoch={expected}, 实际={actual}");

    public static StaleGenerationException RevisionMismatch(long expected, long actual) =>
        new($"阶段版本不匹配: 期望 revision={expected}, 实际={actual}");

    public static StaleGenerationException SessionMismatch(string expected, string? actual) =>
        new($"选人会话不匹配: 期望 session={expected}, 实际={actual ?? "(null)"}");

    public static StaleGenerationException SessionMissing() =>
        new("当前没有活跃的选人会话");
}

/// <summary>
/// 当前游戏世界权威状态快照。
/// </summary>
public sealed record LifecycleState(
    long MatchEpoch,
    long PhaseRevision,
    GamePhase Phase,
    string? SessionId);

/// <summary>
/// 任务代际守卫：在产生外部副作用前验证有效性，防止将过期 action 写入已变迁的新对局。
/// </summary>
public sealed record GenerationGuard(
    long Epoch,
    long Revision,
    string? RequiredSessionId)
{
    /// <summary>
    /// 严格断言当前状态是否仍旧匹配守卫约束。若失效则抛出 <see cref="StaleGenerationException"/>。
    /// </summary>
    public void EnsureValid(LifecycleState current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (Epoch != current.MatchEpoch)
        {
            throw StaleGenerationException.EpochMismatch(Epoch, current.MatchEpoch);
        }

        if (Revision != current.PhaseRevision)
        {
            throw StaleGenerationException.RevisionMismatch(Revision, current.PhaseRevision);
        }

        if (RequiredSessionId is not null)
        {
            if (current.SessionId is null)
            {
                throw StaleGenerationException.SessionMismatch(RequiredSessionId, null);
            }

            if (!string.Equals(RequiredSessionId, current.SessionId, StringComparison.Ordinal))
            {
                throw StaleGenerationException.SessionMismatch(RequiredSessionId, current.SessionId);
            }
        }
    }
}

/// <summary>
/// 阶段变迁结果。
/// </summary>
public abstract record PhaseTransition
{
    /// <summary>阶段无实质变化（重复推送已过滤）。</summary>
    public sealed record NoChange : PhaseTransition;

    /// <summary>同一对局内的正常阶段推演 (例如 ChampSelect -> GameStart)。</summary>
    public sealed record PhaseChanged(GamePhase Previous, GamePhase Current, long MatchEpoch, long PhaseRevision) : PhaseTransition;

    /// <summary>开启了全新对局 (例如 Lobby -> ReadyCheck / ChampSelect)。</summary>
    public sealed record NewMatchStarted(GamePhase Previous, GamePhase Current, long MatchEpoch, long PhaseRevision) : PhaseTransition;

    /// <summary>对局彻底结束或重置退出 (例如 EndOfGame -> Lobby / None，或秒退重回 Lobby)。</summary>
    public sealed record MatchTerminated(GamePhase Previous, GamePhase Current, long FinishedEpoch, long PhaseRevision) : PhaseTransition;
}

/// <summary>
/// 统一游戏流程生命周期代际管理器（对应旧版 agents/lifecycle.rs::LifecycleManager）。
/// 严格跟踪 MatchEpoch 与 PhaseRevision，确保自动化任务具备强代际安全。
/// </summary>
public sealed class AgentLifecycle
{
    private readonly object _lock = new();
    private long _matchEpochCounter = 1;
    private long _phaseRevisionCounter = 1;
    private LifecycleState _state;

    public AgentLifecycle()
    {
        _state = new LifecycleState(1, 1, GamePhase.None, null);
    }

    /// <summary>
    /// 获取当前状态快照。
    /// </summary>
    public LifecycleState Snapshot()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    /// <summary>
    /// 签发当前代际守卫。
    /// </summary>
    public GenerationGuard CaptureGuard(bool requireSession = false)
    {
        lock (_lock)
        {
            return new GenerationGuard(
                _state.MatchEpoch,
                _state.PhaseRevision,
                requireSession ? _state.SessionId : null);
        }
    }

    /// <summary>
    /// 更新选人会话 ID。当会话 ID 发生实质变化时递增 PhaseRevision 以作废旧任务。
    /// </summary>
    public bool UpdateSessionId(string? newSessionId)
    {
        lock (_lock)
        {
            if (string.Equals(_state.SessionId, newSessionId, StringComparison.Ordinal))
            {
                return false;
            }

            long newRev = Interlocked.Increment(ref _phaseRevisionCounter);
            _state = _state with
            {
                SessionId = newSessionId,
                PhaseRevision = newRev,
            };
            return true;
        }
    }

    /// <summary>
    /// 规范化处理阶段变更，去重并推进 Epoch 与 Revision。
    /// </summary>
    public PhaseTransition UpdatePhase(string? newPhaseStr) =>
        UpdatePhase(GamePhaseExtensions.ParseGamePhase(newPhaseStr));

    /// <summary>
    /// 规范化处理阶段变更，去重并推进 Epoch 与 Revision。
    /// </summary>
    public PhaseTransition UpdatePhase(GamePhase newPhase)
    {
        lock (_lock)
        {
            if (_state.Phase == newPhase)
            {
                return new PhaseTransition.NoChange();
            }

            GamePhase oldPhase = _state.Phase;

            // 1. 同一对局内的瞬时重连连接态往返 (InProgress/GameStart <-> Reconnect)
            if ((oldPhase.IsInGame() && newPhase == GamePhase.Reconnect)
                || (oldPhase == GamePhase.Reconnect && newPhase.IsInGame()))
            {
                _state = _state with { Phase = newPhase };
                return new PhaseTransition.PhaseChanged(oldPhase, newPhase, _state.MatchEpoch, _state.PhaseRevision);
            }

            // 2. 观战中性态：观战不属于玩家活跃对局
            if (oldPhase == GamePhase.WatchInProgress || newPhase == GamePhase.WatchInProgress)
            {
                _state = _state with { Phase = newPhase };
                return new PhaseTransition.PhaseChanged(oldPhase, newPhase, _state.MatchEpoch, _state.PhaseRevision);
            }

            long newRevision = Interlocked.Increment(ref _phaseRevisionCounter);

            bool oldIsMatch = oldPhase.IsMatchActive() || oldPhase == GamePhase.Reconnect;
            bool newIsMatch = newPhase.IsMatchActive() || newPhase == GamePhase.Reconnect;

            if (!oldIsMatch && newIsMatch)
            {
                // 全新对局开始
                long newEpoch = Interlocked.Increment(ref _matchEpochCounter);
                string? sessionId = newPhase == GamePhase.ChampSelect && _state.SessionId is null
                    ? $"cs-{newEpoch}-{newRevision}"
                    : _state.SessionId;

                _state = new LifecycleState(newEpoch, newRevision, newPhase, sessionId);
                return new PhaseTransition.NewMatchStarted(oldPhase, newPhase, newEpoch, newRevision);
            }

            if (oldIsMatch && !newIsMatch)
            {
                // 对局结束或秒退
                long finishedEpoch = _state.MatchEpoch;
                _state = new LifecycleState(finishedEpoch, newRevision, newPhase, null);
                return new PhaseTransition.MatchTerminated(oldPhase, newPhase, finishedEpoch, newRevision);
            }

            // 同一对局或空闲阶段内的阶段演进
            string? nextSessionId = newPhase != GamePhase.ChampSelect
                ? null
                : (_state.SessionId ?? $"cs-{_state.MatchEpoch}-{newRevision}");

            _state = new LifecycleState(_state.MatchEpoch, newRevision, newPhase, nextSessionId);
            return new PhaseTransition.PhaseChanged(oldPhase, newPhase, _state.MatchEpoch, newRevision);
        }
    }
}
