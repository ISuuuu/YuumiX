using System.Text.RegularExpressions;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 玩家身份识别与兼容性检测工具（纯函数）。
/// 对应旧版 <c>playerIdentity.ts</c> 与 <c>championCache.ts</c>。
/// </summary>
public static partial class PlayerIdentity
{
    public const int NewPlayerMaxLevel = 30;

    [GeneratedRegex(@"^(玩家|召唤师|player|summoner)\s*[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderNameRegex();

    [GeneratedRegex(@"(\s*电脑|\s+bot)$|^电脑\s*[0-9]*$", RegexOptions.IgnoreCase)]
    private static partial Regex BotNameRegex();

    [GeneratedRegex(@"^game_character_displayname_", RegexOptions.IgnoreCase)]
    private static partial Regex InternalCodeRegex();

    /// <summary>
    /// 槽位英雄 ID 继承：保留已有英雄 ID，只有条目与 incoming 身份明确冲突时才严禁串用。
    /// </summary>
    public static int InheritPlaceholderChampion(
        GameInfoPlayer? entry,
        int cellId,
        string? incomingPuuid = null,
        long incomingSummonerId = 0)
    {
        if (entry is null || entry.ChampionId <= 0)
        {
            return 0;
        }

        string inPuuid = (incomingPuuid ?? string.Empty).Trim();
        bool inSidReal = incomingSummonerId != 0 && incomingSummonerId != cellId;

        string ePuuid = (entry.Info?.Puuid ?? string.Empty).Trim();
        long eRawSid = entry.Info?.SummonerId ?? 0;
        long eSid = eRawSid == cellId ? 0 : eRawSid;

        if (!string.IsNullOrEmpty(inPuuid) && !string.IsNullOrEmpty(ePuuid) && !string.Equals(inPuuid, ePuuid, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (inSidReal && eSid != 0 && incomingSummonerId != eSid)
        {
            return 0;
        }

        return entry.ChampionId;
    }

    /// <summary>
    /// 提取有效的 teamParticipantId 或 partyId（"0", "", null 视为无效）。
    /// </summary>
    public static string? GetValidTpid(PremadePlayerLike? p)
    {
        if (p is null)
        {
            return null;
        }

        string? tpid = p.TeamParticipantId?.Trim();
        if (!string.IsNullOrEmpty(tpid) && tpid != "0")
        {
            return tpid;
        }

        string? partyId = p.PartyId?.Trim();
        if (!string.IsNullOrEmpty(partyId) && partyId != "0")
        {
            return partyId;
        }

        return null;
    }

    /// <summary>
    /// 身份门禁：incoming 的真实身份键与现有条目是否兼容。
    /// 严防脱敏匿名玩家复用已有实名玩家的数据。
    /// </summary>
    public static bool IsIdentityCompatible(
        GameInfoPlayer? entry,
        string? incomingPuuid,
        long incomingSummonerId,
        int incomingCellId)
    {
        if (entry?.Info is null)
        {
            return false;
        }

        string inPuuid = (incomingPuuid ?? string.Empty).Trim();
        bool inSidReal = incomingSummonerId != 0 && incomingSummonerId != incomingCellId;

        string ePuuid = (entry.Info.Puuid ?? string.Empty).Trim();
        long eRawSid = entry.Info.SummonerId;
        long eSid = eRawSid == incomingCellId ? 0 : eRawSid;

        if (!string.IsNullOrEmpty(inPuuid) && !string.IsNullOrEmpty(ePuuid) &&
            !string.Equals(inPuuid, ePuuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (inSidReal && eSid != 0 && incomingSummonerId != eSid)
        {
            return false;
        }

        bool incomingHasIdentity = !string.IsNullOrEmpty(inPuuid) || inSidReal;
        bool entryHasIdentity = !string.IsNullOrEmpty(ePuuid) || eSid != 0;

        return incomingHasIdentity == entryHasIdentity;
    }

    /// <summary>
    /// 校验一个名字是否是合法的真实召唤师名称。
    /// 排除排位匿名占位名（如“玩家1”~“玩家10”）、电脑人机、复合英雄全称（如“迅捷斥候提莫”）及内部代号。
    /// </summary>
    public static bool IsRealSummonerName(
        string? name,
        bool isBot = false,
        bool hasIdentity = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (isBot)
        {
            return false;
        }

        string trimmed = name.Trim();
        int hashIdx = trimmed.IndexOf('#');
        string pureName = hashIdx >= 0 ? trimmed[..hashIdx].Trim() : trimmed;
        string tagLine = hashIdx >= 0 ? trimmed[(hashIdx + 1)..].Trim() : string.Empty;

        if (string.IsNullOrEmpty(pureName))
        {
            return false;
        }

        if (PlaceholderNameRegex().IsMatch(pureName))
        {
            return false;
        }

        if (BotNameRegex().IsMatch(pureName))
        {
            return false;
        }

        if (InternalCodeRegex().IsMatch(pureName))
        {
            return false;
        }

        // 拥有 Riot ID #tagLine 或显式真实身份时，即使名字叫“提莫”也是真实玩家
        if (!string.IsNullOrEmpty(tagLine) || hasIdentity)
        {
            return true;
        }

        return true;
    }

    /// <summary>
    /// 标准化查询名称：去除 #tag、去除空格、转小写。
    /// </summary>
    public static string NormalizeLookupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        int hashIdx = trimmed.IndexOf('#');
        if (hashIdx >= 0)
        {
            trimmed = trimmed[..hashIdx].Trim();
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// 判断是否为排位匿名占位名或电脑名（不可点击跳转搜索或生涯）。
    /// </summary>
    public static bool IsPlaceholderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        string trimmed = name.Trim();
        if (trimmed == "未知")
        {
            return true;
        }

        int hashIdx = trimmed.IndexOf('#');
        string pureName = hashIdx >= 0 ? trimmed[..hashIdx].Trim() : trimmed;

        return PlaceholderNameRegex().IsMatch(pureName) || BotNameRegex().IsMatch(pureName);
    }

    /// <summary>
    /// 提取可用于跨页面搜索/查询的规范化名称（<c>name#tag</c>）。
    /// 若为占位名或空，返回空字符串。
    /// </summary>
    public static string GetSearchName(PremadePlayerLike p, GameInfoPlayer? d)
    {
        var info = d?.Info;
        string raw = !string.IsNullOrWhiteSpace(info?.GameName) ? info.GameName
            : !string.IsNullOrWhiteSpace(info?.DisplayName) ? info.DisplayName
            : !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName
            : !string.IsNullOrWhiteSpace(p.SummonerName) ? p.SummonerName
            : !string.IsNullOrWhiteSpace(p.GameName) ? p.GameName
            : string.Empty;

        if (string.IsNullOrWhiteSpace(raw) || IsPlaceholderName(raw))
        {
            return string.Empty;
        }

        if (raw.Contains('#'))
        {
            return raw;
        }

        string tag = !string.IsNullOrWhiteSpace(info?.TagLine) ? info.TagLine
            : !string.IsNullOrWhiteSpace(p.TagLine) ? p.TagLine
            : string.Empty;

        return string.IsNullOrEmpty(tag) ? raw : $"{raw}#{tag}";
    }

    /// <summary>
    /// 提取界面显示的玩家名（去除 <c>#tag</c> 部分）。
    /// </summary>
    public static string GetDisplayName(PremadePlayerLike p, GameInfoPlayer? d, int index)
    {
        var info = d?.Info;
        string raw = !string.IsNullOrWhiteSpace(info?.GameName) ? info.GameName
            : !string.IsNullOrWhiteSpace(info?.DisplayName) ? info.DisplayName
            : !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName
            : !string.IsNullOrWhiteSpace(p.GameName) ? p.GameName
            : !string.IsNullOrWhiteSpace(p.SummonerName) ? p.SummonerName
            : string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"玩家{index + 1}";
        }

        int hash = raw.IndexOf('#');
        return hash >= 0 ? raw[..hash] : raw;
    }
}
