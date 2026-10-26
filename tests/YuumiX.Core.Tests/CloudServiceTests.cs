using System.Text.Json;
using YuumiX.Core.Persistence;
using YuumiX.Core.Realtime;
using YuumiX.Core.Upload;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 云端服务纯函数验收（requirements-settings.md §5：URL 剥后缀拼 /lcuHub、
/// userid 解析顺序、端点白名单、上传载荷映射与海克斯提取）。
/// </summary>
public sealed class CloudServiceTests
{
    // ── URL 推导 ────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com", "https://example.com/api/lol/upload")]
    [InlineData("https://example.com/", "https://example.com/api/lol/upload")]
    [InlineData("https://example.com/api/lol/upload", "https://example.com/api/lol/upload")]
    [InlineData("example.com", "http://example.com/api/lol/upload")]
    public void BuildUploadUrl_FollowsOldRules(string input, string expected)
    {
        Assert.Equal(expected, UploadUrlBuilder.BuildUploadUrl(input));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com/lcuHub")]
    [InlineData("https://example.com/", "https://example.com/lcuHub")]
    [InlineData("https://example.com/api/lol/upload", "https://example.com/lcuHub")]
    [InlineData("https://example.com/api/lol/upload-batch/", "https://example.com/lcuHub")]
    [InlineData("https://example.com/lcuHub", "https://example.com/lcuHub")]
    [InlineData("https://example.com/api/lol/lcu", "https://example.com/lcuHub")]
    [InlineData("https://example.com/api/lol/upload/lcuHub", "https://example.com/lcuHub")]
    public void BuildHubBaseUrl_StripsKnownSuffixes(string input, string expected)
    {
        Assert.Equal(expected, UploadUrlBuilder.BuildHubBaseUrl(input));
    }

    // ── userid 解析顺序 ─────────────────────────────────────────

    [Fact]
    public void ResolveUserId_FollowsOldOrder()
    {
        var config = new AppConfig();

        // 1. 兜底
        Assert.Equal("lcu_user_001", SignalrService.ResolveUserId(config));

        // 2. Functions.LcuUserId
        config.Functions.LcuUserId = "from-functions";
        Assert.Equal("from-functions", SignalrService.ResolveUserId(config));

        // 3. General.SignalrUserId 优先
        config.General.SignalrUserId = "from-general";
        Assert.Equal("from-general", SignalrService.ResolveUserId(config));
    }

    // ── LCU 端点白名单 ──────────────────────────────────────────

    [Theory]
    [InlineData("/lol-summoner/v1/current-summoner", true)]
    [InlineData("/lol-match-history/v1/games/123", true)]
    [InlineData("/lol-game-data/assets/v1/champion-summary.json", true)]
    [InlineData("/lol-chat/v1/friends", false)]           // 聊天等敏感端点不放行
    [InlineData("/lol-lobby/v2/lobby", false)]
    [InlineData("", false)]
    [InlineData("/lol-summoner/../v1/current-summoner", false)]
    [InlineData("//lol-summoner/v1/current-summoner", false)]
    public void IsEndpointAllowed_UsesWhitelist(string endpoint, bool expected)
    {
        Assert.Equal(expected, SignalrService.IsEndpointAllowed(endpoint));
    }

    // ── 上传载荷构建 ────────────────────────────────────────────

    private const string GameDetailJson = """
        {
            "gameId": 11398709019,
            "gameMode": "CLASSIC",
            "gameType": "MATCHED_GAME",
            "queueId": 420,
            "gameCreation": 1769629685251,
            "gameDuration": 1800,
            "gameVersion": "16.1.1",
            "participantIdentities": [
                { "participantId": 1, "player": { "puuid": "p-me", "gameName": "Me", "tagLine": "CN1", "summonerName": "旧名" } },
                { "participantId": 2, "player": { "puuid": "p-other", "gameName": "Other", "tagLine": "CN2", "summonerName": "OtherOld" } }
            ],
            "participants": [
                {
                    "participantId": 2, "teamId": 200, "championId": 157, "spell1Id": 4, "spell2Id": 12,
                    "augments": [7001],
                    "stats": { "win": false, "kills": 2, "deaths": 5, "assists": 3, "item0": 1001, "playerAugment2": 7002, "perkPrimaryStyle": 8000, "perkSubStyle": 8100 }
                },
                {
                    "participantId": 1, "teamId": 100, "championId": 412, "spell1Id": 14, "spell2Id": 4,
                    "stats": { "win": true, "kills": 10, "deaths": 1, "assists": 20, "goldEarned": 15000, "totalDamageDealtToChampions": 30000 }
                }
            ]
        }
        """;

    [Fact]
    public void FromGameDetail_MapsFieldsAndSplitsParticipants()
    {
        using var doc = JsonDocument.Parse(GameDetailJson);

        var payload = UploadPayloadBuilder.FromGameDetail(
            doc.RootElement, "p-me", championId => championId == 412 ? "Thresh" : "Yasuo");

        Assert.NotNull(payload);

        // 元信息
        Assert.Equal(11398709019, payload.MatchInfo.MatchId);
        Assert.Equal("CLASSIC", payload.MatchInfo.GameMode);
        Assert.Equal(420, payload.MatchInfo.QueueId);
        Assert.Equal("16.1.1", payload.MatchInfo.GameVersion);

        // 内层 = 当前账号（gameName#tagLine 优先于 summonerName）
        var inner = Assert.Single(payload.MatchInfo.Participants);
        Assert.Equal("Me#CN1", inner.SummonerName);
        Assert.Equal("p-me", inner.Puuid);
        Assert.Equal(412, inner.ChampionId);
        Assert.Equal("Thresh", inner.ChampionName);
        Assert.True(inner.Win);
        Assert.Equal(14, inner.SummonerSpell1Id);

        // 外层 = 其余参与者（保持原始顺序）
        var outer = Assert.Single(payload.Participants);
        Assert.Equal("Other#CN2", outer.SummonerName);
        Assert.Equal(157, outer.ChampionId);
        Assert.Equal("Yasuo", outer.ChampionName);
        Assert.False(outer.Win);
        Assert.Equal(1001, outer.Item0);
        // stats 层未提供的字段降级为 0（serde 缺省语义）
        Assert.Equal(0, outer.VisionScore);
    }

    [Fact]
    public void FromGameDetail_WithoutCurrentPuuid_InnerTakesFirst()
    {
        using var doc = JsonDocument.Parse(GameDetailJson);

        var payload = UploadPayloadBuilder.FromGameDetail(doc.RootElement, null);

        Assert.NotNull(payload);
        Assert.Single(payload.MatchInfo.Participants);
        // participants 数组的首位是 pid 2（Other）→ 内层取它
        Assert.Equal("Other#CN2", payload.MatchInfo.Participants[0].SummonerName);
        Assert.Single(payload.Participants);
        Assert.Equal("Me#CN1", payload.Participants[0].SummonerName);
    }

    [Fact]
    public void FromGameDetail_HextechExtraction_DedupesInOrder()
    {
        using var doc = JsonDocument.Parse(GameDetailJson);

        var payload = UploadPayloadBuilder.FromGameDetail(doc.RootElement, "p-me");
        Assert.NotNull(payload);

        // 外层参与者：stats 层先取（playerAugment2=7002），participant 层后取（augments=[7001]）
        // → 按出现顺序 HexTech0=7002, HexTech1=7001（对齐旧 extract_hextech_ids 的来源顺序）
        var outer = payload.Participants[0];
        Assert.Equal(7002, outer.HexTech0);
        Assert.Equal(7001, outer.HexTech1);
        Assert.Equal(0, outer.HexTech2);
        Assert.Equal(0, outer.HexTech3);
        Assert.Equal(0, outer.HexTech4);
    }

    [Fact]
    public void FromGameDetail_NonObject_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("[]");

        Assert.Null(UploadPayloadBuilder.FromGameDetail(doc.RootElement, null));
    }

    // ── 上传服务的开关门控（不触网路径）────────────────────────

    [Fact]
    public async Task UploadMatchAsync_SkipsWhenUnconfigured()
    {
        var config = new AppConfig();
        config.General.UploadApiUrl = "";

        var service = new Upload.UploadService(() => null, () => config);
        var (outcome, message) = await service.UploadMatchAsync(123, "p-me");

        Assert.Equal(UploadOutcome.Failed, outcome);
        Assert.Contains("未配置", message, StringComparison.Ordinal);
    }
}
