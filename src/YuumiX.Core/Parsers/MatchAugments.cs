using System.Text.Json;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 海克斯强化 ID 的提取与去重（原 <c>dedupe_augment_ids</c> / <c>extract_augment_ids</c>）。
///
/// 为什么需要去重而不是直接读 <c>stats.augments</c>：
/// <list type="bullet">
///   <item>真实载荷里 <c>stats</c> **没有** <c>augments</c> 数组，强化只能从
///         <c>playerAugment1..6</c> 五个散字段拿（见 manifest 的 observedInRealPayload）；</item>
///   <item>合成载荷两者都有，且内容重叠，不去重会重复渲染同一格；</item>
///   <item>存在 <c>playerAugment6</c>，本应用只建模 1..5，第 6 个按原实现丢弃。</item>
/// </list>
/// 因此规则固定为：<c>augments</c> 数组优先，随后 1..5 散字段，去零、去重、保序、截断到 5 个。
/// </summary>
public static class MatchAugments
{
    /// <summary>最多保留的强化数量（界面一行放得下的格数）。</summary>
    public const int MaxCount = 5;

    /// <summary>去零、去重（保序）、最多 5 个。</summary>
    public static IReadOnlyList<int> Dedupe(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var seen = new HashSet<int>();
        var ordered = new List<int>(MaxCount);

        foreach (int id in ids)
        {
            if (id == 0 || !seen.Add(id))
            {
                continue;
            }

            ordered.Add(id);
            if (ordered.Count == MaxCount)
            {
                break;
            }
        }

        return ordered;
    }

    /// <summary>从 <c>stats</c> 提取（列表口径）。</summary>
    public static IReadOnlyList<int> Extract(JsonElement stats) => Dedupe(Candidates(stats));

    /// <summary>
    /// 从 <c>stats</c> 与 <c>participant</c> 两处提取（详情口径）。
    /// 详情接口把强化同时挂在 <c>stats</c> 与 <c>participant</c> 上，两处都要读。
    /// </summary>
    public static IReadOnlyList<int> Extract(JsonElement stats, JsonElement participant) =>
        Dedupe(Candidates(stats, participant));

    private static IEnumerable<int> Candidates(params JsonElement[] sources)
    {
        foreach (JsonElement source in sources)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (int id in JsonRead.IntArray(source, "augments"))
            {
                yield return id;
            }

            for (int slot = 1; slot <= 5; slot++)
            {
                yield return JsonRead.Int(source, $"playerAugment{slot}");
            }
        }
    }
}
