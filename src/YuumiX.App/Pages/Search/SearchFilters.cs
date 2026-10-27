using YuumiX.Core.Features.Match.Models;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩列表的队列筛选纯函数（自 <c>SearchPage</c> 拆出，R14）。
/// 结果集由调用方 <c>UseMemo</c> 缓存引用，避免每帧产出新集合导致卡片反复重建。
/// </summary>
internal static class SearchFilters
{
    /// <summary>战绩查询游戏模式预置选项（对齐旧版 Search.vue 与客户端主流队列）。</summary>
    internal static readonly (string Label, Func<MatchSummary, bool> Matcher)[] QueueDefs =
    [
        ("全部", _ => true),
        ("匹配模式", m => m.QueueId == 430 || m.QueueName.Contains("匹配", StringComparison.Ordinal)),
        ("单双排位", m => m.QueueId == 420 || m.QueueName.Contains("单双", StringComparison.Ordinal)),
        ("灵活排位", m => m.QueueId == 440 || m.QueueName.Contains("灵活", StringComparison.Ordinal)),
        ("极地大乱斗", m => m.QueueId == 450 || m.QueueName.Contains("大乱斗", StringComparison.Ordinal)),
        ("海克斯大乱斗", m => m.QueueId == 2400),
        ("经典海斗", m => m.QueueId == 2450),
        ("斗魂竞技场", m => m.QueueId is 1700 or 1710 || m.QueueName.Contains("斗魂", StringComparison.Ordinal)),
        ("快速模式", m => m.QueueId is 480 or 490 || m.QueueName.Contains("快速", StringComparison.Ordinal)),
        ("人机对战", m => (m.QueueId >= 800 && m.QueueId <= 850) || m.QueueName.Contains("人机", StringComparison.Ordinal)),
        ("自定义模式", m => m.QueueId is 0 or 3100 || m.QueueName.Contains("自定义", StringComparison.Ordinal)),
    ];

    internal static string TranslateQueueLabel(string label, bool isEn)
    {
        if (!isEn) return label;
        return label switch
        {
            "全部" => "All",
            "匹配模式" => "Normal",
            "单双排位" => "Ranked Solo",
            "灵活排位" => "Ranked Flex",
            "极地大乱斗" => "ARAM",
            "海克斯大乱斗" => "Hextech ARAM",
            "经典海斗" => "Classic ARAM",
            "斗魂竞技场" => "Arena",
            "快速模式" => "Quickplay",
            "人机对战" => "Co-op vs AI",
            "自定义模式" => "Custom",
            _ => label,
        };
    }

    /// <summary>
    /// 筛选下拉的选项：预置主流模式（全部、匹配模式、单双排位、灵活排位、极地大乱斗等），
    /// 同时去重追加列表中出现的特殊/娱乐模式。
    /// </summary>
    internal static string[] BuildLabels(IReadOnlyList<MatchSummary> matches)
    {
        var isEn = Composition.AppHost.Services?.I18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;
        var labels = new List<string>(QueueDefs.Length);
        foreach (var (label, _) in QueueDefs)
        {
            labels.Add(TranslateQueueLabel(label, isEn));
        }

        foreach (MatchSummary summary in matches)
        {
            if (!string.IsNullOrEmpty(summary.QueueName)
                && !labels.Contains(summary.QueueName, StringComparer.Ordinal)
                && !QueueDefs.Any(def => def.Matcher(summary)))
            {
                labels.Add(TranslateQueueLabel(summary.QueueName, isEn));
            }
        }

        return labels.ToArray();
    }

    /// <summary>按队列筛选出可见列表。</summary>
    internal static IReadOnlyList<MatchSummary> Apply(
        IReadOnlyList<MatchSummary> matches,
        string[] labels,
        int filterIndex)
    {
        if (filterIndex <= 0 || filterIndex >= labels.Length)
        {
            return matches;
        }

        Func<MatchSummary, bool> predicate;
        if (filterIndex < QueueDefs.Length)
        {
            predicate = QueueDefs[filterIndex].Matcher;
        }
        else
        {
            string queueName = labels[filterIndex];
            predicate = m => string.Equals(m.QueueName, queueName, StringComparison.Ordinal);
        }

        var filtered = new List<MatchSummary>(matches.Count);
        foreach (MatchSummary summary in matches)
        {
            if (predicate(summary))
            {
                filtered.Add(summary);
            }
        }

        return filtered;
    }

    /// <summary>按 gameId 定位列表序号；找不到返回 -1。</summary>
    internal static int IndexOf(IReadOnlyList<MatchSummary> matches, long gameId)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].GameId == gameId)
            {
                return i;
            }
        }

        return -1;
    }
}
