using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>
/// SGP 战绩解析的**真实载荷回归**（<c>/match-history-query/v1/products/lol/player/{puuid}/SUMMARY</c>）。
///
/// 与 <see cref="MatchSgpParserTests"/> 的分工：那边用内联载荷锁定"规则"，这里用 2026-10-23
/// 采集的国服真实响应锁定"形状 + 数字"。两者缺一不可——规则测试证明实现符合设计，
/// 真实夹具证明设计符合现实；只有前者时，载荷结构一变（例如统计字段被包进 <c>stats</c>）
/// 所有规则测试仍然全绿，而线上会一片空白。
///
/// 三份样本覆盖的正是规则测试无法证明的点：
/// <list type="bullet">
///   <item><b>10 名参与者、本人在第 4 位</b>：按 puuid 命中而非取首项的实证；</item>
///   <item><b>没有 <c>stats</c> 对象</b>：统计字段直接挂在参与者上（清单里的期望值即来自该形态）；</item>
///   <item><b>没有 <c>perk0</c></b>：基石符文的唯一来源是 <c>perks.styles[0].selections[0].perk</c>；</item>
///   <item><b><c>teamPosition</c> 与 <c>lane/role</c> 冲突</b>：首局 <c>lane=JUNGLE</c>、<c>teamPosition=TOP</c>，
///         实际为上单（<c>roleBoundItem=1220</c> 上路任务奖励），这是分路判定优先级的真实依据；</item>
///   <item><b>大乱斗无分路</b>：<c>teamPosition</c> 为空串，解析结果为 <c>null</c>。</item>
/// </list>
/// </summary>
public sealed class MatchSgpFixtureTests
{
    /// <summary>真实样本的数量下限：少于三条说明夹具被误删，不能让测试静默通过。</summary>
    private const int MinimumSampleCount = 3;

    public static TheoryData<string> SampleFiles()
    {
        var data = new TheoryData<string>();
        Assert.True(SgpFixtures.All.Count >= MinimumSampleCount, "SGP 夹具不足，检查 Fixtures/match/sgp/");
        foreach (SgpSample sample in SgpFixtures.All)
        {
            data.Add(sample.File);
        }

        return data;
    }

    private static SgpSample Sample(string file) =>
        SgpFixtures.All.Single(s => s.File == file);

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void ParseSgpHistory_RealSample_MatchesExpectedFields(string file)
    {
        SgpSample sample = Sample(file);

        MatchSummary summary = Assert.Single(sample.ParseSgp());

        Assert.Equal(sample.GameId, summary.GameId);
        Assert.Equal(sample.QueueId, summary.QueueId);
        Assert.Equal(sample.ChampionId, summary.ChampionId);
        Assert.Equal(sample.Kills, summary.Kills);
        Assert.Equal(sample.Deaths, summary.Deaths);
        Assert.Equal(sample.Assists, summary.Assists);
        Assert.Equal(sample.CreepScore, summary.CreepScore);
        Assert.Equal(sample.Gold, summary.Gold);
        Assert.Equal(sample.DurationSeconds, summary.DurationSeconds);
        Assert.Equal(sample.RuneId, summary.RuneId);
        Assert.Equal(sample.Position, summary.Position);
        Assert.Equal(sample.Win, summary.Win);
        Assert.Equal(sample.Remake, summary.Remake);
        Assert.Equal(sample.AugmentIds.ToArray(), summary.AugmentIds.ToArray());
        Assert.Equal(MatchQueueCatalog.Resolve(sample.QueueId).Name, summary.QueueName);
    }

    /// <summary>
    /// 同一局用 LCU 与 SGP 各解析一次，关键数字必须逐项相等。
    /// 这是"两条数据源共用一套字段装配"的唯一真实证据：分开演进时最先漂的就是这里。
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void ParseSgpHistory_RealSample_AgreesWithLcuCounterpart(string file)
    {
        SgpSample sample = Sample(file);
        if (sample.LcuFile is null)
        {
            return; // 大乱斗样本早于 LCU 的 20 局窗口，没有对照
        }

        MatchSummary fromSgp = Assert.Single(sample.ParseSgp());

        using var document = JsonDocument.Parse(sample.ReadLcuText());
        MatchSummary fromLcu = Assert.Single(MatchParser.ParseHistory(document.RootElement));

        Assert.Equal(fromLcu.GameId, fromSgp.GameId);
        Assert.Equal(fromLcu.QueueId, fromSgp.QueueId);
        Assert.Equal(fromLcu.GameCreation, fromSgp.GameCreation);
        Assert.Equal(fromLcu.DurationSeconds, fromSgp.DurationSeconds);
        Assert.Equal(fromLcu.ChampionId, fromSgp.ChampionId);
        Assert.Equal(fromLcu.Spell1Id, fromSgp.Spell1Id);
        Assert.Equal(fromLcu.Spell2Id, fromSgp.Spell2Id);
        Assert.Equal(fromLcu.ChampionLevel, fromSgp.ChampionLevel);
        Assert.Equal(fromLcu.Kills, fromSgp.Kills);
        Assert.Equal(fromLcu.Deaths, fromSgp.Deaths);
        Assert.Equal(fromLcu.Assists, fromSgp.Assists);
        Assert.Equal(fromLcu.CreepScore, fromSgp.CreepScore);
        Assert.Equal(fromLcu.Gold, fromSgp.Gold);
        Assert.Equal(fromLcu.DamageToChampions, fromSgp.DamageToChampions);
        Assert.Equal(fromLcu.TotalHeal, fromSgp.TotalHeal);
        Assert.Equal(fromLcu.RuneId, fromSgp.RuneId);
        Assert.Equal(fromLcu.Position, fromSgp.Position);
        Assert.Equal(fromLcu.RoleBoundItem, fromSgp.RoleBoundItem);
        Assert.Equal(fromLcu.Win, fromSgp.Win);
        Assert.Equal(fromLcu.Remake, fromSgp.Remake);
        Assert.Equal(fromLcu.ItemIds.ToArray(), fromSgp.ItemIds.ToArray());
    }

