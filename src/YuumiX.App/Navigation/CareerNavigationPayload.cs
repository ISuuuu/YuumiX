using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Navigation;

/// <summary>
/// 跨页面生涯跳转载荷，对应原 Vue 的 <c>navigateCareerPayload</c>。
/// 用于从最近队友卡片、战绩详情玩家名等入口打开**他人**的生涯页（需求 §2.8）。
/// </summary>
public sealed class CareerNavigationPayload
{
    public static CareerNavigationPayload Instance { get; } = new();

    /// <summary>目标玩家 puuid；为 <c>null</c> 表示无待处理跳转。</summary>
    public string? PendingPuuid { get; private set; }

    /// <summary>可选的预载资料（队友卡片已知的名字与头像），让页面先渲染基础信息再拉权威数据。</summary>
    public SummonerProfile? PendingSummoner { get; private set; }

    public void Set(string puuid, SummonerProfile? summoner = null)
    {
        PendingPuuid = puuid;
        PendingSummoner = summoner;
    }

    public (string? Puuid, SummonerProfile? Summoner) Consume()
    {
        var result = (PendingPuuid, PendingSummoner);
        PendingPuuid = null;
        PendingSummoner = null;
        return result;
    }
}
