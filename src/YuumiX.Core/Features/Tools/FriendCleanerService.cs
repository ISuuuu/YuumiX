using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// <see cref="IFriendCleanerService"/> 的生产实现。
/// </summary>
public sealed class FriendCleanerService : IFriendCleanerService
{
    private const int ChunkSize = 5;
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly ILogger<FriendCleanerService>? _logger;

    public FriendCleanerService(
        Func<ILcuTransport?> transportProvider,
        ILogger<FriendCleanerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        _transportProvider = transportProvider;
        _logger = logger;
    }

    private ILcuTransport GetRequiredTransport()
    {
        var transport = _transportProvider();
        if (transport is null)
        {
            throw new InvalidOperationException("LCU 客户端未连接，请先启动英雄联盟客户端");
        }
        return transport;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FriendEntry>> ScanFriendsAsync(
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();

        // 1. 获取全量好友列表
        var friendsResp = await transport.SendAsync(
            LcuRequest.Get("/lol-chat/v1/friends"),
            cancellationToken).ConfigureAwait(false);

        if (!friendsResp.IsSuccess)
        {
            throw new InvalidOperationException($"获取好友列表失败 (HTTP {friendsResp.StatusCode})");
        }

        List<LcuFriend> friends;
        try
        {
            friends = JsonSerializer.Deserialize<List<LcuFriend>>(friendsResp.Body) ?? [];
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "解析好友列表 JSON 失败");
            return [];
        }

        var results = new ConcurrentBag<FriendEntry>();
        int total = friends.Count;
        int completed = 0;

        // 2. 分批检测
        for (int i = 0; i < friends.Count; i += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = friends.Skip(i).Take(ChunkSize).ToList();
            var tasks = chunk.Select(async f =>
            {
                var status = await CheckFriendLastMatchAsync(transport, f, cancellationToken).ConfigureAwait(false);
                int? inactiveDays = null;
                if (status.Status != FriendActivityStatus.Hidden && status.LastGameTime is { } ts && ts > 0)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long diffMs = Math.Max(0, now - ts);
                    inactiveDays = (int)(diffMs / (1000 * 60 * 60 * 24));
                }

                results.Add(new FriendEntry
                {
                    Friend = f,
                    MatchStatus = status,
                    InactiveDays = inactiveDays,
                });

                int currentCompleted = Interlocked.Increment(ref completed);
                onProgress?.Invoke(currentCompleted, total);
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return results.ToList();
    }

    private async Task<FriendMatchStatus> CheckFriendLastMatchAsync(
        ILcuTransport transport,
        LcuFriend friend,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(friend.Puuid))
        {
            return new FriendMatchStatus(null, FriendActivityStatus.Hidden);
        }

        // 1. 在线保护：如果当前好友处于非离线状态，直接认定活跃
        if (!string.Equals(friend.Availability, "offline", StringComparison.OrdinalIgnoreCase))
        {
            return new FriendMatchStatus(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), FriendActivityStatus.Online);
        }

        // 2. 查询最近比赛战绩
        try
        {
            var matchResp = await transport.SendAsync(
                LcuRequest.Get($"/lol-match-history/v1/products/lol/{friend.Puuid}/matches?begIndex=0&endIndex=1"),
                cancellationToken).ConfigureAwait(false);

            bool matchEmpty = false;
            if (matchResp.IsSuccess)
            {
                using var matchDoc = JsonDocument.Parse(matchResp.Body);
                if (matchDoc.RootElement.TryGetProperty("games", out var gamesObj)
                    && gamesObj.TryGetProperty("games", out var gamesArr)
                    && gamesArr.ValueKind == JsonValueKind.Array)
                {
                    matchEmpty = gamesArr.GetArrayLength() == 0;
                    if (!matchEmpty)
                    {
                        var g = gamesArr[0];
                        long ts = 0;
                        if (g.TryGetProperty("gameCreation", out var gc) && gc.ValueKind == JsonValueKind.Number)
                        {
                            ts = gc.GetInt64();
                        }
                        else if (g.TryGetProperty("gameCreationDate", out var gcd) && gcd.ValueKind == JsonValueKind.String)
                        {
                            if (DateTimeOffset.TryParse(gcd.GetString(), out var dt))
                            {
                                ts = dt.ToUnixTimeMilliseconds();
                            }
                        }

                        if (ts > 0)
                        {
                            return new FriendMatchStatus(ts, FriendActivityStatus.Ok);
                        }
                    }
                }
            }

            // 3. 战绩受限/隐藏/为空时，尝试通过英雄熟练度 lastPlayTime 穿透获取
            var masteryResp = await transport.SendAsync(
                LcuRequest.Get($"/lol-champion-mastery/v1/{friend.Puuid}/champion-mastery"),
                cancellationToken).ConfigureAwait(false);

            bool masteryEmpty = false;
            if (masteryResp.IsSuccess)
            {
                using var masteryDoc = JsonDocument.Parse(masteryResp.Body);
                if (masteryDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    masteryEmpty = masteryDoc.RootElement.GetArrayLength() == 0;
                    long maxPlayTime = 0;
                    foreach (var m in masteryDoc.RootElement.EnumerateArray())
                    {
                        if (m.TryGetProperty("lastPlayTime", out var lpt) && lpt.GetInt64() > maxPlayTime)
                        {
                            maxPlayTime = lpt.GetInt64();
                        }
                    }

                    if (maxPlayTime > 0)
                    {
                        return new FriendMatchStatus(maxPlayTime, FriendActivityStatus.Mastery);
                    }
                }
            }

            // 4. 判断是否被隐私保护（隐藏战绩）拦截
            string errText = matchResp.Body.ToUpperInvariant();
            bool isPrivate = !matchResp.IsSuccess &&
                (matchResp.StatusCode == 403 ||
                 errText.Contains("PRIVACY") ||
                 errText.Contains("PRIVATE") ||
                 errText.Contains("FORBIDDEN"));

            if (isPrivate)
            {
                return new FriendMatchStatus(null, FriendActivityStatus.Hidden);
            }

            return new FriendMatchStatus(null, matchEmpty && masteryEmpty
                ? FriendActivityStatus.None : FriendActivityStatus.Hidden);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "查询好友 {Puuid} 战绩异常，标记为隐藏保护", friend.Puuid);
            return new FriendMatchStatus(null, FriendActivityStatus.Hidden);
        }
    }

    /// <inheritdoc />
    public async Task<int> BatchDeleteFriendsAsync(
        IReadOnlyList<string> friendIds,
        CancellationToken cancellationToken = default)
    {
        if (friendIds is null || friendIds.Count == 0) return 0;
        var transport = GetRequiredTransport();

        int deletedCount = 0;
        foreach (string id in friendIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resp = await transport.SendAsync(
                    LcuRequest.Delete($"/lol-chat/v1/friends/{id}"),
                    cancellationToken).ConfigureAwait(false);

                if (resp.IsSuccess)
                {
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "删除好友 {Id} 异常", id);
            }
        }

        return deletedCount;
    }
}
