using System.Text.Json;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.Core.Parsers;

/// <summary>
/// 召唤师解析：<c>/lol-summoner/v1/current-summoner</c> 原始 JSON → <see cref="SummonerProfile"/>。
///
/// 对应原项目 <c>summoner.rs</c> 的 <c>LcuSummoner::to_display</c>。原实现用 <c>?</c> 把
/// <c>accountId</c> / <c>displayName</c> / <c>puuid</c> / <c>summonerId</c> 四个字段缺失的情况
/// 变成 <c>None</c>，由调用方报"召唤师数据不完整"。这里保持一致：任一缺失即返回 <c>null</c>，
/// 而**不是**产出一个 ID 为 0 的假对象——后者会让后续所有按 puuid 的查询静默打空。
/// </summary>
public static class SummonerParser
{
    /// <summary>
    /// 解析当前召唤师。
    /// </summary>
    /// <returns>召唤师；四个关键字段任一缺失时返回 <c>null</c>。</returns>
    public static SummonerProfile? ParseSummoner(JsonElement summoner)
    {
        if (summoner.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long? accountId = JsonRead.AsLong(JsonRead.Property(summoner, "accountId"));
        string? displayName = JsonRead.String(summoner, "displayName");
        string? puuid = JsonRead.String(summoner, "puuid");
        long? summonerId = JsonRead.AsLong(JsonRead.Property(summoner, "summonerId"));

        if (accountId is null || summonerId is null || string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(puuid))
        {
            return null;
        }

        return new SummonerProfile
        {
            AccountId = accountId.Value,
            DisplayName = displayName,

            // 老账号（Riot ID 体系之前）没有 gameName，只有 displayName。
            // 这里用 displayName 兜底，而不是留空：<see cref="SummonerProfile.RiotId"/> 只由
            // GameName/TagLine 拼装，留空会让这类账号在界面上显示为空白名字。
            GameName = JsonRead.String(summoner, "gameName") ?? displayName,
            TagLine = JsonRead.String(summoner, "tagLine") ?? string.Empty,
            Puuid = puuid,
            SummonerId = summonerId.Value,
            SummonerLevel = JsonRead.Int(summoner, "summonerLevel"),
            ProfileIconId = JsonRead.Int(summoner, "profileIconId"),
            PercentCompleteForNextLevel = JsonRead.Int(summoner, "percentCompleteForNextLevel"),
            XpSinceLastLevel = JsonRead.Long(summoner, "xpSinceLastLevel"),
            XpUntilNextLevel = JsonRead.Long(summoner, "xpUntilNextLevel"),
            Privacy = JsonRead.String(summoner, "privacy"),
        };
    }
}
