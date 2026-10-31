using Microsoft.UI.Reactor.Navigation;
using Windows.ApplicationModel.DataTransfer;
using YuumiX.App.Navigation;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner.Models;

namespace YuumiX.App.Pages.Career;

/// <summary>生涯页的导航命令（自 <c>CareerPage</c> 拆出，R14）：跨页跳转与查看目标切换。</summary>
internal static class CareerCommands
{
    /// <summary>最近队友点击（裁决 D9）：有 puuid 留在生涯页打开他人生涯（预载占位资料）；无 puuid 跳战绩查询。</summary>
    internal static void OnClickTeammate(
        RecentTeammate tm,
        Action<int> setQueueFilter,
        Action<CareerViewTarget?> setViewTarget,
        Action<CareerTab> setTab,
        NavigationHandle<AppRoute> nav)
    {
        if (!string.IsNullOrEmpty(tm.Puuid))
        {
            setQueueFilter(0);
            setViewTarget(new CareerViewTarget(tm.Puuid, CareerLoaders.PlaceholderSummoner(tm.Puuid, tm.Name, tm.ProfileIconId)));
            setTab(CareerTab.Matches);
            return;
        }

        nav.Navigate(new AppRoute.SearchRoute(tm.Name, tm.Puuid));
    }

    /// <summary>返回我的生涯：清空查看目标走现有加载链路（召唤师走 5 分钟缓存）。</summary>
    internal static void BackToMyCareer(
        Action<int> setQueueFilter,
        Action<CareerViewTarget?> setViewTarget,
        Action<CareerTab> setTab)
    {
        setQueueFilter(0);
        setViewTarget(null);
        setTab(CareerTab.Matches);
    }

    /// <summary>Career → Search 跳转（§4）：历史战绩按钮 = 只搜索（gameId: -1）。</summary>
    internal static void GoToHistory(NavigationHandle<AppRoute> nav, SummonerProfile? summoner)
    {
        if (summoner is not null)
        {
            nav.Navigate(new AppRoute.SearchRoute(summoner.RiotId, summoner.Puuid, GameId: -1));
        }
    }

    /// <summary>Career → Search 跳转（§4）：对局卡片点击 = 搜索本人并选中该局（gameId > 0）。</summary>
    internal static void OpenMatchInSearch(NavigationHandle<AppRoute> nav, SummonerProfile? summoner, MatchSummary match)
    {
        if (summoner is not null)
        {
            nav.Navigate(new AppRoute.SearchRoute(summoner.RiotId, summoner.Puuid, GameId: match.GameId));
        }
    }

    /// <summary>复制 Riot ID 到剪贴板。</summary>
    internal static void CopyRiotId(SummonerProfile summoner, Action<bool> setCopied)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(summoner.RiotId);
            Clipboard.SetContent(package);
            setCopied(true);
        }
        catch (Exception)
        {
            setCopied(false);
        }
    }
}
