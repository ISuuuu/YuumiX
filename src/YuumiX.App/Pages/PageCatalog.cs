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
            "LCU 客户端启动与连接状态",
            "快捷入口与最近搜索",
            "公告与更新提示",
        ]),
        [AppRoute.Career] = ("生涯", "src/views/Career.vue",
        [
            "召唤师近期战绩概览",
            "资产统计",
            "英雄熟练度 2.0（里程碑/成就/等级排行）",
            "挑战勋章装配",
            "战利品入口",
        ]),
        [AppRoute.Search] = ("战绩查询", "src/views/Search.vue",
        [
            "召唤师搜索与历史",
            "对局历史列表（经典/大乱斗/海克斯强化）",
            "对局详情面板（10 人数据）",
            "ROFL 回放下载与客户端观看",
        ]),
        [AppRoute.GameInfo] = ("对局信息", "src/views/GameInfo.vue",
        [
            "实时 10 人段位与近期 KDA",
            "阵营组队分析",
            "宿命对局深度回溯",
            "曾友/曾敌徽标互斥展示",
        ]),
        [AppRoute.SavedPlayers] = ("路人集", "src/views/SavedPlayers.vue",
        [
            "同局玩家自动记录与频次统计",
            "自定义标签/备注",
            "标记置顶与筛选",
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
            "好友清理",
            "OP.GG 符文与出装导入",
            "直接观战",
            "自定义 5v5 房间",
            "客户端缩放与黑边修复",
            "游戏设置防篡改锁定",
        ]),
        [AppRoute.Settings] = ("设置", "src/views/Settings.vue",
        [
            "常规（客户端路径/代理/日志/截图目录）",
            "个性化（主题色/语言/卡片配色）",
            "功能开关与候选列表",
            "云端服务",
            "更新与关于",
        ]),
    };

    public static (string Title, string SourceView, string[] Pending) For(AppRoute route) => Entries[route];
}
