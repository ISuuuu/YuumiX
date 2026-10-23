using System.Text.Json.Nodes;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

/// <summary>清单 <c>sgp/manifest.json</c> 中的一条真实样本。</summary>
/// <param name="File">SGP 响应文件名（相对 <c>sgp/</c>）。</param>
/// <param name="GameId">该样本包含的那一局。</param>
/// <param name="QueueId">队列 ID。</param>
/// <param name="TargetPuuid">被查询玩家 puuid（已脱敏的占位值）。</param>
/// <param name="TargetParticipantIndex">
/// 该玩家在 <c>participants</c> 里的下标。这份夹具之所以有意义，正因为**它不是 0**——
/// 取 <c>participants[0]</c> 的实现在这里必然读到别人的战绩。
/// </param>
/// <param name="ChampionId">本人英雄。</param>
/// <param name="Kills">击杀。</param>
/// <param name="Deaths">死亡。</param>
/// <param name="Assists">助攻。</param>
/// <param name="CreepScore">补刀（近战兵 + 野怪）。</param>
/// <param name="Gold">金币。</param>
/// <param name="DurationSeconds">时长（秒）。</param>
/// <param name="RuneId">基石符文：SGP 没有 <c>perk0</c>，期望值来自 <c>perks.styles[0].selections[0].perk</c>。</param>
/// <param name="TeamPosition">SGP 原始的 <c>teamPosition</c>（大乱斗为空串），用于解释分路判定的依据。</param>
/// <param name="Position">解析后的分路；娱乐模式为 <c>null</c>。</param>
/// <param name="Win">胜负。</param>
/// <param name="Remake">是否重开。</param>
/// <param name="AugmentIds">海克斯强化 ID（已去重、去零）。</param>
/// <param name="LcuFile">同一局的 LCU 列表夹具文件名；没有对照样本时为 <c>null</c>。</param>
internal sealed record SgpSample(
    string File,
    long GameId,
    int QueueId,
    string TargetPuuid,
    int TargetParticipantIndex,
    int ChampionId,
    int Kills,
    int Deaths,
    int Assists,
    int CreepScore,
    int Gold,
    int DurationSeconds,
    int RuneId,
    string? TeamPosition,
    MatchPosition? Position,
    bool Win,
    bool Remake,
    IReadOnlyList<int> AugmentIds,
    string? LcuFile);

/// <summary>
/// SGP 真实夹具的访问入口。
///
/// 与原 <c>Fixtures/match/manifest.json</c>（LCU 抓包）分开，因为两者要断言的形状不同：
/// LCU 是 <c>games.games[]</c> + 单人参与者，SGP 是 <c>games[].json</c> + 10 人参与者，
/// 且统计字段直接挂在参与者上（没有 <c>stats</c> 对象）。混在一个清单里会让"读到哪一份"
/// 变得需要读文件内容才能判断。
///
/// 样本采集于 2026-10-23（腾讯国服 HN1），脱敏规则见 <c>sgp/manifest.json</c> 的
/// <c>desensitized</c> 字段：只换标识类字段，其余原样保留，否则夹具失去"防结构漂移"的作用。
/// </summary>
internal static class SgpFixtures
{
    private static readonly Lazy<IReadOnlyList<SgpSample>> Cached = new(Load);

    /// <summary>夹具根目录（随生成输出复制）。</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "match", "sgp");

    /// <summary>全部真实样本，顺序与清单一致。</summary>
    public static IReadOnlyList<SgpSample> All => Cached.Value;

    /// <summary>读取 SGP 响应原文。</summary>
    public static string ReadText(this SgpSample sample) => File.ReadAllText(Path.Combine(Root, sample.File));

    /// <summary>读取 LCU 对照夹具原文；没有对照时失败——调用方应先判断 <see cref="SgpSample.LcuFile"/>。</summary>
    public static string ReadLcuText(this SgpSample sample)
    {
        if (sample.LcuFile is null)
        {
            throw new InvalidOperationException($"样本 {sample.File} 没有 LCU 对照夹具");
        }

        return File.ReadAllText(Path.Combine(Root, sample.LcuFile));
    }

    /// <summary>解析该样本里的 SGP 响应。</summary>
    public static IReadOnlyList<MatchSummary> ParseSgp(this SgpSample sample)
    {
        using var document = System.Text.Json.JsonDocument.Parse(sample.ReadText());
        return MatchParser.ParseSgpHistory(document.RootElement, sample.TargetPuuid);
    }

    private static IReadOnlyList<SgpSample> Load()
    {
        string manifestPath = Path.Combine(Root, "manifest.json");
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("sgp/manifest.json 解析为 null");

        JsonArray samples = manifest["samples"]?.AsArray()
            ?? throw new InvalidOperationException("sgp/manifest.json 缺少 samples 数组");

        var result = new List<SgpSample>(samples.Count);
        foreach (JsonNode? node in samples)
        {
            if (node is null)
            {
                throw new InvalidOperationException("sgp/manifest.json 的 samples 含 null 项");
            }

            result.Add(new SgpSample(
                File: Require<string>(node, "file"),
                GameId: Require<long>(node, "gameId"),
                QueueId: Require<int>(node, "queueId"),
                TargetPuuid: Require<string>(node, "targetPuuid"),
                TargetParticipantIndex: Require<int>(node, "targetParticipantIndex"),
                ChampionId: Require<int>(node, "championId"),
                Kills: Require<int>(node, "kills"),
                Deaths: Require<int>(node, "deaths"),
                Assists: Require<int>(node, "assists"),
                CreepScore: Require<int>(node, "creepScore"),
                Gold: Require<int>(node, "gold"),
                DurationSeconds: Require<int>(node, "durationSeconds"),
                RuneId: Require<int>(node, "runeId"),
                TeamPosition: node["teamPosition"]?.GetValue<string>(),
                Position: node["position"] is { } position
                    ? Enum.Parse<MatchPosition>(position.GetValue<string>())
                    : null,
                Win: Require<bool>(node, "win"),
                Remake: Require<bool>(node, "remake"),
                AugmentIds: node["augmentIds"]?.AsArray().Select(x => x!.GetValue<int>()).ToArray() ?? [],
                LcuFile: node["lcuFile"]?.GetValue<string>()));
        }

        return result;
    }

    private static T Require<T>(JsonNode node, string name)
    {
        JsonNode? value = node[name]
            ?? throw new InvalidOperationException($"sgp/manifest.json 的样本缺少 {name}");

        return value.GetValue<T>();
    }
}
