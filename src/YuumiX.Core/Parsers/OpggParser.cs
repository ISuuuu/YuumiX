using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using YuumiX.Core.Features.Opgg.Models;

namespace YuumiX.Core.Parsers;

/// <summary>
/// OP.GG 与 LCU 符文 JSON 纯函数解析器。
/// </summary>
public static partial class OpggParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("[ \\t]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex("(\\r?\\n){3,}")]
    private static partial Regex MultiNewlineRegex();

    /// <summary>
    /// 解析 OP.GG 梯队列表响应，按模式与分路归一化并按 Rank 升序排列。
    /// </summary>
    public static IReadOnlyList<TierListItem> ParseTierList(string json, string mode, string? position = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var payload = JsonSerializer.Deserialize<OpggTierPayload>(json, JsonOptions);
            var rawList = payload?.Data;
            if (rawList is null || rawList.Count == 0)
            {
                return [];
            }

            var result = new List<TierListItem>(rawList.Count);

            if (string.Equals(mode, OpggMode.Ranked, StringComparison.OrdinalIgnoreCase))
            {
                var targetPos = string.IsNullOrWhiteSpace(position) ? OpggPosition.Mid : position.ToUpperInvariant();

                foreach (var champ in rawList)
                {
                    if (champ.Positions is null) continue;

                    var posEntry = champ.Positions.FirstOrDefault(p =>
                        string.Equals(p.Name, targetPos, StringComparison.OrdinalIgnoreCase));

                    if (posEntry?.Stats is null) continue;

                    var stats = posEntry.Stats;
                    var counters = posEntry.Counters?
                        .Select(c => c.ChampionId)
                        .Where(id => id > 0)
                        .ToList() ?? [];

                    result.Add(new TierListItem
                    {
                        Id = champ.Id,
                        Name = champ.Name,
                        WinRate = stats.WinRate,
                        PickRate = stats.PickRate,
                        BanRate = stats.BanRate,
                        Kda = stats.Kda,
                        Tier = stats.TierData?.Tier ?? stats.Tier,
                        Rank = stats.TierData?.Rank ?? stats.Rank,
                        Position = targetPos,
                        Counters = counters
                    });
                }
            }
            else
            {
                foreach (var champ in rawList)
                {
                    if (champ.AverageStats is null) continue;

                    var stats = champ.AverageStats;
                    result.Add(new TierListItem
                    {
                        Id = champ.Id,
                        Name = champ.Name,
                        WinRate = stats.WinRate,
                        PickRate = stats.PickRate,
                        BanRate = stats.BanRate,
                        Kda = stats.Kda,
                        Tier = stats.TierData?.Tier ?? stats.Tier,
                        Rank = stats.TierData?.Rank ?? stats.Rank,
                        Position = null,
                        Counters = []
                    });
                }
            }

            result.Sort((a, b) => (a.Rank ?? 999).CompareTo(b.Rank ?? 999));
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 解析单英雄出装响应负载。
    /// </summary>
    public static OpggBuildPayload? ParseBuildPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<OpggBuildPayload>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析单英雄出装详情数据。
    /// </summary>
    public static OpggBuildData? ParseBuildData(string json)
    {
        return ParseBuildPayload(json)?.Data;
    }

    /// <summary>
    /// 解析 LCU /lol-game-data/assets/v1/perkstyles.json（兼容顶级数组或 { styles: [...] } 两种形态）。
    /// </summary>
    public static IReadOnlyList<PerkStyle> ParsePerkStyles(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                return root.Deserialize<List<PerkStyle>>(JsonOptions) ?? [];
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("styles", out var stylesEl) &&
                stylesEl.ValueKind == JsonValueKind.Array)
            {
                return stylesEl.Deserialize<List<PerkStyle>>(JsonOptions) ?? [];
            }

            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 解析 LCU /lol-game-data/assets/v1/perks.json 详情并清洗 HTML 描述。
    /// </summary>
    public static IReadOnlyDictionary<int, PerkDetail> ParsePerkDetails(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, PerkDetail>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<int, PerkDetail>();
            }

            var dict = new Dictionary<int, PerkDetail>();
            foreach (var el in root.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id))
                {
                    continue;
                }

                var name = el.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var iconPath = el.TryGetProperty("iconPath", out var iconProp) ? iconProp.GetString() ?? "" : "";
                var shortDesc = el.TryGetProperty("shortDesc", out var descProp) ? descProp.GetString() ?? "" : "";
                var cleanedDesc = CleanHtml(shortDesc);

                dict[id] = new PerkDetail(id, name, iconPath, shortDesc, cleanedDesc);
            }

            return dict;
        }
        catch
        {
            return new Dictionary<int, PerkDetail>();
        }
    }

    /// <summary>
    /// 剥离 HTML 标签、保留换行并反转义 HTML 实体。
    /// </summary>
    public static string CleanHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // 1. 将 <br> 替换为换行符
        var text = BrTagRegex().Replace(html, "\n");

        // 2. 剥离所有其余 HTML / XML 标签
        text = HtmlTagRegex().Replace(text, "");

        // 3. 反转义 HTML 实体（如 &nbsp; / &lt; / &amp;）
        text = WebUtility.HtmlDecode(text);

        // 4. 清理连续空格与过多换行
        text = MultiSpaceRegex().Replace(text, " ");
        text = MultiNewlineRegex().Replace(text, "\n\n");

        return text.Trim();
    }
}
