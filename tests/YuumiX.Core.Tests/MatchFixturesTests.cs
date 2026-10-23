using System.Text.Json.Nodes;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 夹具可用性回归：12 个战绩夹具（8 真实 + 4 合成）必须随生成输出部署到位，
/// 且清单、文件、载荷三者一致。
///
/// 为什么这组用例属于契约而不是解析器分支：三个并行分支都读同一批夹具
/// （解析器做逐字段回归、服务喂 <c>FakeLcuServer</c>、界面拿它造假数据渲染），
/// 夹具丢失或漏配复制规则时，错误会以"某个分支测试莫名读不到文件"的形式散落在各处。
/// 把"夹具是否就位"这条前置条件单独钉死，后续分支排查时可以直接排除这一层。
/// </summary>
public sealed class MatchFixturesTests
{
    private static MatchFixture Resolve(string file) =>
        MatchFixtures.All.FirstOrDefault(f => f.File == file)
        ?? throw new InvalidOperationException($"清单中不存在 {file}");

    /// <summary>清单里登记的全部夹具文件名，供 Theory 逐条展开。</summary>
    public static TheoryData<string> FixtureFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var fixture in MatchFixtures.All)
            {
                data.Add(fixture.File);
            }

            return data;
        }
    }

    [Fact]
    public void Manifest_DeclaresEightRealAndFourSynthetic()
    {
        var real = MatchFixtures.All.Where(f => !f.IsSynthetic).ToList();
        var synthetic = MatchFixtures.All.Where(f => f.IsSynthetic).ToList();

        Assert.Equal(MatchFixtures.ExpectedRealCount, real.Count);
        Assert.Equal(MatchFixtures.ExpectedSyntheticCount, synthetic.Count);
        Assert.Equal(MatchFixtures.ExpectedRealCount + MatchFixtures.ExpectedSyntheticCount, MatchFixtures.All.Count);
    }

    [Fact]
    public void Manifest_EveryEntryHasFileOnDisk()
    {
        foreach (var fixture in MatchFixtures.All)
        {
            Assert.True(
                File.Exists(fixture.FullPath),
                $"{fixture.File} 在清单中但磁盘上不存在（检查 csproj 的 Fixtures/match 复制规则）：{fixture.FullPath}");
        }
    }

    [Fact]
    public void Disk_EveryFixtureFileIsListedInManifest()
    {
        var onDisk = Directory
            .EnumerateFiles(MatchFixtures.Root, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(MatchFixtures.Root, path).Replace('\\', '/'))
            // sgp/ 子目录有自己的一份清单与回归用例（SgpFixtures / MatchSgpFixtureTests）：
            // 那边的载荷形状是 games[].json + 10 人参与者，与本清单的 games.games[] 不是一回事。
            .Where(relative => !relative.StartsWith("sgp/", StringComparison.Ordinal))
            .Where(relative => !string.Equals(relative, "manifest.json", StringComparison.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        var inManifest = MatchFixtures.All
            .Select(f => f.File)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(inManifest, onDisk);
    }

    /// <summary>
    /// 每个夹具都必须是 <c>/lol-match-history/v1/products/lol/{puuid}/matches</c> 的完整外层响应：
    /// <c>games.games[]</c> 非空，且首个对局带解析器必读的五个字段。
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Fixture_HasExpectedOuterShape(string file)
    {
        var fixture = Resolve(file);
        var root = fixture.ReadJson();

        var games = root["games"]?["games"]?.AsArray();
        Assert.True(games is not null, $"{fixture.Label}: 缺少 games.games 数组");
        Assert.NotEmpty(games!);

        var game = games[0]!;
        Assert.True(game["gameId"]?.GetValue<long>() > 0, $"{fixture.Label}: gameId 缺失或非正数");
        Assert.NotNull(game["queueId"]);
        Assert.True(game["gameCreation"]?.GetValue<long>() > 0, $"{fixture.Label}: gameCreation 缺失（应为毫秒）");
        Assert.True(game["gameDuration"]?.GetValue<int>() >= 0, $"{fixture.Label}: gameDuration 缺失");

        var participants = game["participants"]?.AsArray();
        Assert.True(participants is not null && participants.Count > 0, $"{fixture.Label}: participants 为空");
        Assert.NotNull(participants![0]?["stats"]);
    }

    /// <summary>
    /// 清单声明的 <c>queueId</c> 必须与载荷一致。真实抓包脱敏改不到这里，
    /// 一旦不一致说明夹具被换过而清单没跟——此时"覆盖哪个模式"的说明全部失效。
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void Fixture_QueueIdMatchesManifest(string file)
    {
        var fixture = Resolve(file);
        var game = fixture.ReadJson()["games"]!["games"]!.AsArray()[0]!;

        Assert.Equal(fixture.QueueId, game["queueId"]!.GetValue<int>());

        if (fixture.GameId is { } expectedGameId)
        {
            Assert.Equal(expectedGameId, game["gameId"]!.GetValue<long>());
        }
    }

    /// <summary>"三分钟重开"与"斗魂名次"两个场景是解析器最容易漏的分支，前置确认载荷里真有这些字段。</summary>
    [Fact]
    public void Fixture_RemakeAndArenaCarryTheirSignatureFields()
    {
        var remake = Resolve("real/remake.json").ReadJson();
        Assert.True(
            remake["games"]!["games"]!.AsArray()[0]!["participants"]![0]!["stats"]!["gameEndedInEarlySurrender"]!.GetValue<bool>(),
            "real/remake.json 必须带 gameEndedInEarlySurrender=true");

        var arena = Resolve("synthetic/arena-1700.json").ReadJson();
        Assert.NotNull(arena["games"]!["games"]!.AsArray()[0]!["participants"]![0]!["stats"]!["subteamPlacement"]);
    }
}