    /// <summary>
    /// 锁定"真实载荷的形状"。这些断言不验证业务正确性，只验证**夹具仍然是它声称的那种形状**：
    /// 一旦某天 SGP 改回 <c>stats</c> 对象或补上 <c>perk0</c>，本用例会失败，
    /// 提醒重新采集夹具，而不是让别的用例继续在半真半假的样本上通过。
    /// </summary>
    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void RealSample_KeepsSgpPayloadShape(string file)
    {
        SgpSample sample = Sample(file);

        using var document = JsonDocument.Parse(sample.ReadText());
        JsonElement root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.GetProperty("games").ValueKind);

        JsonElement entry = root.GetProperty("games")[0];
        Assert.True(entry.TryGetProperty("metadata", out JsonElement metadata), "SGP 每项应带 metadata");
        Assert.Equal($"HN1_{sample.GameId}", metadata.GetProperty("match_id").GetString());

        JsonElement game = entry.GetProperty("json");
        JsonElement participants = game.GetProperty("participants");
        Assert.Equal(10, participants.GetArrayLength());

        // 本人不是第 0 位：这条断言就是"按 puuid 命中"这个设计的存在理由
        Assert.NotEqual(0, sample.TargetParticipantIndex);
        JsonElement target = participants[sample.TargetParticipantIndex];
        Assert.True(
            string.Equals(sample.TargetPuuid, target.GetProperty("puuid").GetString(), StringComparison.OrdinalIgnoreCase),
            "本人 puuid 与清单不符");

        // 统计字段平铺在参与者上
        Assert.False(target.TryGetProperty("stats", out _), "SGP 参与者不应带 stats 对象");
        Assert.False(target.TryGetProperty("perk0", out _), "SGP 参与者不应带 perk0");
        Assert.True(target.TryGetProperty("perks", out JsonElement perks), "缺 perks 时符文无从回退");

        // 清单里的 runeId 就是 perks 回退口径算出来的：大乱斗两者同为 0
        Assert.Equal(
            sample.RuneId,
            perks.GetProperty("styles")[0].GetProperty("selections")[0].GetProperty("perk").GetInt32());

        Assert.Equal(sample.TeamPosition, target.GetProperty("teamPosition").GetString());
    }

    /// <summary>
    /// 目录里的每个 json（<c>manifest.json</c> 除外）都必须在清单里登记，反之亦然。
    /// 与 <c>MatchFixturesTests.Disk_EveryFixtureFileIsListedInManifest</c> 分工相同：
    /// 漏登记的文件不会被任何用例读到，却会在别人"加一份样本"时安静地失效。
    /// </summary>
    [Fact]
    public void Disk_EverySgpFixtureFileIsListedInManifest()
    {
        var onDisk = Directory
            .EnumerateFiles(SgpFixtures.Root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.Equals(name, "manifest.json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var listed = SgpFixtures.All
            .SelectMany(sample => sample.LcuFile is null ? [sample.File] : new[] { sample.File, sample.LcuFile })
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(listed, onDisk);
    }

    /// <summary>
    /// 首局同时具备"上单的三个证据"：<c>teamPosition=TOP</c>、<c>roleBoundItem=1220</c>（上路任务奖励）、
    /// 而 <c>lane=JUNGLE</c> 是错的那一个。分路优先级（roleBoundItem 先于 timeline）由此有了实证。
    /// </summary>
    [Fact]
    public void RealSample_RankedTop_ResolvesTopDespiteJungleLane()
    {
        SgpSample sample = Sample("summary-ranked-top.json");

        using var document = JsonDocument.Parse(sample.ReadText());
        JsonElement target = document.RootElement.GetProperty("games")[0].GetProperty("json")
            .GetProperty("participants")[sample.TargetParticipantIndex];

        Assert.Equal("TOP", target.GetProperty("teamPosition").GetString());
        Assert.Equal("JUNGLE", target.GetProperty("lane").GetString());
        Assert.Equal(1220, target.GetProperty("roleBoundItem").GetInt32());

        MatchSummary summary = Assert.Single(sample.ParseSgp());
        Assert.Equal(MatchPosition.Top, summary.Position);
    }

    /// <summary>大乱斗：<c>teamPosition</c> 是空串，解析结果必须是 <c>null</c>（不推分路）。</summary>
    [Fact]
    public void RealSample_Aram_HasNoPositionAndKeepsAugments()
    {
        SgpSample sample = Sample("summary-aram.json");

        Assert.Equal(string.Empty, sample.TeamPosition);

        MatchSummary summary = Assert.Single(sample.ParseSgp());
        Assert.Null(summary.Position);
        Assert.Equal("海克斯大乱斗", summary.QueueName);
        Assert.NotEmpty(summary.AugmentIds);
        Assert.Equal(sample.AugmentIds.ToArray(), summary.AugmentIds.ToArray());
    }
}
