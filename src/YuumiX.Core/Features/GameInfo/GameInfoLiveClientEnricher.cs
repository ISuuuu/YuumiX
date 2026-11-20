using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// Live Client Data（2999 端口）轮询与敌方脱敏实名/英雄补全协作者（§4.11）。
/// </summary>
internal sealed class GameInfoLiveClientEnricher : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ILiveClientService? _liveClientService;
    private readonly Func<GameDataCatalog?>? _catalogProvider;
    private readonly ISummonerService _summonerService;
    private readonly ILogger? _logger;
    private readonly GameInfoStateStore _stateStore;
    private readonly Func<bool> _isActiveGamePhase;
    private readonly Func<int, long, string?, PremadePlayerLike?, CancellationToken, Task<GameInfoPlayer?>> _loadPlayerDataAsync;
    private readonly Action _writeReservedSnapshot;

    private CancellationTokenSource? _pollCts;
    private readonly HashSet<string> _queriedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Live Client 轮询重试间隔（毫秒），测试可注入缩短。</summary>
    internal int PollDelayMs { get; set; } = 2000;

    /// <summary>Live Client 最大轮询重试次数。</summary>
    internal int MaxRetries { get; set; } = 15;

    public GameInfoLiveClientEnricher(
        ILiveClientService? liveClientService,
        Func<GameDataCatalog?>? catalogProvider,
        ISummonerService summonerService,
        ILogger? logger,
        GameInfoStateStore stateStore,
        Func<bool> isActiveGamePhase,
        Func<int, long, string?, PremadePlayerLike?, CancellationToken, Task<GameInfoPlayer?>> loadPlayerDataAsync,
        Action writeReservedSnapshot)
    {
        _liveClientService = liveClientService;
        _catalogProvider = catalogProvider;
        _summonerService = summonerService;
        _logger = logger;
        _stateStore = stateStore;
        _isActiveGamePhase = isActiveGamePhase;
        _loadPlayerDataAsync = loadPlayerDataAsync;
        _writeReservedSnapshot = writeReservedSnapshot;
    }

    internal void CancelPolling()
    {
        lock (_gate)
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            _queriedNames.Clear();
        }
    }

    internal void StartPolling()
    {
        if (_liveClientService is null || !_isActiveGamePhase())
        {
            return;
        }

        CancellationToken token;
        lock (_gate)
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = new CancellationTokenSource();
            token = _pollCts.Token;
        }

        _ = Task.Run(() => PollLoopAsync(token), token);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        for (int retry = 0; retry < MaxRetries; retry++)
        {
            if (cancellationToken.IsCancellationRequested || !_isActiveGamePhase())
            {
                return;
            }

            GameInfoState snapshot = _stateStore.CurrentState;
            bool allHaveHeroes = snapshot.MyTeam.Concat(snapshot.TheirTeam).All(p => p.ChampionId > 0);
            if (allHaveHeroes && retry > 0)
            {
                return;
            }

            bool updated = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                _writeReservedSnapshot();
            }

            try
            {
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<bool> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_liveClientService is null) return false;

        var clientList = await _liveClientService.GetPlayerListAsync(cancellationToken).ConfigureAwait(false);
        if (clientList.Count == 0) return false;

        var catalogObj = _catalogProvider?.Invoke();
        var catalog = catalogObj?.Champions;
        var aliases = catalogObj?.ChampionAliases;
        bool hasUpdated = false;

        static string NormalizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            int hash = name.IndexOf('#');
            string pure = hash >= 0 ? name[..hash] : name;
            return pure.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        }

        // 1. 推导我方 LiveClient 阵营（ORDER 蓝方 / CHAOS 红方，§4.11）
        GameInfoState state = _stateStore.CurrentState;
        string? myLiveClientTeam = state.MapSide switch
        {
            "blue" => "ORDER",
            "red" => "CHAOS",
            _ => null,
        };

        if (string.IsNullOrEmpty(myLiveClientTeam))
        {
            var allyRealPlayers = state.MyTeam.Where(p =>
            {
                string rawName = p.GameName ?? p.DisplayName ?? p.SummonerName ?? string.Empty;
                bool hasIdentity = !string.IsNullOrEmpty(p.Puuid) || !string.IsNullOrEmpty(p.TagLine) ||
                                   (p.SummonerId != 0 && p.SummonerId != p.CellId);
                return PlayerIdentity.IsRealSummonerName(rawName, p.IsBot, hasIdentity);
            }).ToList();

            foreach (var lp in clientList)
            {
                var lpNames = new[] { NormalizeName(lp.RiotIdGameName), NormalizeName(lp.SummonerName), NormalizeName(lp.RiotId) }
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                bool isMyAlly = allyRealPlayers.Any(p =>
                {
                    var pNames = new[] { NormalizeName(p.GameName), NormalizeName(p.DisplayName), NormalizeName(p.SummonerName) }
                        .Where(s => !string.IsNullOrEmpty(s));
                    return pNames.Any(pn => lpNames.Contains(pn));
                });

                if (isMyAlly && !string.IsNullOrEmpty(lp.Team))
                {
                    myLiveClientTeam = lp.Team;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(myLiveClientTeam))
        {
            string? activeName = await _liveClientService.GetActivePlayerNameAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(activeName) && PlayerIdentity.IsRealSummonerName(activeName, false, true))
            {
                string activeNorm = NormalizeName(activeName);
                var matchedTeams = clientList
                    .Where(lp => new[] { NormalizeName(lp.RiotIdGameName), NormalizeName(lp.SummonerName), NormalizeName(lp.RiotId) }.Contains(activeNorm))
                    .Select(lp => lp.Team)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matchedTeams.Count == 1)
                {
                    myLiveClientTeam = matchedTeams[0];
                }
            }
        }

        if (string.IsNullOrEmpty(myLiveClientTeam))
        {
            myLiveClientTeam = _stateStore.CurrentState.MapSide == "red" ? "CHAOS" : "ORDER";
        }

        string deducedMapSide = string.Equals(myLiveClientTeam, "ORDER", StringComparison.OrdinalIgnoreCase) ? "blue" : "red";
        _stateStore.MutateRaw(s => string.IsNullOrEmpty(s.MapSide) ? s with { MapSide = deducedMapSide } : s, true);

        var enemyLiveClients = clientList.Where(lp => !string.Equals(lp.Team, myLiveClientTeam, StringComparison.OrdinalIgnoreCase)).ToList();
        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var lp in clientList)
        {
            int champId = LiveClientChampionMatcher.FindChampionId(catalog, lp.ChampionName, lp.RawChampionName, aliases);
            if (champId <= 0) continue;

            bool isEnemyLp = !string.Equals(lp.Team, myLiveClientTeam, StringComparison.OrdinalIgnoreCase);
            GameInfoState current = _stateStore.CurrentState;
            List<PremadePlayerLike> candidateTeam = (isEnemyLp ? current.TheirTeam : current.MyTeam).ToList();

            var lpCleanNames = new[] { NormalizeName(lp.RiotIdGameName), NormalizeName(lp.SummonerName), NormalizeName(lp.RiotId) }
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            // 1) 实名全等匹配
            PremadePlayerLike? targetPlayer = candidateTeam.FirstOrDefault(p =>
            {
                string rawPName = p.GameName ?? p.DisplayName ?? p.SummonerName ?? string.Empty;
                bool hasIdentity = !string.IsNullOrEmpty(p.Puuid) || !string.IsNullOrEmpty(p.TagLine) ||
                                   (p.SummonerId != 0 && p.SummonerId != p.CellId);
                if (!PlayerIdentity.IsRealSummonerName(rawPName, p.IsBot, hasIdentity)) return false;
                var pCleanNames = new[] { NormalizeName(p.GameName), NormalizeName(p.DisplayName), NormalizeName(p.SummonerName) }
                    .Where(s => !string.IsNullOrEmpty(s));
                return pCleanNames.Any(pn => lpCleanNames.Contains(pn));
            });

            // 2) 匹配已有相同 championId 的槽位
            if (targetPlayer is null)
            {
                targetPlayer = candidateTeam.FirstOrDefault(p =>
                {
                    string key = !string.IsNullOrEmpty(p.Puuid) ? p.Puuid : p.CellId.ToString();
                    if (matchedKeys.Contains(key)) return false;
                    return p.ChampionId == champId;
                });
            }

            // 3) 敌方脱敏槽位按序号回填
            string realName = lp.RiotIdGameName ?? lp.SummonerName ?? lp.RiotId ?? string.Empty;
            bool lpHasIdentity = !string.IsNullOrEmpty(lp.RiotIdTagLine) || realName.Contains('#');
            bool isRealName = PlayerIdentity.IsRealSummonerName(realName, lp.IsBot, lpHasIdentity);

            if (targetPlayer is null && isEnemyLp)
            {
                int lpIdx = enemyLiveClients.IndexOf(lp);
                if (lpIdx >= 0 && lpIdx < candidateTeam.Count)
                {
                    var slot = candidateTeam[lpIdx];
                    string key = !string.IsNullOrEmpty(slot.Puuid) ? slot.Puuid : slot.CellId.ToString();
                    if (!matchedKeys.Contains(key) && (slot.ChampionId <= 0 || slot.ChampionId == champId))
                    {
                        targetPlayer = slot;
                        if (isRealName && !string.IsNullOrEmpty(realName) && slot.DisplayName != realName)
                        {
                            slot = slot with { GameName = realName, DisplayName = realName };
                            candidateTeam[lpIdx] = slot;
                        }
                    }
                }
            }

            if (targetPlayer is null) continue;

            string targetKey = !string.IsNullOrEmpty(targetPlayer.Puuid) ? targetPlayer.Puuid : targetPlayer.CellId.ToString();
            matchedKeys.Add(targetKey);

            _stateStore.MutateRaw(s =>
            {
                var dict = new Dictionary<string, GameInfoPlayer>(s.PlayerData);
                string pdKey = !string.IsNullOrEmpty(targetPlayer.Puuid) ? targetPlayer.Puuid : targetPlayer.CellId.ToString();
                if (dict.TryGetValue(pdKey, out var existingPd) && existingPd.ChampionId != champId)
                {
                    dict[pdKey] = existingPd with { ChampionId = champId };
                    hasUpdated = true;
                }

                var myUpdated = isEnemyLp
                    ? s.MyTeam
                    : s.MyTeam.Select(tp => tp.CellId == targetPlayer.CellId ? tp with { ChampionId = champId } : tp).ToList();

                var theirUpdated = isEnemyLp
                    ? s.TheirTeam.Select(tp => tp.CellId == targetPlayer.CellId ? tp with { ChampionId = champId } : tp).ToList()
                    : s.TheirTeam;

                if (hasUpdated || targetPlayer.ChampionId != champId)
                {
                    hasUpdated = true;
                    return s with
                    {
                        MyTeam = myUpdated,
                        TheirTeam = theirUpdated,
                        PlayerData = dict,
                    };
                }

                return s;
            }, false);

            // 实名回填与重新拉取战绩
            if (isRealName && !string.IsNullOrEmpty(realName) && string.IsNullOrEmpty(targetPlayer.Puuid))
            {
                bool shouldFetch;
                lock (_gate)
                {
                    shouldFetch = _queriedNames.Add(realName);
                }

                if (shouldFetch)
                {
                    _ = FetchRealSummonerAsync(realName, targetPlayer, cancellationToken);
                }
            }
        }

        if (hasUpdated)
        {
            _stateStore.PublishCurrentState();
        }

        return hasUpdated;
    }

    private async Task FetchRealSummonerAsync(
        string realName,
        PremadePlayerLike targetSlot,
        CancellationToken cancellationToken)
    {
        try
        {
            SummonerProfile? profile = null;
            if (realName.Contains('#'))
            {
                profile = await _summonerService.FindByNameAsync(realName, cancellationToken).ConfigureAwait(false);
            }

            if (profile is null)
            {
                string pureName = realName.Split('#')[0];
                profile = await _summonerService.FindByNameAsync(pureName, cancellationToken).ConfigureAwait(false);
            }

            if (profile is not null)
            {
                var updatedSlot = targetSlot with
                {
                    SummonerId = profile.SummonerId,
                    Puuid = profile.Puuid,
                    GameName = profile.GameName,
                    DisplayName = profile.DisplayName,
                    TagLine = profile.TagLine,
                    ProfileIconId = profile.ProfileIconId,
                };

                await _loadPlayerDataAsync(
                    targetSlot.CellId,
                    profile.SummonerId,
                    profile.Puuid,
                    updatedSlot,
                    cancellationToken).ConfigureAwait(false);

                _writeReservedSnapshot();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "LiveClient 回填实名拉取召唤师异常: {RealName}", realName);
            lock (_gate)
            {
                _queriedNames.Remove(realName);
            }
        }
    }

    public void Dispose()
    {
        CancelPolling();
    }
}
