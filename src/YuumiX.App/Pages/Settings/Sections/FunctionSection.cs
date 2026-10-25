using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>
/// 设置页「功能」分组：LCU API 并发数 / 默认对局数量 / 对局信息过滤 / 保留对局信息 / 段位显示。
/// 写回统一经 <see cref="SettingsUi.Commit"/>（先改内存配置再落盘，见页面 Commit）。
/// </summary>
internal static class FunctionSection
{
    private const int CareerGamesMin = 1;
    private const int CareerGamesMax = 50;
    private const int ApiConcurrencyMin = 1;
    private const int ApiConcurrencyMax = 8;

    public static Element Render(SettingsUi ui)
    {
        var config = ui.Config;

        return VStack(8,
            SettingsCardKit.Stepper("LCU API 并发数", "与客户端通信的并发请求数（推荐 5，范围 1–8）。过大会导致客户端闪退 · 重启后生效", Math.Clamp(config.Functions.ApiConcurrencyNumber, ApiConcurrencyMin, ApiConcurrencyMax), ApiConcurrencyMin, ApiConcurrencyMax, v =>
            {
                config.Functions.ApiConcurrencyNumber = v;
                ui.Commit(nameof(FunctionsConfig.ApiConcurrencyNumber));
            }, ui.Palette),
            SettingsCardKit.Stepper("默认对局数量", "调整在个人生涯界面中显示的最大对局数量（范围 1–50）", Math.Clamp(config.Functions.CareerGamesNumber, CareerGamesMin, CareerGamesMax), CareerGamesMin, CareerGamesMax, v =>
            {
                config.Functions.CareerGamesNumber = v;
                ui.Commit(nameof(FunctionsConfig.CareerGamesNumber));
            }, ui.Palette),
            SettingsCardKit.Toggle("对局信息过滤", "基于你当前游戏模式（地图/队列）筛选战绩，只显示相同模式的玩家历史数据", config.Functions.GameInfoFilter, v =>
            {
                config.Functions.GameInfoFilter = v;
                ui.Commit(nameof(FunctionsConfig.GameInfoFilter));
            }),
            SettingsCardKit.Toggle("保留对局信息界面内容", "保留上一局的对局信息内容直到下一次对局开始", config.Functions.EnableReserveGameinfo, v =>
            {
                config.Functions.EnableReserveGameinfo = v;
                ui.Commit(nameof(FunctionsConfig.EnableReserveGameinfo));
            }),
            SettingsCardKit.Toggle("对局详情中显示段位", "在搜索界面对局详情界面中显示段位，启动该选项将影响加载界面的速度", config.Functions.ShowTierInGameInfo, v =>
            {
                config.Functions.ShowTierInGameInfo = v;
                ui.Commit(nameof(FunctionsConfig.ShowTierInGameInfo));
            }));
    }
}
