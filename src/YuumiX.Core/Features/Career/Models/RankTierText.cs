using YuumiX.Core.Localization;

namespace YuumiX.Core.Features.Career.Models;

/// <summary>
/// 段位英文标识 → 中文文案的唯一转换点。
///
/// 原实现把 <c>TIER_MAP</c> 抄了三份（<c>useMatchHistory.ts</c>、<c>MatchHistoryTab.vue</c>、
/// 对局信息页各一份），任何一份漏改就会出现"排位表写超凡大师、详情面板写 MASTER"。
/// 这里收成一个静态类，界面只负责摆放，不再判断段位该怎么写。
///
/// 未知 tier **原样返回**而不是兜底成"--"：LCU 新增段位（如曾短暂出现的
/// <c>EMERALD</c> 之前）时，界面显示英文总比显示"--"更可诊断。
/// </summary>
public static class RankTierText
{
    /// <summary>
    /// 段位名（不含分部）。<c>NONE</c> / <c>UNRANKED</c> / 空值统一为 <c>--</c>，
    /// 与原实现的占位一致。优先根据当前语言本地化。
    /// </summary>
    public static string Tier(string? tier)
    {
        if (string.IsNullOrEmpty(tier) || tier is "NONE" or "UNRANKED")
        {
            return "--";
        }

        var upper = tier.ToUpperInvariant();
        if (I18nService.Current != null)
        {
            var key = $"tools.spoofTier.{upper}";
            var translated = I18nService.Current.T(key);
            if (translated != key)
            {
                return translated;
            }
        }

        return upper switch
        {
            "IRON" => "坚韧黑铁",
            "BRONZE" => "英勇黄铜",
            "SILVER" => "不屈白银",
            "GOLD" => "荣耀黄金",
            "PLATINUM" => "华贵铂金",
            "EMERALD" => "流光翡翠",
            "DIAMOND" => "璀璨钻石",
            "MASTER" => "超凡大师",
            "GRANDMASTER" => "傲世宗师",
            "CHALLENGER" => "最强王者",
            _ => tier,
        };
    }

    /// <summary>
    /// 段位 + 分部（如「荣耀黄金 II」）。
    ///
    /// 分部为 <c>NA</c> 或空时只返回段位名：大师及以上没有分部，
    /// 补一个空格会让文案变成「超凡大师 」这种带尾空格的残形。
    /// </summary>
    public static string Display(string? tier, string? division)
    {
        string tierText = Tier(tier);
        if (tierText == "--")
        {
            return tierText;
        }

        return string.IsNullOrEmpty(division) || division == "NA"
            ? tierText
            : $"{tierText} {division}";
    }

    /// <summary>是否存在真实段位（用于决定要不要画段位图标）。</summary>
    public static bool HasRank(string? tier) =>
        tier is not (null or "" or "NONE" or "UNRANKED");
}
