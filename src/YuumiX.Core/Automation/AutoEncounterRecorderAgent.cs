using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Automation;

/// <summary>
/// 对局相遇玩家自动记录代理（对应旧版 agents/auto_match.rs::spawn_record_encountered_players）。
/// </summary>
public sealed class AutoEncounterRecorderAgent : IAutomationAgent
{
    private sealed record CurrentGameCache(
        long GameId,
        int QueueId,
        IReadOnlyList<EncounterPlayerEntry> Players);

    private static readonly TimeSpan CacheFetchDelay = TimeSpan.FromMilliseconds(1500);

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly SavedPlayersStore _savedPlayersStore;
    private readonly AgentSupervisor _supervisor;
    private readonly AgentLifecycle _lifecycle;
    private readonly ILogger<AutoEncounterRecorderAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private CurrentGameCache? _currentCache;

    public string Name => "AutoEncounterRecorderAgent";
    public bool IsRunning => _isRunning;

    public AutoEncounterRecorderAgent(
        Func<ILcuTransport?> transportProvider,
        SavedPlayersStore savedPlayersStore,
        AgentSupervisor supervisor,
        AgentLifecycle lifecycle,
        ILogger<AutoEncounterRecorderAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(savedPlayersStore);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(lifecycle);

        _transportProvider = transportProvider;
        _savedPlayersStore = savedPlayersStore;
        _supervisor = supervisor;
        _lifecycle = lifecycle;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_isRunning) return Task.CompletedTask;
            _isRunning = true;
        }

        _logger?.LogInformation("对局相遇自动记录代理已启动");

        _ = Task.Run(async () =>
        {
            try
            {
                await BackfillMissingIdentitiesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "历史路人身份回填跳过或未就绪");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
            _currentCache = null;
        }

        _logger?.LogInformation("对局相遇自动记录代理已停止");
        return Task.CompletedTask;
    }

    public void OnPhaseChanged(GamePhase current)
    {
        if (!_isRunning) return;

        if (current == GamePhase.InProgress)
        {
            _ = _supervisor.TrySpawnAsync(
                TaskKind.Other("CacheEncounterGame"),
                "CacheCurrentGamePlayers",
                async ct => await CacheCurrentGameAsync(ct));
        }

        if (current is GamePhase.EndOfGame or GamePhase.PreEndOfGame or GamePhase.WaitingForStats)
        {
            _ = _supervisor.TrySpawnAsync(
                TaskKind.AutoEncounterRecord,
                "RecordEncounteredPlayers",
                async ct => await RecordEncounteredPlayersAsync(ct));
        }
    }

    private async Task CacheCurrentGameAsync(CancellationToken ct)
    {
        await Task.Delay(CacheFetchDelay, ct).ConfigureAwait(false);
        var transport = _transportProvider();
        if (transport is null) return;

        try
        {
            var res = await transport.SendAsync(LcuRequest.Get("/lol-gameflow/v1/session"), ct).ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.Body)) return;

            using var doc = JsonDocument.Parse(res.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("gameData", out var gameData)) return;
            if (!gameData.TryGetProperty("gameId", out var gameIdProp) || !gameIdProp.TryGetInt64(out var gameId)) return;

            int queueId = 0;
            if (gameData.TryGetProperty("queueId", out var qidProp) && qidProp.TryGetInt32(out var qid))
            {
                queueId = qid;
            }
            else if (gameData.TryGetProperty("queue", out var queueObj) && queueObj.TryGetProperty("id", out var qProp) && qProp.TryGetInt32(out var qVal))
            {
                queueId = qVal;
            }

            if (queueId is 1090 or 1100 or 1130 or 1160) return;

            var targets = new List<(long SummonerId, int ChampionId, string FallbackName)>();
            foreach (var teamKey in new[] { "teamOne", "teamTwo" })
            {
                if (gameData.TryGetProperty(teamKey, out var teamArr) && teamArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in teamArr.EnumerateArray())
                    {
                        long summonerId = p.TryGetProperty("summonerId", out var sidProp) && sidProp.TryGetInt64(out var sid) ? sid : 0;
                        int championId = p.TryGetProperty("championId", out var cidProp) && cidProp.TryGetInt32(out var cid) ? cid : 0;
                        string fallbackName = p.TryGetProperty("summonerName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString() ?? string.Empty
                            : string.Empty;

                        if (summonerId > 0)
                        {
                            targets.Add((summonerId, championId, fallbackName));
                        }
                    }
                }
            }

            if (targets.Count == 0) return;

            using var throttle = new SemaphoreSlim(8);
            var tasks = targets.Select(async target =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var (puuid, name, iconId, tagLine) = await FetchSummonerIdentityAsync(transport, target.SummonerId, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(puuid)) return null;

                    var finalName = !string.IsNullOrWhiteSpace(name) ? name : target.FallbackName;
                    return new EncounterPlayerEntry
                    {
                        Puuid = puuid,
                        SummonerName = finalName,
                        ProfileIconId = iconId,
                        TagLine = tagLine,
                        ChampionId = target.ChampionId,
                    };
                }
                finally
                {
                    throttle.Release();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var players = results.Where(r => r is not null).Select(r => r!).ToList();

            if (players.Count > 0)
            {
                lock (_stateLock)
                {
                    _currentCache = new CurrentGameCache(gameId, queueId, players);
                }
                _logger?.LogInformation("已缓存本局相遇对局信息 (gameId={GameId}, queueId={QueueId}, 共 {Count} 名玩家)", gameId, queueId, players.Count);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "缓存本局相遇信息异常");
        }
    }

    private async Task RecordEncounteredPlayersAsync(CancellationToken ct)
    {
        CurrentGameCache? cache;
        lock (_stateLock)
        {
            cache = _currentCache;
            _currentCache = null;
        }

        if (cache is null || cache.Players.Count == 0) return;

        var transport = _transportProvider();
        if (transport is null) return;

        try
        {
            string selfPuuid = string.Empty;
            var meRes = await transport.SendAsync(LcuRequest.Get("/lol-summoner/v1/current-summoner"), ct).ConfigureAwait(false);
            if (meRes.IsSuccess && !string.IsNullOrWhiteSpace(meRes.Body))
            {
                using var meDoc = JsonDocument.Parse(meRes.Body);
                if (meDoc.RootElement.TryGetProperty("puuid", out var pProp) && pProp.ValueKind == JsonValueKind.String)
                {
                    selfPuuid = pProp.GetString() ?? string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(selfPuuid)) return;

            var recorded = await _savedPlayersStore.RecordEncountersAsync(
                cache.Players,
                selfPuuid,
                cache.GameId,
                cache.QueueId.ToString(),
                ct).ConfigureAwait(false);

            _logger?.LogInformation("对局相遇记录完成，共记录 {Recorded} 名同局玩家", recorded);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "对局相遇玩家持久化异常");
        }
    }

    private static async Task<(string Puuid, string Name, int IconId, string? TagLine)> FetchSummonerIdentityAsync(
        ILcuTransport transport,
        long summonerId,
        CancellationToken ct)
    {
        try
        {
            var res = await transport.SendAsync(LcuRequest.Get($"/lol-summoner/v1/summoners/{summonerId}"), ct).ConfigureAwait(false);
            if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.Body))
            {
                return (string.Empty, string.Empty, 0, null);
            }

            using var doc = JsonDocument.Parse(res.Body);
            var root = doc.RootElement;

            string puuid = root.TryGetProperty("puuid", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            string displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty;
            string gameName = root.TryGetProperty("gameName", out var gn) ? gn.GetString() ?? string.Empty : string.Empty;
            string? tagLine = root.TryGetProperty("tagLine", out var tl) ? tl.GetString() : null;
            int iconId = root.TryGetProperty("profileIconId", out var ic) && ic.TryGetInt32(out var icon) ? icon : 0;

            string name = !string.IsNullOrWhiteSpace(displayName) ? displayName : gameName;
            return (puuid, name, iconId, tagLine);
        }
        catch
        {
            return (string.Empty, string.Empty, 0, null);
        }
    }

    private async Task BackfillMissingIdentitiesAsync(CancellationToken ct)
    {
        var transport = _transportProvider();
        if (transport is null) return;

        var targets = await _savedPlayersStore.GetPendingBackfillPlayersAsync(50, ct).ConfigureAwait(false);
        if (targets.Count == 0) return;

        using var throttle = new SemaphoreSlim(8);
        var tasks = targets.Select(async Task<(string Puuid, string SelfPuuid, string TagLine, string Name, int IconId)?> (target) =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var res = await transport.SendAsync(LcuRequest.Get($"/lol-summoner/v2/summoners/puuid/{target.Puuid}"), ct).ConfigureAwait(false);
                if (!res.IsSuccess || string.IsNullOrWhiteSpace(res.Body)) return null;

                using var doc = JsonDocument.Parse(res.Body);
                var root = doc.RootElement;

                string tagLine = root.TryGetProperty("tagLine", out var tl) ? tl.GetString() ?? string.Empty : string.Empty;
                string displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? string.Empty : string.Empty;
                string gameName = root.TryGetProperty("gameName", out var gn) ? gn.GetString() ?? string.Empty : string.Empty;
                int iconId = root.TryGetProperty("profileIconId", out var ic) && ic.TryGetInt32(out var icon) ? icon : 0;
                string name = !string.IsNullOrWhiteSpace(displayName) ? displayName : gameName;

                if (string.IsNullOrEmpty(tagLine) && string.IsNullOrEmpty(name) && iconId == 0) return null;

                return (target.Puuid, target.SelfPuuid, tagLine, name, iconId);
            }
            catch
            {
                return null;
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var valid = results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
        if (valid.Count > 0)
        {
            var updated = await _savedPlayersStore.SaveBackfilledIdentitiesAsync(valid, ct).ConfigureAwait(false);
            _logger?.LogInformation("已回填 {Updated} 名路人玩家的身份信息 (TagLine/头像)", updated);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
