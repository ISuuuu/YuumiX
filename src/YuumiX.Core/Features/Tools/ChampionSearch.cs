using System.Text;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 英雄搜索匹配的统一入口（英雄选择器、禁用候选库共用）。
///
/// <para>匹配范围：英雄名称、称号、英文别名、英雄 id，以及别名索引里的
/// 中文别称 / 拼音全拼 / 拼音首字母。因此「瞎子」「xiazi」「lq」「leesin」都能命中李青，
/// 「vn」命中薇恩，「yasuo」命中亚索，「901」命中斯莫德。</para>
///
/// <para><b>别名来源两级</b>：默认用内置基线 <see cref="BuiltinKeywords"/>；
/// 传入 <see cref="IChampionAliasProvider"/> 时以它为准（内置基线 + 磁盘缓存 + 在线刷新）。</para>
///
/// <para><b>规范化规则</b>：查询串与候选串两侧都只保留字母、数字与汉字并转小写，
/// 于是「Lee Sin」「lee sin」「LEESIN」等价，「烈娜塔 · 戈拉斯克」也能被「烈娜塔戈拉斯克」命中。
/// 该规则只放宽空白与标点，不做编辑距离等模糊匹配——避免「输错一个字就乱序」的观感。</para>
/// </summary>
public static class ChampionSearch
{
    /// <summary>内置基线索引（id → 规范化关键词），编译期数据，进程内构建一次。</summary>
    internal static IReadOnlyDictionary<int, string> BuiltinKeywords { get; } = BuildBuiltinKeywords();

    /// <summary>单个英雄条目是否命中查询串（空查询视为全部命中）。</summary>
    public static bool Matches(ChampionSummaryItem champion, string? query, IChampionAliasProvider? aliases = null) =>
        Matches(champion.Id, champion.Name, champion.Alias, champion.Title, query, aliases);

    /// <summary>按字段匹配，便于对非 <see cref="ChampionSummaryItem"/> 来源的条目复用同一规则。</summary>
    public static bool Matches(
        int id,
        string? name,
        string? alias,
        string? title,
        string? query,
        IChampionAliasProvider? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string q = Normalize(query);
        if (q.Length == 0)
        {
            // 只输入了空格/标点：等价于不筛选
            return true;
        }

        if (q == id.ToString())
        {
            return true;
        }

        if (Normalize(name).Contains(q, StringComparison.Ordinal)
            || Normalize(alias).Contains(q, StringComparison.Ordinal)
            || Normalize(title).Contains(q, StringComparison.Ordinal))
        {
            return true;
        }

        // 优先用运行时索引（含在线刷新的新英雄），拿不到再退回内置基线
        string? keywords = aliases?.GetKeywords(id);
        if (keywords is null && !BuiltinKeywords.TryGetValue(id, out keywords))
        {
            return false;
        }

        return keywords.Contains(q, StringComparison.Ordinal);
    }

    /// <summary>只保留字母、数字与汉字并转小写；用于抹平空白、分隔符与大小写差异。</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static Dictionary<int, string> BuildBuiltinKeywords()
    {
        var map = new Dictionary<int, string>(
            ChampionAliasTable.Keywords.Count + ChampionAliasTable.CommunityPatches.Count);

        foreach (var (id, keywords) in ChampionAliasTable.Keywords)
        {
            map[id] = Normalize(keywords);
        }

        foreach (var (id, patch) in ChampionAliasTable.CommunityPatches)
        {
            string normalized = Normalize(patch);
            if (normalized.Length == 0)
            {
                continue;
            }

            map[id] = map.TryGetValue(id, out string? existing) && existing.Length > 0
                ? existing + normalized
                : normalized;
        }

        return map;
    }
}
