using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Services;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Persistence;
using YuumiX.Core.Realtime;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>路径条目编辑草稿（防抖期间由页面持有，见 PathCommitDelayMs）。</summary>
internal sealed record PathEditUi(int Index, bool IsWeGame, string? Draft);

/// <summary>云端服务文本草稿（URL / userid，防抖期间由页面持有）。</summary>
internal sealed record CloudTextUi(string? Url, string? UserId);

/// <summary>软件更新区状态：检查/下载结果 + 两个 UseMutation 句柄 + 错误回写（R7）。</summary>
internal sealed record UpdateUi(
    YuumiX.Core.Updates.UpdateInfo? Info,
    string? Error,
    YuumiX.Core.Updates.DownloadProgress? Progress,
    string? DownloadedPath,
    Microsoft.UI.Reactor.Hooks.Mutation<object?, YuumiX.Core.Updates.UpdateInfo?> Check,
    Microsoft.UI.Reactor.Hooks.Mutation<YuumiX.Core.Updates.UpdateInfo, string> Download,
    Action<string?> SetError);

/// <summary>
/// 设置页「通用设置」分组：客户端路径 / 云端服务 / 启动与托盘 / 清除缓存 / 软件更新。
/// </summary>
internal static partial class GeneralSection
{
    public static Element Render(
        SettingsUi ui,
        PathEditUi? pathEdit,
        Action<int, bool, string> requestPathCommit,
        CloudTextUi cloud,
        Action<string?, string?> requestCloudTextCommit,
        SignalrStatus signalrStatus,
        UpdateUi update,
        Action openClearDialog)
    {
        var config = ui.Config;

        var lolPathCard = LolPathCard(ui, pathEdit, requestPathCommit);
        var cloudCard = CloudCard(ui, cloud, requestCloudTextCommit, signalrStatus);
        var clearCacheCard = SettingsCardKit.Card(
            "清除缓存", "删除所有游戏资源（英雄图标/装备/符文）与 OP.GG 数据的磁盘缓存",
            Button("清除缓存", openClearDialog).AutomationName("清除缓存按钮"));

        return VStack(8,
            lolPathCard,
            cloudCard,
            SettingsCardKit.Toggle("随应用启动客户端", "启动 YuumiX 时一并拉起英雄联盟客户端（含 WeGame 形态）", config.General.EnableStartLolWithApp, v =>
            {
                config.General.EnableStartLolWithApp = v;
                ui.Commit(nameof(GeneralConfig.EnableStartLolWithApp));
            }),
            // 两态开关，不是三态复选框：配置里的 null 只是「老配置未表态」的存储细节，
            // 语义上等价于 false（旧版 get_close_to_tray 的 unwrap_or(false)），不能渲染成第三种状态。
            SettingsCardKit.Toggle("关闭到托盘", "点击关闭按钮时将窗口隐藏到系统托盘区域",
                config.General.EnableCloseToTray ?? false, v =>
                {
                    config.General.EnableCloseToTray = v;
                    ui.Commit(nameof(GeneralConfig.EnableCloseToTray));
                }, automationName: "关闭到托盘开关"),
            SettingsCardKit.Toggle("游戏开始时最小化", "检测到对局开始后自动最小化主窗口", config.General.EnableGameStartMinimize, v =>
            {
                config.General.EnableGameStartMinimize = v;
                ui.Commit(nameof(GeneralConfig.EnableGameStartMinimize));
            }),
            clearCacheCard,
            SettingsCardKit.Toggle("检查更新", "启动时检查新版本（便携版走整包覆盖）", config.General.EnableCheckUpdate, v =>
            {
                config.General.EnableCheckUpdate = v;
                ui.Commit(nameof(GeneralConfig.EnableCheckUpdate));
            }),
            UpdateCard(ui, update));
    }
    /// <summary>云端服务（对应旧 CloudServiceSection.vue）：LCU 实时开关进页头，URL / userid / 连接状态留在展开区。</summary>
    private static Element CloudCard(SettingsUi ui, CloudTextUi cloud, Action<string?, string?> requestCloudTextCommit, SignalrStatus signalrStatus)
    {
        var config = ui.Config;
        var signalr = ui.Services.Services.GetRequiredService<SignalrService>();

        string BadgeText(SignalrStatus status) => status.State switch
        {
            SignalrConnectionState.Connected => "已连接",
            SignalrConnectionState.Connecting => "连接中",
            SignalrConnectionState.Error => "连接失败",
            _ => "未连接",
        };

        Element BadgeElement(SignalrStatus status)
        {
            Element Dot(ThemeRef color) =>
                Border(Empty()).Width(8).Height(8).CornerRadius(YuumiTheme.ControlRadius).Background(color);

            var color = status.State switch
            {
                SignalrConnectionState.Connected => Theme.SystemSuccess,
                SignalrConnectionState.Connecting => Theme.SystemCaution,
                SignalrConnectionState.Error => Theme.SystemCritical,
                _ => Theme.SystemNeutral,
            };

            return HStack(8,
                Dot(color),
                Caption(BadgeText(status)).Foreground(color));
        }

        var items = new List<Element>();
        if (config.Functions.LcuRealtimeEnabled)
        {
            items.Add(SettingsCardKit.Card(
                "SignalR 连接状态",
                "LCU 实时远程查询通道状态",
                BadgeElement(signalrStatus)));
        }

        items.Add(SettingsCardKit.Card("服务器 API 地址", "对局数据上传的服务端地址",
            TextBox(
                cloud.Url ?? config.General.UploadApiUrl,
                v => requestCloudTextCommit(v, null),
                "http://example.com")
                .Width(240)
                .AutomationName("服务器 API 地址")));

        items.Add(SettingsCardKit.Card("userid", "上报数据使用的用户标识",
            TextBox(
                cloud.UserId ?? config.General.SignalrUserId,
                v => requestCloudTextCommit(null, v),
                "留空默认使用 lcu_user_001")
                .Width(240)
                .AutomationName("SignalR userid")));

        if (signalrStatus.State == SignalrConnectionState.Error && !string.IsNullOrEmpty(signalrStatus.Error))
        {
            items.Add(SettingsCardKit.ContentCard(
                Caption($"连接异常： {signalrStatus.Error}")
                    .Foreground(Theme.SystemCritical)));
        }

        return SettingsCardKit.Expander(
            "云端服务",
            "对局数据上传与 LCU 实时远程查询（SignalR）",
            config.Functions.LcuRealtimeEnabled
                ? signalrStatus.State == SignalrConnectionState.Connected
                    ? SettingsStatus.Connected
                    : SettingsStatus.Disconnected
                : string.IsNullOrWhiteSpace(config.General.UploadApiUrl)
                    ? SettingsStatus.Unconfigured
                    : SettingsStatus.Enabled,
            headerControl: ToggleSwitch(config.Functions.LcuRealtimeEnabled, v =>
            {
                if (v == config.Functions.LcuRealtimeEnabled) return;
                config.Functions.LcuRealtimeEnabled = v;
                ui.Commit(nameof(FunctionsConfig.LcuRealtimeEnabled));
                // 即时应用：开启 → 开始连接（徽标经状态流转 connecting），关闭 → 停止
                _ = signalr.ApplyConfigAsync();
            }, header: null, onContent: null, offContent: null)
                .AutomationName("LCU 实时查询开关"),
            innerItems: [.. items]);
    }

