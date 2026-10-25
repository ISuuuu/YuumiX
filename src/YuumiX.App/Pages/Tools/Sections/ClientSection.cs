using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Tools.Sections;

/// <summary>
/// 工具箱「客户端」分组：修复客户端窗口 / 快速重启客户端。
/// 重启走 <see cref="SendLcuAsync"/>（原 SendLcu 的 void 异步签名收敛为 Task，
/// 调用方以丢弃赋值触发）：LCU POST 不接受取消令牌、按钮也不需要 pending 态，
/// UseMutation 在此没有增益（R7 逐处批注）。
/// </summary>
internal static class ClientSection
{
    public static Element Render(ToolsUi ui, ConfirmUi confirm, Action<ConfirmUi> setConfirm)
    {
        // ══════════════════════════════════════════════════════
        // 3. 客户端组
        // ══════════════════════════════════════════════════════
        var fixWindowCard = ToolsVisuals.ActionCard(
            ui,
            "修复客户端窗口", "修复客户端错误的窗口大小与黑边缩放（重设 LeagueClientUx 尺寸）",
            Button("立即修复", async () =>
            {
                try
                {
                    var msg = await ui.Tools.FixClientWindowAsync();
                    ui.Toast.Success(msg);
                }
                catch (Exception ex)
                {
                    ui.Toast.Error($"窗口修复失败: {ex.Message}");
                }
            }).AutomationName("修复客户端窗口"));

        var restartClientCard = ToolsVisuals.ActionCard(
            ui,
            "快速重启客户端", "重新引导 LeagueClientUx，无需重新排队或重新输入账号密码",
            // 这一组是 HStack 而不是单个控件，ActionCard 的 IsEnabled 对 StackPanel 无效，
            // 因此三个按钮各自再禁一次（客户端没开时重启/取消都无从谈起）。
            confirm.RestartClient
                ? HStack(8,
                    Caption("⚡ 确定重启客户端？无需重登").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center),
                    Button(Caption("取消"), () => setConfirm(confirm with { RestartClient = false })).Padding(8, 4).IsEnabled(ui.IsLcuConnected).AutomationName("取消重启客户端"),
                    Button(Caption("确认重启"), () =>
                    {
                        setConfirm(confirm with { RestartClient = false });
                        _ = SendLcuAsync(ui, async t =>
                        {
                            await t.SendAsync(LcuRequest.Post("/riotclient/kill-and-restart-ux", "{}"));
                        }, "已向 Riot Client 发送重启指令，客户端正在重新引导...");
                    })
                    .Background(Theme.Ref("SystemFillColorCriticalBrush"))
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                    .Padding(8, 4)
                    .IsEnabled(ui.IsLcuConnected)
                    .AutomationName("确认重启客户端"))
                : Button("重启客户端", () => setConfirm(confirm with { RestartClient = true })).IsEnabled(ui.IsLcuConnected).AutomationName("快速重启客户端"));

        return VStack(8, fixWindowCard, restartClientCard);
    }

    /// <summary>
    /// 经 LCU 通道执行一次指令并把结果交给 Toast。异常在此全捕获，
    /// 调用方以 <c>_ =</c> 触发即可（fire-and-forget，不产生未观察异常）。
    /// </summary>
    internal static async Task SendLcuAsync(ToolsUi ui, Func<ILcuTransport, Task> action, string successMessage)
    {
        try
        {
            var transport = ui.Connection.Transport;
            if (transport is null)
            {
                ui.Toast.Error("LCU 客户端未连接，请先启动英雄联盟客户端");
                return;
            }

            await action(transport);
            ui.Toast.Success(successMessage);
            ui.Logger.LogInformation("LCU 操作成功: {Message}", successMessage);
        }
        catch (Exception ex)
        {
            ui.Toast.Error($"操作失败: {ex.Message}");
            ui.Logger.LogError(ex, "LCU 操作异常");
        }
    }
}
