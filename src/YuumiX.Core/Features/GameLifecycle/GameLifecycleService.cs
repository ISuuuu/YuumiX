using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Features.GameLifecycle;

/// <summary>
/// 对局生命周期监控服务：监听 LCU 的 gameflow-phase WebSocket 事件，
/// 提取对局结束（<see cref="GameEnded"/>）与新对局开始（<see cref="NewGameStarted"/>）信号，
/// 驱动生涯、战绩查询等页面的自动静默刷新。
/// 对应原实现的 <c>store.gameEndedTrigger</c> 与 <c>store.newGameStartedTrigger</c>。
/// </summary>
public sealed class GameLifecycleService : IDisposable
{
    private const string GameflowPhaseUri = "/lol-gameflow/v1/gameflow-phase";

    private readonly LcuConnection _connection;
    private readonly ILogger<GameLifecycleService>? _logger;
    private readonly EventStream<long> _gameEnded;
    private readonly EventStream<long> _newGameStarted;
    private readonly IDisposable? _subscription;
    private readonly IDisposable? _stateSubscription;

    private string _previousPhase = string.Empty;
    private bool _hadInGame;
    private bool _disposed;

    public GameLifecycleService(LcuConnection connection, ILogger<GameLifecycleService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _connection = connection;
        _logger = logger;
        _gameEnded = new EventStream<long>(ex => _logger?.LogWarning(ex, "对局结束事件观察者异常"));
        _newGameStarted = new EventStream<long>(ex => _logger?.LogWarning(ex, "新对局开始事件观察者异常"));

        _subscription = connection.Events.Subscribe(OnLcuEvent);
        _stateSubscription = connection.States.Subscribe(state =>
        {
            if (state.Status == LcuConnectionStatus.Connected)
            {
                _ = ReconcilePhaseAsync(connection);
            }
        });
    }

    private async Task ReconcilePhaseAsync(LcuConnection connection)
    {
        try
        {
            var transport = connection.Transport;
            if (transport is null) return;

            var resp = await transport.SendAsync(LcuRequest.Get(GameflowPhaseUri)).ConfigureAwait(false);
            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body)) return;

            string phase = resp.Body.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(phase))
            {
                OnPhaseChanged(phase);
            }
        }
        catch (Exception ex)
        {
            // 空 catch 会让「启动时客户端已经在对局/选人中」这类情况彻底静默：
            // 阶段对账是收不到 gameflow-phase 事件前的唯一真源，失败后 CurrentPhase 停在旧值，
            // 生涯自动刷新、对局结束检测都会跟着失灵，而日志里一条线索都没有。
            _logger?.LogWarning(ex, "LCU 连接后的阶段对账失败，当前阶段可能停留在旧值");
        }
    }

    /// <summary>对局结束事件流（投递毫秒时间戳）。</summary>
    public EventStream<long> GameEnded => _gameEnded;

    /// <summary>新对局开始事件流（投递毫秒时间戳）。</summary>
    public EventStream<long> NewGameStarted => _newGameStarted;

    public string CurrentPhase { get; private set; } = string.Empty;

    /// <summary>当前局真实对局 ID（局内从 session 获取；对应旧 upload.rs current_game_id）。</summary>
    public long? CurrentGameId { get; internal set; }

    /// <summary>最近一次刚结束对局的真实 ID。</summary>
    public long? LastEndedGameId { get; internal set; }

    public void OnPhaseChanged(string phase)
    {
        string prev = _previousPhase;
        CurrentPhase = phase;
        _previousPhase = phase;

        if (string.Equals(phase, "InProgress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phase, "GameStart", StringComparison.OrdinalIgnoreCase))
        {
            _hadInGame = true;
            _ = TryFetchCurrentGameIdAsync();
        }

        if (string.Equals(phase, "ChampSelect", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(phase, "ReadyCheck", StringComparison.OrdinalIgnoreCase))
        {
            CurrentGameId = null;
        }

        if (_hadInGame &&
            (string.Equals(phase, "EndOfGame", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "WaitingForStats", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "PreEndOfGame", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "Lobby", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "None", StringComparison.OrdinalIgnoreCase)))
        {
            _hadInGame = false;
            LastEndedGameId = CurrentGameId;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger?.LogInformation(
                "检测到对局结束 ({Prev} -> {Current})，对局 ID={GameId}，触发自动刷新",
                prev, phase, LastEndedGameId);
            _gameEnded.Publish(now);
        }

        if (string.Equals(phase, "ChampSelect", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(prev, "ChampSelect", StringComparison.OrdinalIgnoreCase))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger?.LogInformation("进入选人阶段，触发新对局开始信号");
            _newGameStarted.Publish(now);
        }
    }

    private async Task TryFetchCurrentGameIdAsync()
    {
        try
        {
            var transport = _connection.Transport;
            if (transport is null) return;

            var resp = await transport.SendAsync(LcuRequest.Get("/lol-gameflow/v1/session")).ConfigureAwait(false);
            if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Body)) return;

            using var doc = JsonDocument.Parse(resp.Body);
            if (doc.RootElement.TryGetProperty("gameData", out var gameData) &&
                gameData.TryGetProperty("gameId", out var idProp) &&
                idProp.TryGetInt64(out var id) && id > 0)
            {
                CurrentGameId = id;
                _logger?.LogInformation("记录当前局真实对局 ID: {GameId}", id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "获取当前对局 session gameId 失败");
        }
    }

    private void OnLcuEvent(LcuEvent ev)
    {
        if (!string.Equals(ev.Uri, GameflowPhaseUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? phase = null;
        if (ev.Data.ValueKind == JsonValueKind.String)
        {
            phase = ev.Data.GetString();
        }

        if (!string.IsNullOrWhiteSpace(phase))
        {
            OnPhaseChanged(phase);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription?.Dispose();
        _stateSubscription?.Dispose();
    }
}
