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
            "召唤师近期战绩概览",
            "资产统计",
            "英雄熟练度 2.0（里程碑/成就/等级排行）",
            "挑战勋章装配",
            "战利品入口",
        ]),
        [AppRoute.Search] = ("战绩查询", "src/views/Search.vue",
        [
            "搜索历史（当前只保留最近一次查询）",
            "详情面板段位与近期战绩后台加载",
            "列表翻页（LCU 本机仅缓存 20 局，需走 SGP 才能看到更早的对局）",
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
            "好友清理与离线对局时间探测",
            "SGP 凭据提取与命令行独立进程直接观战",
            "英雄熟练度与分路详细选择器弹窗",
            "召唤师技能与符文出装一键导入",
        ]),
        [AppRoute.Settings] = ("设置", "src/views/Settings.vue",
        [
            "云端服务（SignalR Hub 远程推送与对局上传）",
            "在线更新（增量/便携包下载与安装）",
            "客户端路径多目录管理弹窗",
        ]),
    };

    public static (string Title, string SourceView, string[] Pending) For(AppRoute route) => Entries[route];
}
