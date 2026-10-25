using Xunit;
using YuumiX.App.Navigation;

namespace YuumiX.App.Tests;

public sealed class NavigationRouteTests
{
    [Fact]
    public void KeyFor_ParameterizedCareerRoute_ReturnsCareerKey()
    {
        var route = new AppRoute.CareerRoute("test-puuid-1234");
        Assert.Equal("Career", RouteCatalog.KeyFor(route));
    }

    [Fact]
    public void KeyFor_ParameterizedSearchRoute_ReturnsSearchKey()
    {
        var route = new AppRoute.SearchRoute("Test#TAG", "puuid-1234", 123456789L);
        Assert.Equal("Search", RouteCatalog.KeyFor(route));
    }

    [Fact]
    public void RouteGuard_SameModuleParameterizedRoute_DoesNotTriggerDefaultRouteOverride()
    {
        // 模拟：当前已导航到他人生涯
        AppRoute currentRoute = new AppRoute.CareerRoute("other-player-puuid");
        string selectedTag = "Career";

        // 验证：tag 属于同一模块时，守卫条件为 false，不会触发无参 CareerRoute 覆盖
        bool shouldNavigate = RouteCatalog.KeyFor(currentRoute) != selectedTag;
        Assert.False(shouldNavigate);
    }

    [Fact]
    public void RouteGuard_DifferentModule_TriggersNavigation()
    {
        // 模拟：当前在战绩查询，用户点击了侧边栏的"生涯"
        AppRoute currentRoute = new AppRoute.SearchRoute();
        string selectedTag = "Career";

        bool shouldNavigate = RouteCatalog.KeyFor(currentRoute) != selectedTag;
        Assert.True(shouldNavigate);

        var targetRoute = RouteCatalog.Resolve(selectedTag);
        Assert.IsType<AppRoute.CareerRoute>(targetRoute);
    }
}
