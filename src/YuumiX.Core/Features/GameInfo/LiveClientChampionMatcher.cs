using System.Text.RegularExpressions;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// Live Client 英雄名称到 championId 的匹配解析器（纯函数）。
/// 对应旧版 <c>championCache.ts</c> 的 <c>findChampionIdByLiveClientName</c>。
/// </summary>
public static partial class LiveClientChampionMatcher
{
    [GeneratedRegex(@"^game_character_displayname_", RegexOptions.IgnoreCase)]
    private static partial Regex InternalNamePrefixRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex NonAlphaNumericRegex();

    /// <summary>
    /// 将 LiveClientData 的 championName 或 rawChampionName 匹配为 championId。
    /// </summary>
    /// <param name="champions">英雄 ID → 英雄名称映射（来自 GameDataCatalog 或 Assets）</param>
    /// <param name="champName">如 "Ahri", "MonkeyKing", "放逐之刃"</param>
    /// <param name="rawChampName">如 "game_character_displayname_Ahri"</param>
    /// <param name="aliases">可选的英文别名（alias）→ 英雄 ID 映射</param>
    public static int FindChampionId(
        IReadOnlyDictionary<int, string>? champions,
        string? champName,
        string? rawChampName,
        IReadOnlyDictionary<string, int>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(champName) && string.IsNullOrWhiteSpace(rawChampName))
        {
            return 0;
        }

        string nameTrimmed = (champName ?? string.Empty).Trim();
        string rawTrimmed = (rawChampName ?? string.Empty).Trim();

        // 1. 全等匹配（中文名、称号等）
        if (champions is { Count: > 0 })
        {
            if (!string.IsNullOrEmpty(nameTrimmed))
            {
                foreach (var (id, name) in champions)
                {
                    if (string.Equals(name, nameTrimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }
            }

            if (!string.IsNullOrEmpty(rawTrimmed))
            {
                foreach (var (id, name) in champions)
                {
                    if (string.Equals(name, rawTrimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }
            }
        }

        // 2. 清洗英文纯字母数字
        string cleanRaw = string.Empty;
        if (!string.IsNullOrEmpty(rawTrimmed))
        {
            string stripped = InternalNamePrefixRegex().Replace(rawTrimmed, string.Empty);
            cleanRaw = NonAlphaNumericRegex().Replace(stripped, string.Empty).ToLowerInvariant();
        }

        string cleanName = string.Empty;
        if (!string.IsNullOrEmpty(nameTrimmed))
        {
            cleanName = NonAlphaNumericRegex().Replace(nameTrimmed, string.Empty).ToLowerInvariant();
        }

        // 3. 特例别名表优先命中（wukong/monkeyking/drmundo 等）
        if (!string.IsNullOrEmpty(cleanName) && LiveClientSpecialAliases.Map.TryGetValue(cleanName, out int aliasId))
        {
            return aliasId;
        }
        if (!string.IsNullOrEmpty(cleanRaw) && LiveClientSpecialAliases.Map.TryGetValue(cleanRaw, out int rawAliasId))
        {
            return rawAliasId;
        }

        // 4. 别名表查询（如有）
        if (aliases is { Count: > 0 })
        {
            if (!string.IsNullOrEmpty(cleanName) && aliases.TryGetValue(cleanName, out int aId))
            {
                return aId;
            }
            if (!string.IsNullOrEmpty(cleanRaw) && aliases.TryGetValue(cleanRaw, out int raId))
            {
                return raId;
            }
        }

        // 5. 清洗比对已有字典（如果字典里的 key/value 包含英文 alias）
        if (champions is { Count: > 0 })
        {
            foreach (var (id, name) in champions)
            {
                string cleanChamp = NonAlphaNumericRegex().Replace(name, string.Empty).ToLowerInvariant();
                if (!string.IsNullOrEmpty(cleanName) && string.Equals(cleanChamp, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
                if (!string.IsNullOrEmpty(cleanRaw) && string.Equals(cleanChamp, cleanRaw, StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }

            // 6. 中文模糊包含（限制长度 >= 2，避免单字误伤）
            if (nameTrimmed.Length >= 2)
            {
                foreach (var (id, name) in champions)
                {
                    if (name.Contains(nameTrimmed, StringComparison.OrdinalIgnoreCase) ||
                        nameTrimmed.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }
            }
        }

        return 0;
    }
}