    /// <summary>软件更新（对应旧 UpdateSection.vue + UpdateDialog.vue；检查/下载执行态由 UseMutation 提供，R7）。</summary>
    private static Element UpdateCard(SettingsUi ui, UpdateUi update)
    {
        var updateCardStatus = update.Info is { } found ? $"发现新版 v{found.Version}" : "已是最新";
        var items = new List<Element>
        {
            SettingsCardKit.Card("检查更新", "手动检查并下载新版本",
                Button(update.Check.IsPending ? "检查中..." : "检查更新", () => _ = update.Check.RunAsync(null))
                    .IsEnabled(!update.Check.IsPending)
                    .AutomationName("检查更新按钮")),
        };

        if (update.Error is not null)
        {
            items.Add(SettingsCardKit.ContentCard(
                Body($"检查/下载失败： {update.Error}")
                    .Foreground(Theme.SystemCritical)));
        }

        if (update.Info is not null)
        {
            items.Add(SettingsCardKit.ContentCard(VStack(4,
                SettingsVisuals.InfoRow("新版本", $"v{update.Info.Version}"),
                SettingsVisuals.InfoRow("当前版本", $"v{update.Info.CurrentVersion}"),
                string.IsNullOrEmpty(update.Info.Notes)
                    ? Empty()
                    : Caption(update.Info.Notes).Foreground(Theme.SecondaryText).TextWrapping(),
                // 下载与安装区：下载中显示进度，就绪后切换为安装按钮。
                // DownloadUrl 为空表示该版本没有当前形态对应的资产（例如只发了 zip 却用安装版运行），
                // 此时不能渲染「下载更新」——点下去必然抛「该版本没有可下载的资产」，只能引导去发布页。
                update.DownloadedPath is not null
                    ? Button(ui.Services.Paths.IsPortable ? "应用更新并重启" : "安装更新并重启", () => ApplyPendingUpdate(ui, update, update.DownloadedPath))
                        .AutomationName("安装更新按钮")
                    : update.Progress is { } p
                        ? VStack(4,
                            Progress(p.Percent ?? 0),
                            Caption($"已下载 {p.Downloaded / 1024.0 / 1024.0:F1} MB" + (p.Total is { } t ? $" / {t / 1024.0 / 1024.0:F1} MB" : string.Empty)))
                        : update.Info.DownloadUrl is null
                            ? Caption("该版本未提供适用于当前安装形态的更新包，请在下方发布页手动下载。")
                                .Foreground(Theme.SecondaryText).TextWrapping()
                            : Button("下载更新", () => _ = update.Download.RunAsync(update.Info)).AutomationName("下载更新按钮"))));
        }

        items.Add(SettingsCardKit.Card("开源发布页", "在浏览器中打开 GitHub Releases",
            Button("前往 GitHub Releases", () =>
            {
                try
                {
                    _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        // 指向发布列表而非 /releases/latest：仓库只有预发布时 latest 会返回 404
                        FileName = $"https://github.com/{YuumiX.Core.Updates.GitHubClient.Repository}/releases",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    ui.Logger.LogWarning(ex, "打开 GitHub Releases 失败");
                }
            }).AutomationName("前往 GitHub Releases")));

        return SettingsCardKit.Expander(
            "软件更新",
            ui.Services.Paths.IsPortable
                ? "便携版通过 zip 覆盖方式更新；检查到新版后手动下载应用"
                : "安装版通过 setup 静默重装；检查到新版后手动下载应用",
            SettingsStatus.Custom,
            headerControl: null,
            innerItems: [.. items],
            statusText: updateCardStatus);
    }

    private static void ApplyPendingUpdate(SettingsUi ui, UpdateUi update, string path)
    {
        try
        {
            bool willRestart = YuumiX.Core.Updates.UpdateService.ApplyPendingUpdate(path, ui.Services.Paths.IsPortable);
            if (!willRestart)
            {
                return;
            }

            // 两种形态都必须等主进程彻底退出后由外部脚本接手（运行中的 exe 与已映射的 dll
            // 都被锁住；安装版若交给 Restart Manager 强关，未落盘的配置会丢）。
            // 这里主动结束消息循环，让 App.cs 的 finally 正常刷新日志、释放容器，
            // 随后脚本才开始 robocopy 覆盖或启动安装程序，并拉起新版本。
            ui.Toast.Info(ui.Services.Paths.IsPortable
                ? "正在应用更新，应用即将重启…"
                : "正在启动安装程序，应用即将退出…");
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            update.SetError(ex.Message);
            ui.Toast.Error($"安装更新失败: {ex.Message}");
        }
    }
}
