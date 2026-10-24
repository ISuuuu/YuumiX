using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// 对局结束后自动点赞代理（对应旧版 agents/auto_match.rs::spawn_auto_honor）。
/// 监听 /lol-honor-v2/v1/ballot 事件，收到荣誉投票单后自动给队友点赞（点赞类型 HEART）并提交投票。
/// 每场对局仅点赞一次，进入 InProgress 阶段时重置标记。
/// </summary>
public sealed class AutoHonorAgent : IAutomationAgent
{
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<AppConfig> _configProvider;
    private readonly AgentSupervisor _supervisor;
    private readonly ILogger<AutoHonorAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private bool _honoredForCurrentMatch;

    public string Name => "AutoHonorAgent";
    public bool IsRunning => _isRunning;

    public AutoHonorAgent(
        Func<ILcuTransport?> transportProvider,
        Func<AppConfig> configProvider,
        AgentSupervisor supervisor,
        ILogger<AutoHonorAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(configProvider);
        ArgumentNullException.ThrowIfNull(supervisor);

        _transportProvider = transportProvider;
        _configProvider = configProvider;
        _supervisor = supervisor;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = true;
        }

        _logger?.LogInformation("自动荣誉点赞代理已启动");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
        }

        _supervisor.CancelKind(TaskKind.AutoHonor);
        _logger?.LogInformation("自动荣誉点赞代理已停止");
        return Task.CompletedTask;
    }

    public void OnPhaseChanged(GamePhase currentPhase)
    {
        if (currentPhase is GamePhase.InProgress or GamePhase.ChampSelect)
        {
            lock (_stateLock)
            {
                _honoredForCurrentMatch = false;
            }
        }
    }

    /// <summary>
    /// 处理接收到的荣誉投票事件 (/lol-honor-v2/v1/ballot)。
    /// </summary>
    public void OnBallotEvent(JsonElement data)
    {
        if (!_isRunning) return;

        var cfg = _configProvider().Functions;
        if (!cfg.EnableAutoHonor) return;

        lock (_stateLock)
        {
            if (_honoredForCurrentMatch) return;
            _honoredForCurrentMatch = true;
        }

        if (data.ValueKind != JsonValueKind.Object) return;

        var puuids = new List<string>();
        if (data.TryGetProperty("eligibleAllies", out var allies) && allies.ValueKind == JsonValueKind.Array)
        {
            foreach (var ally in allies.EnumerateArray())
            {
                if (ally.ValueKind != JsonValueKind.Object) continue;
                bool isBot = ally.TryGetProperty("botPlayer", out var b) && b.ValueKind == JsonValueKind.True;
                if (isBot) continue;

                if (ally.TryGetProperty("puuid", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var puuidStr = p.GetString();
                    if (!string.IsNullOrEmpty(puuidStr))
                    {
                        puuids.Add(puuidStr);
                    }
                }
            }
        }

        if (puuids.Count == 0)
        {
            _logger?.LogDebug("荣誉投票：没有可点赞的队友，跳过");
            return;
        }

        int maxVotes = 1;
        if (data.TryGetProperty("votePool", out var pool) && pool.ValueKind == JsonValueKind.Object)
        {
            if (pool.TryGetProperty("votes", out var v) && v.TryGetInt32(out var votesCount))
            {
                maxVotes = Math.Max(1, votesCount);
            }
        }

        var targets = puuids.Take(maxVotes).ToList();

        _ = _supervisor.TrySpawnAsync(TaskKind.AutoHonor, "AutoHonor", async ct =>
        {
            var transport = _transportProvider();
            if (transport is null) return;

            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var honorPayload = JsonSerializer.Serialize(new
                {
                    honorType = "HEART",
                    recipientPuuid = target,
                });

                var resp = await transport.SendAsync(
                    LcuRequest.Post("/lol-honor-v2/v1/honor-player", honorPayload),
                    ct).ConfigureAwait(false);

                if (resp.IsSuccess)
                {
                    _logger?.LogInformation("自动点赞成功，目标: {Target}", target);
                }
                else
                {
                    _logger?.LogWarning("自动点赞失败: HTTP {Status}", resp.StatusCode);
                }
            }

            // 提交选票
            var ballotResp = await transport.SendAsync(
                LcuRequest.Post("/lol-honor-v2/v1/ballot", "{}"),
                ct).ConfigureAwait(false);

            if (ballotResp.IsSuccess)
            {
                _logger?.LogInformation("荣誉选票提交完成，共点赞 {Count} 票", targets.Count);
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
    }
}
