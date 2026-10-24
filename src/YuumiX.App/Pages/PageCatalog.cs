using YuumiX.App.Navigation;

namespace YuumiX.App.Pages;

/// <summary>
/// 每个路由对应的原 Vue 视图与待迁移功能清单。
///
/// 这是迁移进度的唯一事实来源：某个页面的 <see cref="PendingFeatures"/> 清空时，
/// 即代表该模块已完成迁移，可以把占位替换为真实组件。
/// </summary>
public static class PageCatalog
{
    private static readonly Dictionary<AppRoute, (string Title, string SourceView, string[] Pending)> Entries = new()
    {
        [AppRoute.Home] = ("主页", "src/views/Home.vue",
        [
            "快捷入口与最近搜索",
            "公告与更新提示",
        ]),
        [AppRoute.Career] = ("生涯", "src/views/Career.vue",
        [
            "挑战勋章装配",
            "资产统计",
            "战利品进度面板完整化（线形进度条+逐条结果列表，P2）",
            "战利品 LCU 真实图标替换 emoji 缩略（P2）",
        ]),
        [AppRoute.Search] = ("战绩查询", "src/views/Search.vue",
        [
            "战绩上传（可选云端同步）",
        ]),
        [AppRoute.GameInfo] = ("对局信息", "src/views/GameInfo.vue",
        [
        ]),
        [AppRoute.SavedPlayers] = ("路人集", "src/views/SavedPlayers.vue",
        [
            "选人/对局聊天室自动提醒",
            "JSON 导入导出",
        ]),
        [AppRoute.Tft] = ("云顶之弈", "src/views/TFT.vue",
        [
            "战绩解析与详情弹窗",
            "段位近况",
            "OP.GG 热门阵容与站位图",
            "海克斯强化百科",
        ]),
        [AppRoute.Tools] = ("工具箱", "src/views/Tools.vue",
        [
            "大乱斗板凳席独立悬浮窗（阶段 8）",
        ]),
        [AppRoute.Settings] = ("设置", "src/views/Settings.vue",
        [
        ]),
        [AppRoute.Notice] = ("公告", "src/components/layout/NoticePopup.vue",
        [
            "从 GitHub 拉取 NOTICE.md 并弹窗展示",
            "公告内容缓存与版本比对",
        ]),
    };

    public static (string Title, string SourceView, string[] Pending) For(AppRoute route) => Entries[route];
}
