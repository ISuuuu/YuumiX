using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Common;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Features.AramBench;

/// <summary>
/// 大乱斗板凳席（ARAM bench）服务：把 LCU 的选人会话翻译成「悬浮窗要画什么」，
/// 并提供抢/换板凳席英雄的动作。对应旧版 <c>views/BenchOverlay.vue</c> + <c>ws.rs</c> 里的
/// <c>bench_my_champions</c> 推送逻辑。
///
/// 职责边界：**只产状态与动作，不碰窗口**。窗口是 App 层的事（<c>AramBenchWindowHost</c>），
/// Core 的 TFM 里没有 WinUI 类型（ADR-09），也不该知道"悬浮窗该不该出现"这种产品判断。
/// </summary>
public interface IAramBenchService
{
    /// <summary>当前快照。UI 首帧直接取它，避免先渲染一帧空态再被事件覆盖。</summary>
    AramBenchState Current { get; }

    /// <summary>状态流。回调在 LCU 后台线程上同步触发，UI 侧需自行封送（ADR-07）。</summary>
    EventStream<AramBenchState> States { get; }

    /// <summary>抢/换一张板凳席英雄卡。</summary>
    Task<AramBenchSwapOutcome> SwapAsync(int championId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAramBenchService"/> 的实现。
///
/// 旧版把这段逻辑分散在 Vue 组件与 Rust 侧（历史英雄经 <c>AppState.bench_my_champions</c>
/// 跨窗口推给悬浮窗，因为两个 WebView 的 localStorage 不共享）。单进程后这层 IPC 没必要，
/// 状态直接留在本服务内，窗口组件订阅即可——这也是"我持有过哪些英雄"不再需要单独推送的原因。
///
/// 四条行为约定（前三条来自旧实现的实际踩坑，第四条是接口约束下的预期表现）：
/// <list type="number">
///   <item><b>前 15 秒整排置灰是正常现象，不是缺陷</b>：这段窗口内 LCU 会拒绝把队友手持的英雄
///         换给本机，接口层面拿不到任何可抢的板凳席英雄，因此旧版与新版看到的一样都是全灰。
///         旧版另有一条"本局持有过即可抢回"的分支（<c>unlockedMyBenchChampions</c>），
///         但它的唯一入口是"交换被会话确认生效"，而保护期内的交换正是被这一步拦下的动作，
///         正常时序下永远不会点亮——这里保留同名兜底 <see cref="_unlocked"/>，
///         仅用于本地保护期判定比 LCU 更保守（如拿不到 <c>timer.totalTimeInPhase</c>、
///         且助手在选人进行到一半才启动）时，避免"换成功了却仍显示灰色"的自相矛盾；</item>
///   <item><b>2xx 不等于生效</b>：对队友英雄调用 swap 同样返回成功却静默不生效，
///         因此必须等会话把「我的手持英雄」推成目标 ID 才算成功；</item>
///   <item><b>未拥有/不可用置灰</b>：<c>pickable-champion-ids</c> 拉取失败时**不置灰**
///         （宁可让用户点一下拿到明确失败，也不要把全部英雄误判成不可用）；</item>
///   <item><b>离开选人阶段必须彻底重置</b>：保护期计时与解锁集合都是按局内的，
///         跨局残留会让下一局一进选人就出现"所有英雄都可抢"的错误状态。</item>
/// </list>
/// </summary>
public sealed class AramBenchService : IAramBenchService, IDisposable
{
    private const string SessionUri = "/lol-champ-select/v1/session";
    private const string CurrentChampionUri = "/lol-champ-select/v1/current-champion";
    private const string PhaseUri = "/lol-gameflow/v1/gameflow-phase";
    private const string PickableIdsPath = "/lol-champ-select/v1/pickable-champion-ids";

    /// <summary>前 15 秒队友保护期（对齐旧实现 <c>FIRST_STAGE_PROTECT_MS</c>）。</summary>
    public static readonly TimeSpan ProtectionWindow = TimeSpan.FromSeconds(15);

    /// <summary>等待会话确认交换生效的上限。超过即视为"LCU 静默不生效"。</summary>
    private static readonly TimeSpan SwapVerifyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>可用英雄列表的重试次数与间隔（旧实现：3 次、每次 2 秒）。</summary>
    private const int PickableRetryCount = 3;
    private static readonly TimeSpan PickableRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>可换状态无需提示（空字符串，不产生多余的"点击秒抢"浮层）。</summary>
    private const string ReasonReady = "";

    /// <summary>保护期内的置灰原因。</summary>
    private const string ReasonProtected = "前 15 秒准备阶段（队友保护期中）";

    private const string ReasonNotOwned = "未拥有/不可用";

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly TimeProvider _time;
    private readonly ILogger<AramBenchService>? _logger;
    private readonly EventStream<AramBenchState> _states;
    private readonly IDisposable _eventSubscription;
    private readonly IDisposable _connectionSubscription;
    private readonly Lock _gate = new();

    private AramBenchState _current = AramBenchState.Idle;
    private List<ChampSelectBenchChampion> _bench = [];
    private HashSet<int> _pickable = [];

    /// <summary>
    /// 本局"经会话确认交换成功过"的英雄 ID（含被换下的旧英雄）。
    ///
    /// 只服务于一个兜底场景：本地判定仍在保护期、而 LCU 其实已经放行（典型成因是
    /// <c>timer.totalTimeInPhase</c> 缺失、退回"首次看到会话"的本机计时导致偏保守）。
    /// 正常时序下它保持为空——保护期内的交换本来就拿不到确认，界面全灰是预期表现。
    /// </summary>
    private readonly HashSet<int> _unlocked = [];

    private bool _inChampSelect;
    private bool _benchEnabled;
    private bool _protected;
    private bool _pickableLoaded;
    private bool _pickableFetching;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.MinValue;
    private int _myChampionId;

    private int _pendingSwapId;
    private TaskCompletionSource<int>? _swapCompletion;

    private bool _disposed;

    /// <param name="transportProvider">当前传输层提供器（重连会换实例，不能缓存）。</param>
    /// <param name="events">LCU 事件流，通常传 <c>LcuConnection.Events</c>。</param>
    /// <param name="connectionStates">连接状态流，用于断开时清空状态。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="timeProvider">时钟（测试注入用）。仅保护期兜底计算与重试等待使用。</param>
    public AramBenchService(
        Func<ILcuTransport?> transportProvider,
        EventStream<LcuEvent> events,
        EventStream<LcuConnectionState> connectionStates,
        ILogger<AramBenchService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(connectionStates);

        _transportProvider = transportProvider;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;

        _states = new EventStream<AramBenchState>(ex => _logger?.LogWarning(ex, "板凳席状态观察者抛出异常，已隔离"));
        _eventSubscription = events.Subscribe(OnLcuEvent);
        _connectionSubscription = connectionStates.Subscribe(OnConnectionState);
    }

    public AramBenchState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public EventStream<AramBenchState> States => _states;

    public async Task<AramBenchSwapOutcome> SwapAsync(int championId, CancellationToken cancellationToken = default)
    {
        if (championId <= 0)
        {
            return AramBenchSwapOutcome.Failed;
        }

        lock (_gate)
        {
            if (!_inChampSelect || !_benchEnabled)
            {
                return AramBenchSwapOutcome.NoSession;
            }

            // 未拥有校验在发请求前做：LCU 对不可用英雄的响应语焉不详，
            // 提前拦下能给用户一句明确的原因。
            if (!IsPickableLocked(championId))
            {
                return AramBenchSwapOutcome.NotOwned;
            }
        }

        var transport = _transportProvider();
        if (transport is null)
        {
            return AramBenchSwapOutcome.NoSession;
        }

        // 等待器必须早于 POST 建立：会话回推可能比 await 恢复还快，晚建就会永久等超时。
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingSwapId = championId;
            _swapCompletion = completion;
        }

        try
        {
            var response = await transport
                .SendAsync(LcuRequest.Post($"/lol-champ-select/v1/session/bench/swap/{championId}", "{}"), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                ClearPendingSwap(championId);

                // 404 = 会话已结束（换人结算/退出选人），其余非 2xx 基本都是保护期拦截。
                if (response.IsNotFound)
                {
                    _logger?.LogDebug("板凳席交换时选人会话已结束: {ChampionId}", championId);
                    return AramBenchSwapOutcome.NoSession;
                }

                _logger?.LogInformation(
                    "板凳席交换被拒绝: {ChampionId}，HTTP {StatusCode}",
                    championId,
                    response.StatusCode);
                return AramBenchSwapOutcome.Protected;
            }

            // 2xx 不代表真的换到：队友手持的英雄同样返回成功但静默不生效。
            // 超时用 None 令牌：调用方取消时应当抛 OperationCanceledException 而不是被当成"保护期"，
            // 因此取消在下面显式检查，而不是让延时任务以取消态提前胜出。
            var verified = await Task
                .WhenAny(completion.Task, Task.Delay(SwapVerifyTimeout, _time, CancellationToken.None))
                .ConfigureAwait(false) == completion.Task;

            ClearPendingSwap(championId);
            cancellationToken.ThrowIfCancellationRequested();

            if (verified)
            {
                _logger?.LogInformation("板凳席交换已生效: {ChampionId}", championId);
                return AramBenchSwapOutcome.Swapped;
            }

            _logger?.LogInformation("板凳席交换未被会话确认（保护期或队友英雄）: {ChampionId}", championId);
            return AramBenchSwapOutcome.Protected;
        }
        catch (OperationCanceledException)
        {
            ClearPendingSwap(championId);
            throw;
        }
        catch (Exception ex)
        {
            ClearPendingSwap(championId);
            _logger?.LogWarning(ex, "板凳席交换请求失败: {ChampionId}", championId);
            return AramBenchSwapOutcome.Failed;
        }
    }

    // ── LCU 事件 ─────────────────────────────────────────────────────────

    private void OnLcuEvent(LcuEvent evt)
    {
        try
        {
            if (evt.Uri.StartsWith(SessionUri, StringComparison.OrdinalIgnoreCase))
            {
                OnSessionEvent(evt.Data);
            }
            else if (evt.Uri.StartsWith(CurrentChampionUri, StringComparison.OrdinalIgnoreCase))
            {
                OnCurrentChampionEvent(evt.Data);
            }
            else if (evt.Uri.StartsWith(PhaseUri, StringComparison.OrdinalIgnoreCase))
            {
                OnPhaseEvent(evt.Data);
            }
        }
        catch (Exception ex)
        {
            // 事件处理抛异常会打断 EventStream 的投递链，必须就地吃掉并留痕。
            _logger?.LogWarning(ex, "处理板凳席事件失败: {Uri}", evt.Uri);
        }
    }

    private void OnSessionEvent(JsonElement data)
    {
        ChampSelectSession? session;
        try
        {
            session = data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : JsonSerializer.Deserialize<ChampSelectSession>(data.GetRawText());
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "选人会话反序列化失败，本次事件忽略");
            return;
        }

        bool needPickable = false;
        lock (_gate)
        {
            if (session is null)
            {
                // 会话对象被删除等价于选人结束；阶段事件可能晚一拍才到。
                ResetLocked();
            }
            else
            {
                needPickable = ApplySessionLocked(session);
            }
        }

        Publish();

        // 刻意放到锁外：可用英雄列表要发 HTTP，且拉完会再 Publish 一次，
        // 在持锁状态下发布等于让所有观察者在锁内被回调。
        if (needPickable)
        {
            _ = FetchPickableIdsAsync();
        }
    }

