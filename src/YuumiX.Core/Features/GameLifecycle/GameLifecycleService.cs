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

    private readonly ILogger<GameLifecycleService>? _logger;
    private readonly EventStream<long> _gameEnded;
    private readonly EventStream<long> _newGameStarted;
    private readonly IDisposable? _subscription;

    private string _previousPhase = string.Empty;
    private bool _hadInGame;
    private bool _disposed;

    public GameLifecycleService(LcuConnection connection, ILogger<GameLifecycleService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _logger = logger;
        _gameEnded = new EventStream<long>(ex => _logger?.LogWarning(ex, "对局结束事件观察者异常"));
        _newGameStarted = new EventStream<long>(ex => _logger?.LogWarning(ex, "新对局开始事件观察者异常"));

        _subscription = connection.Events.Subscribe(OnLcuEvent);
    }

    /// <summary>对局结束事件流（投递毫秒时间戳）。</summary>
    public EventStream<long> GameEnded => _gameEnded;

    /// <summary>新对局开始事件流（投递毫秒时间戳）。</summary>
    public EventStream<long> NewGameStarted => _newGameStarted;

    public string CurrentPhase { get; private set; } = string.Empty;

    public void OnPhaseChanged(string phase)
    {
        string prev = _previousPhase;
        CurrentPhase = phase;
        _previousPhase = phase;

        if (string.Equals(phase, "InProgress", StringComparison.OrdinalIgnoreCase))
        {
            _hadInGame = true;
        }

        if (_hadInGame &&
            (string.Equals(phase, "EndOfGame", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "WaitingForStats", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "PreEndOfGame", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "Lobby", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(phase, "None", StringComparison.OrdinalIgnoreCase)))
        {
            _hadInGame = false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger?.LogInformation("检测到对局结束 ({Prev} -> {Current})，触发自动刷新", prev, phase);
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
    }
}
