using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;

namespace YuumiX.Core.Tests;

public sealed class ToolsServiceTests
{
    private sealed class MockTransport : ILcuTransport
    {
        public Func<LcuRequest, LcuResponse> Handler { get; set; } = _ => new LcuResponse(200, "{}");

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Handler(request));
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockSgpService : ISgpService
    {
        public bool IsSupported(string? server) => server == "hn1";
        public Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-token");
        public Task<IReadOnlyList<Features.Match.Models.MatchSummary>> GetMatchHistoryAsync(string? server, string puuid, int startIndex, int count, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Features.Match.Models.MatchSummary>>([]);
        public Task<SpectateCredentials?> GetSpectateCredentialsAsync(string? server, string puuid, CancellationToken cancellationToken = default)
        {
            if (puuid == "in-game-puuid")
            {
                return Task.FromResult<SpectateCredentials?>(new SpectateCredentials
                {
                    ObserverServerIp = "127.0.0.1",
                    ObserverServerPort = 8088,
                    ObserverEncryptionKey = "secret-key",
                    GameId = 987654321,
                    Server = server ?? "hn1",
                });
            }
            return Task.FromResult<SpectateCredentials?>(null);
        }
    }

    [Fact]
    public async Task LaunchSpectateAsync_Cmd_LaunchesGameProcess()
    {
        // 临时目录里放一个假的 Game/League of Legends.exe，验证进程启动参数拼接
        var tempDir = Path.Combine(Path.GetTempPath(), "YuumiX_Test_" + Guid.NewGuid().ToString("N"));
        var gameDir = Path.Combine(tempDir, "Game");
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "League of Legends.exe");
        File.WriteAllText(exePath, "fake");

        try
        {
            ProcessStartInfo? captured = null;
            var transport = new MockTransport
            {
                Handler = req =>
                {
                    if (req.Path.StartsWith("/lol-summoner/v1/summoners"))
                    {
                        return new LcuResponse(200, """{"puuid":"in-game-puuid","displayName":"Faker"}""");
                    }
                    return new LcuResponse(200, "{}");
                }
            };

            var creds = new LcuCredentials(1234, "token", 12345, "hn1");
            var config = new YuumiX.Core.Persistence.AppConfig();
            config.General.LolPath.Add(tempDir);
            var service = new ToolsService(
                () => transport,
                new MockSgpService(),
                () => creds,
                () => config,
                processStartOverride: info => captured = info);

            var result = await service.LaunchSpectateAsync("Faker", SpectateMethod.Cmd);

            Assert.True(result.Success);
            Assert.Contains("观战启动成功", result.Message);
            Assert.NotNull(result.Credentials);
            Assert.NotNull(captured);
            Assert.Equal(exePath, captured!.FileName);
            // 参数顺序对齐旧 spectate_directly: spectator {ip}:{port} {key} {gameId} {server}
            Assert.Equal("spectator 127.0.0.1:8088 secret-key 987654321 hn1", captured.Arguments);
            Assert.Equal(gameDir, captured.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LaunchSpectateAsync_Cmd_FailsWithTriedPathsWhenExeMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "YuumiX_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var transport = new MockTransport
            {
                Handler = req =>
                {
                    if (req.Path.StartsWith("/lol-summoner/v1/summoners"))
                    {
                        return new LcuResponse(200, """{"puuid":"in-game-puuid","displayName":"Faker"}""");
                    }
                    return new LcuResponse(200, "{}");
                }
            };

            var creds = new LcuCredentials(1234, "token", 12345, "hn1");
            var config = new YuumiX.Core.Persistence.AppConfig();
            config.General.LolPath.Add(tempDir);
            var service = new ToolsService(() => transport, new MockSgpService(), () => creds, () => config);

            var result = await service.LaunchSpectateAsync("Faker", SpectateMethod.Cmd);

            Assert.False(result.Success);
            Assert.Contains("未找到游戏可执行文件", result.Message);
            Assert.Contains(Path.Combine(tempDir, "Game", "League of Legends.exe"), result.Message);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TryResolveGameExecutable_FallsBackToParentGameDirectory()
    {
        // LolPath 指向 LeagueClient 子目录（无 Game），回退到父目录下的 Game
        var tempDir = Path.Combine(Path.GetTempPath(), "YuumiX_Test_" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(tempDir, "LeagueClient");
        var gameDir = Path.Combine(tempDir, "Game");
        Directory.CreateDirectory(clientDir);
        Directory.CreateDirectory(gameDir);
        var exePath = Path.Combine(gameDir, "League of Legends.exe");
        File.WriteAllText(exePath, "fake");

        try
        {
            string? resolved = YuumiX.Core.Features.Tools.ToolsService.TryResolveGameExecutable(
                [clientDir], out var attempted);

            Assert.Equal(exePath, resolved);
            // 主路径（clientDir/Game）未命中会记入尝试列表，回退路径命中
            Assert.Contains(Path.Combine(clientDir, "Game", "League of Legends.exe"), attempted);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TryResolveGameExecutable_SkipsWeGameMarkerEntries()
    {
        string? resolved = YuumiX.Core.Features.Tools.ToolsService.TryResolveGameExecutable(
            ["WeGame"], out var attempted);

        Assert.Null(resolved);
        // 只有 WeGame 标记时没有真实路径可尝试
        Assert.Empty(attempted);
    }

    [Fact]
    public async Task LaunchSpectateAsync_Cmd_ReturnsCredentialsWhenInGame()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path.StartsWith("/lol-summoner/v1/summoners"))
                {
                    return new LcuResponse(200, """{"puuid":"in-game-puuid","displayName":"Faker"}""");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var creds = new LcuCredentials(1234, "token", 12345, "hn1");
        var service = new ToolsService(() => transport, new MockSgpService(), () => creds);

        // 未配置客户端路径时：凭据已拉到，但无法定位游戏进程启动，提示先去设置配置
        var result = await service.LaunchSpectateAsync("Faker", SpectateMethod.Cmd);

        Assert.False(result.Success);
        Assert.Contains("未配置英雄联盟客户端路径", result.Message);
        Assert.NotNull(result.Credentials);
        Assert.Equal(987654321, result.Credentials.GameId);
        Assert.Equal("127.0.0.1", result.Credentials.ObserverServerIp);
    }

    [Fact]
    public async Task LaunchSpectateAsync_Cmd_ReturnsNotFoundWhenNotInGame()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path.StartsWith("/lol-summoner/v1/summoners"))
                {
                    return new LcuResponse(200, """{"puuid":"offline-puuid","displayName":"Rookie"}""");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var creds = new LcuCredentials(1234, "token", 12345, "hn1");
        var service = new ToolsService(() => transport, new MockSgpService(), () => creds);

        var result = await service.LaunchSpectateAsync("Rookie", SpectateMethod.Cmd);

        Assert.False(result.Success);
        Assert.Contains("不在游戏", result.Message);
    }

    [Fact]
    public async Task GetChampionSkinsAsync_ParsesSkinsList()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-game-data/assets/v1/champions/1.json")
                {
                    return new LcuResponse(200, """
                    {
                      "skins": [
                        { "id": 1000, "name": "黑暗之女 安妮", "loadScreenPath": "/load/1000.jpg", "splashPath": "/splash/1000.jpg" },
                        { "id": 1001, "name": "哥特萝莉 安妮", "loadScreenPath": "/load/1001.jpg", "splashPath": "/splash/1001.jpg" }
                      ]
                    }
                    """);
                }
                return new LcuResponse(404, "{}");
            }
        };

        var service = new ToolsService(() => transport, new MockSgpService());
        var skins = await service.GetChampionSkinsAsync(1);

        Assert.Equal(2, skins.Count);
        Assert.Equal("黑暗之女 安妮", skins[0].Name);
        Assert.True(skins[0].IsBase);
        Assert.Equal("哥特萝莉 安妮", skins[1].Name);
        Assert.False(skins[1].IsBase);
    }

    [Fact]
    public async Task DodgeChampSelectAsync_ThrowsIfNotInChampSelect()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-gameflow/v1/gameflow-phase")
                {
                    return new LcuResponse(200, "\"Lobby\"");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var service = new ToolsService(() => transport, new MockSgpService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DodgeChampSelectAsync());
        Assert.Contains("选人阶段", ex.Message);
    }

    [Fact]
    public void PersistedSettings_Readonly_GetAndSet()
    {
        var service = new ToolsService(() => new MockTransport(), new MockSgpService());
        var tempDir = Path.Combine(Path.GetTempPath(), "YuumiX_Test_" + Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(tempDir, "Game", "Config");
        Directory.CreateDirectory(configDir);
        var settingsFile = Path.Combine(configDir, "PersistedSettings.json");
        File.WriteAllText(settingsFile, "{}");

        try
        {
            string[] paths = [tempDir];

            Assert.False(service.GetGameSettingsReadonly(paths));

            service.SetGameSettingsReadonly(paths, true);
            Assert.True(service.GetGameSettingsReadonly(paths));

            service.SetGameSettingsReadonly(paths, false);
            Assert.False(service.GetGameSettingsReadonly(paths));
        }
        finally
        {
            File.SetAttributes(settingsFile, FileAttributes.Normal);
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FriendCleaner_ScanAndBatchDelete()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-chat/v1/friends")
                {
                    return new LcuResponse(200, """
                    [
                      { "id": "f1", "puuid": "p1", "name": "Friend1", "availability": "chat" },
                      { "id": "f2", "puuid": "p2", "name": "Friend2", "availability": "offline" },
                      { "id": "f3", "puuid": "p3", "name": "Friend3", "availability": "offline" }
                    ]
                    """);
                }
                if (req.Path.StartsWith("/lol-match-history/v1/products/lol/p2/matches"))
                {
                    long twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
                    return new LcuResponse(200, $$"""
                    {
                      "games": {
                        "games": [
                          { "gameCreation": {{twoDaysAgo}} }
                        ]
                      }
                    }
                    """);
                }
                if (req.Path.StartsWith("/lol-match-history/v1/products/lol/p3/matches"))
                {
                    return new LcuResponse(403, "{\"message\":\"FORBIDDEN\"}");
                }
                if (req.Method == HttpMethod.Delete && req.Path.StartsWith("/lol-chat/v1/friends/"))
                {
                    return new LcuResponse(200, "{}");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var cleaner = new FriendCleanerService(() => transport);
        var entries = await cleaner.ScanFriendsAsync();

        Assert.Equal(3, entries.Count);

        var f1 = entries.First(e => e.Friend.Id == "f1");
        Assert.Equal(FriendActivityStatus.Online, f1.MatchStatus.Status);

        var f2 = entries.First(e => e.Friend.Id == "f2");
        Assert.Equal(FriendActivityStatus.Ok, f2.MatchStatus.Status);
        Assert.True(f2.InactiveDays >= 1);

        var f3 = entries.First(e => e.Friend.Id == "f3");
        Assert.Equal(FriendActivityStatus.Hidden, f3.MatchStatus.Status);

        // 批量删除测试
        int deleted = await cleaner.BatchDeleteFriendsAsync(["f1", "f2"]);
        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task LaunchSpectateAsync_Lcu_Success()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path.StartsWith("/lol-summoner/v1/summoners"))
                {
                    return new LcuResponse(200, """{"puuid":"faker-puuid","displayName":"Faker"}""");
                }
                if (req.Path == "/lol-chat/v1/friends")
                {
                    return new LcuResponse(200, """[{"puuid":"faker-puuid","lol":{"gameId":"12345678"}}]""");
                }
                if (req.Path == "/lol-spectator/v1/spectate/launch")
                {
                    return new LcuResponse(204, "");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var service = new ToolsService(() => transport, new MockSgpService());
        var res = await service.LaunchSpectateAsync("Faker", SpectateMethod.Lcu);

        Assert.True(res.Success);
        Assert.Contains("LCU 观战指令已发送", res.Message);
    }

    [Fact]
    public async Task RankSpoof_And_OnlineStatus_And_Preferences()
    {
        string? lastBody = null;
        string? lastPath = null;
        var transport = new MockTransport
        {
            Handler = req =>
            {
                lastPath = req.Path;
                lastBody = req.JsonBody;
                return new LcuResponse(200, "{}");
            }
        };

        var service = new ToolsService(() => transport, new MockSgpService());

        // 段位伪装
        await service.SetRankSpoofAsync("RANKED_SOLO_5x5", "CHALLENGER", "I");
        Assert.Equal("/lol-chat/v1/me", lastPath);
        Assert.Contains("CHALLENGER", lastBody);

        // 在线状态
        await service.SetOnlineStatusAsync("away");
        Assert.Equal("/lol-chat/v1/me", lastPath);
        Assert.Contains("away", lastBody);

        // 个性签名
        await service.SetStatusMessageAsync("Hello YuumiX");
        Assert.Equal("/lol-chat/v1/me", lastPath);
        Assert.Contains("Hello YuumiX", lastBody);

        // 卸下全部勋章
        await service.ClearChallengeBadgesAsync();
        Assert.Equal("/lol-challenges/v1/update-player-preferences", lastPath);
        Assert.Contains("challengeIds", lastBody);

        // 卸下头像框
        await service.ClearProfileTokensAsync();
        Assert.Equal("/lol-summoner/v1/current-summoner/profile-tokens", lastPath);

        // 创建大厅
        await service.CreateLobbyAsync(2400);
        Assert.Equal("/lol-lobby/v2/lobby", lastPath);
        Assert.Contains("2400", lastBody);
    }
}