    /// <summary>
    /// <c>current-champion</c> 事件是"我当前手持英雄"的最快来源（会话整体回推有节流），
    /// 单独处理它能让交换确认提前一帧到达。
    /// </summary>
    private void OnCurrentChampionEvent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Number || !data.TryGetInt32(out var championId) || championId <= 0)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            if (!_inChampSelect)
            {
                return;
            }

            changed = TrackMyChampionLocked(championId);
        }

        if (changed)
        {
            Publish();
        }
    }

    private void OnPhaseEvent(JsonElement data)
    {
        var phase = data.ValueKind == JsonValueKind.String ? data.GetString() : null;
        if (string.IsNullOrEmpty(phase))
        {
            return;
        }

        if (string.Equals(phase, "ChampSelect", StringComparison.OrdinalIgnoreCase))
        {
            // 进入选人：会话事件随后就到，不必在这里造状态。
            // 但跨局残留必须此刻清掉——否则保护期会被上一局的开始时间污染。
            lock (_gate)
            {
                if (!_inChampSelect)
                {
                    ResetLocked();
                }
            }

            return;
        }

        bool hadState;
        lock (_gate)
        {
            hadState = _inChampSelect || _benchEnabled || _bench.Count > 0;
            ResetLocked();
        }

        if (hadState)
        {
            _logger?.LogDebug("离开选人阶段 ({Phase})，板凳席状态已清空", phase);
            Publish();
        }
    }

    private void OnConnectionState(LcuConnectionState state)
    {
        if (state.Status != LcuConnectionStatus.Disconnected)
        {
            return;
        }

        bool hadState;
        lock (_gate)
        {
            hadState = _inChampSelect || _benchEnabled || _bench.Count > 0;
            ResetLocked();
        }

        if (hadState)
        {
            Publish();
        }
    }

    // ── 状态计算（调用方必须持锁）────────────────────────────────────────

    /// <summary>应用一次会话快照。返回是否需要补拉可用英雄列表（由调用方在锁外发起）。</summary>
    private bool ApplySessionLocked(ChampSelectSession session)
    {
        if (!_inChampSelect)
        {
            // 首次看到本局的会话：保护期与解锁集合都从这里开始计时
            _inChampSelect = true;
            _sessionStartedAt = _time.GetUtcNow();
            _unlocked.Clear();
            _myChampionId = 0;
            _pendingSwapId = 0;
            _swapCompletion = null;
            _pickable.Clear();
            _pickableLoaded = false;
        }

        _benchEnabled = session.BenchEnabled;
        _bench = session.BenchChampions ?? [];

        var me = session.MyTeam?.Find(player => player.CellId == session.LocalPlayerCellId);
        int myChampionId = Math.Max(me?.ChampionId ?? 0, me?.ChampionPickIntent ?? 0);
        TrackMyChampionLocked(myChampionId);

        _protected = IsProtectedPhaseLocked(session.Timer);

        // 可用英雄列表一次选人只需成功一次；失败由 FetchPickableIdsAsync 内部重试
        return _benchEnabled && !_pickableLoaded && !_pickableFetching;
    }

    /// <summary>
    /// 记录"我当前手持的英雄"，并处理交换确认。返回是否发生了确认（用于日志/调试）。
    /// </summary>
    private bool TrackMyChampionLocked(int championId)
    {
        if (championId <= 0)
        {
            return false;
        }

        bool confirmed = false;

        if (_pendingSwapId == championId)
        {
            // 交换真正生效：经会话确认手持变为目标英雄，目标英雄与换下的旧英雄加入解锁集合，动态点亮为亮色
            _unlocked.Add(championId);
            if (_myChampionId > 0 && _myChampionId != championId)
            {
                _unlocked.Add(_myChampionId);
            }

            _pendingSwapId = 0;
            _swapCompletion?.TrySetResult(championId);
            _swapCompletion = null;
            confirmed = true;
        }

        _myChampionId = championId;
        return confirmed;
    }

    private bool IsProtectedPhaseLocked(ChampSelectTimer? timer)
    {
        // FINALIZATION（最后确认阶段）一律放开：此时队友保护期已过，抢谁的看手速
        if (string.Equals(timer?.Phase, "FINALIZATION", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (timer?.TotalTimeInPhase is ulong total && total > 0)
        {
            ulong left = Math.Min(timer.AdjustedTimeLeftInPhase ?? total, total);
            return TimeSpan.FromMilliseconds(total - left) < ProtectionWindow;
        }

        // 拿不到完整 timer 时退回本机计时：会话首次出现即视为阶段开始
        return _time.GetUtcNow() - _sessionStartedAt < ProtectionWindow;
    }

    private bool IsPickableLocked(int championId) =>
        _pickable.Count == 0 || _pickable.Contains(championId);

    private void ResetLocked()
    {
        _inChampSelect = false;
        _benchEnabled = false;
        _protected = false;
        _bench = [];
        _pickable.Clear();
        _pickableLoaded = false;
        _unlocked.Clear();
        _myChampionId = 0;
        _pendingSwapId = 0;
        _swapCompletion = null;
        _sessionStartedAt = DateTimeOffset.MinValue;
    }

    private void ClearPendingSwap(int championId)
    {
        lock (_gate)
        {
            if (_pendingSwapId != championId)
            {
                return;
            }

            _pendingSwapId = 0;
            _swapCompletion = null;
        }
    }

    // ── 可用英雄列表 ─────────────────────────────────────────────────────

    private async Task FetchPickableIdsAsync()
    {
        lock (_gate)
        {
            if (_pickableFetching || _pickableLoaded || !_inChampSelect)
            {
                return;
            }

            _pickableFetching = true;
        }

        try
        {
            for (int attempt = 0; attempt < PickableRetryCount; attempt++)
            {
                var transport = _transportProvider();
                if (transport is null)
                {
                    return;
                }

                // 选人已结束就没必要继续拉——重试期间用户可能已经取消选人
                lock (_gate)
                {
                    if (!_inChampSelect)
                    {
                        return;
                    }
                }

                var response = await transport
                    .SendAsync(LcuRequest.Get(PickableIdsPath), CancellationToken.None)
                    .ConfigureAwait(false);

                if (response.IsSuccess)
                {
                    int[]? ids = null;
                    try
                    {
                        ids = JsonSerializer.Deserialize<int[]>(response.Body);
                    }
                    catch (JsonException ex)
                    {
                        _logger?.LogDebug(ex, "可用英雄列表解析失败");
                    }

                    if (ids is not null)
                    {
                        lock (_gate)
                        {
                            // 请求在途期间可能已经离开选人阶段：此时写入会污染下一局的置灰判断
                            if (!_inChampSelect)
                            {
                                return;
                            }

                            _pickable = [.. ids];
                            _pickableLoaded = true;
                        }

                        _logger?.LogDebug("可用英雄列表获取成功，共 {Count} 个", ids.Length);
                        Publish();
                    }

                    return;
                }

                // 选人会话未激活时 LCU 返回 404（或 "No active delegate"），重试没有意义
                if (response.IsNotFound)
                {
                    _logger?.LogDebug("选人会话未激活，停止拉取可用英雄列表");
                    return;
                }

                if (attempt < PickableRetryCount - 1)
                {
                    await Task.Delay(PickableRetryDelay, _time).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "拉取可用英雄列表失败（不影响置灰逻辑，将按全部可用显示）");
        }
        finally
        {
            lock (_gate)
            {
                _pickableFetching = false;
            }
        }
    }

    // ── 发布 ─────────────────────────────────────────────────────────────

    private void Publish()
    {
        AramBenchState next;
        lock (_gate)
        {
            next = BuildStateLocked();
            if (SameState(_current, next))
            {
                return;
            }

            _current = next;
        }

        _states.Publish(next);
    }

    private AramBenchState BuildStateLocked()
    {
        if (!_inChampSelect)
        {
            return AramBenchState.Idle;
        }

        var champions = new List<AramBenchChampion>(_bench.Count);
        foreach (var entry in _bench)
        {
            int championId = entry.ChampionId;
            if (championId <= 0)
            {
                continue;
            }

            champions.Add(DescribeLocked(entry));
        }

        return new AramBenchState(_inChampSelect, _benchEnabled, _protected, champions);
    }

    private AramBenchChampion DescribeLocked(ChampSelectBenchChampion entry)
    {
        int championId = entry.ChampionId;
        if (!IsPickableLocked(championId))
        {
            return new AramBenchChampion(championId, false, ReasonNotOwned);
        }

        // 经官方会话置换成功验证属于本人的专属英雄：在前 15 秒内保持动态点亮高亮
        if (_unlocked.Contains(championId))
        {
            return new AramBenchChampion(championId, true, ReasonReady);
        }

        // 前 15 秒准备阶段：初始置灰
        if (_protected)
        {
            return new AramBenchChampion(championId, false, ReasonProtected);
        }

        // 15 秒之后：全面开放阶段，全部高亮
        return new AramBenchChampion(championId, true, ReasonReady);
    }

    /// <summary>
    /// 结构相等判断：IDs 与可点状态都一致才算没变。
    /// 记录类型默认按引用比较 <see cref="Champions"/>，直接 <c>==</c> 会永远不等。
    /// </summary>
    private static bool SameState(AramBenchState left, AramBenchState right)
    {
        if (left.IsChampSelect != right.IsChampSelect
            || left.BenchEnabled != right.BenchEnabled
            || left.IsProtected != right.IsProtected
            || left.Champions.Count != right.Champions.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Champions.Count; i++)
        {
            var a = left.Champions[i];
            var b = right.Champions[i];
            if (a.ChampionId != b.ChampionId || a.Clickable != b.Clickable)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eventSubscription.Dispose();
        _connectionSubscription.Dispose();
        _states.Complete();
    }
}
