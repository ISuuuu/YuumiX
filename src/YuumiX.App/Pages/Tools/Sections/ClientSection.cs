using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.Tools.Sections;

/// <summary>工具箱客户端分组。</summary>
internal static class ClientSection
{
    public static Element Render(ToolsUi ui, ConfirmUi confirm, Action<ConfirmUi> setConfirm, bool restartPending)
    {
        var i18n = ui.I18n;

        // ══════════════════════════════════════════════════════
        // 3. 客户端组
        // ══════════════════════════════════════════════════════
        var fixWindowCard = ToolsVisuals.ActionCard(
            ui,
            i18n.T("tools.fixWindow.title", "修复客户端窗口"),
            i18n.T("tools.fixWindow.desc", "修复客户端错误的窗口大小与黑边缩放（重设 LeagueClientUx 尺寸）"),
            Button(i18n.T("common.confirm", "立即修复"), async () =>
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
            i18n.T("tools.restartClient.title", "快速重启客户端"),
            i18n.T("tools.restartClient.desc", "重新引导 LeagueClientUx，无需重新排队或重新输入账号密码"),
            Button(i18n.T("tools.restartClient.title", "重启客户端"), () => setConfirm(confirm with { RestartClient = true }))
                .IsEnabled(ui.IsLcuConnected && !restartPending)
                .AutomationName(i18n.T("tools.restartAutomation", "快速重启客户端")));

        return VStack(8, fixWindowCard, restartClientCard);
    }

}
