using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Navigation;

/// <summary>
/// 跨页面生涯跳转载荷，对应原 Vue 的 <c>navigateCareerPayload</c>。
/// 用于从对局信息玩家卡片等页面跳转到生涯页并自动加载目标玩家数据。
/// </summary>
public sealed class CareerNavigationPayload
{
    public static CareerNavigationPayload Instance { get; } = new();

    public string? PendingPuuid { get; set; }
    public SummonerProfile? PendingProfile { get; set; }

    public void Set(string puuid, SummonerProfile? profile = null)
    {
        PendingPuuid = puuid;
        PendingProfile = profile;
    }

    public (string? Puuid, SummonerProfile? Profile) Consume()
    {
        var result = (PendingPuuid, PendingProfile);
        PendingPuuid = null;
        PendingProfile = null;
        return result;
    }
}
