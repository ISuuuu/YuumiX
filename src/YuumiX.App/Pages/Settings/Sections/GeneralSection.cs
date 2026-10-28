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
/// <param name="Checked">
/// 是否已经有过一次检查结果。用来区分「尚未检查」与「查过、已是最新」——
/// <see cref="Info"/> 为 <c>null</c> 时这两种情况长得一样，只看它会把未检查误报成最新。
/// </param>
internal sealed record UpdateUi(
    YuumiX.Core.Updates.UpdateInfo? Info,
    bool Checked,
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
        var i18n = ui.Services.I18n;

        var lolPathCard = LolPathCard(ui, pathEdit, requestPathCommit);
        var cloudCard = CloudCard(ui, cloud, requestCloudTextCommit, signalrStatus);
        var clearCacheCard = SettingsCardKit.Card(
            i18n.T("settings.clearCacheTitle", "清除缓存"),
            i18n.T("settings.clearCacheDesc", "删除所有游戏资源（英雄图标/装备/符文）与 OP.GG 数据的磁盘缓存"),
            Button(i18n.T("settings.deleteBtn", "清除缓存"), openClearDialog).AutomationName("清除缓存按钮"));

        return VStack(8,
            lolPathCard,
            cloudCard,
            SettingsCardKit.Toggle(
                i18n.T("settings.autoStartLolTitle", "随应用启动客户端"),
                i18n.T("settings.autoStartLolDesc", "启动 YuumiX 时一并拉起英雄联盟客户端（含 WeGame 形态）"),
                config.General.EnableStartLolWithApp,
                v =>
                {
                    config.General.EnableStartLolWithApp = v;
                    ui.Commit(nameof(GeneralConfig.EnableStartLolWithApp));
                }),
            // 两态开关，不是三态复选框：配置里的 null 只是「老配置未表态」的存储细节，
            // 语义上等价于 false（旧版 get_close_to_tray 的 unwrap_or(false)），不能渲染成第三种状态。
            SettingsCardKit.Toggle(
                i18n.T("settings.closeToTrayTitle", "关闭到托盘"),
                i18n.T("settings.closeToTrayDesc", "点击关闭按钮时将窗口隐藏到系统托盘区域"),
                config.General.EnableCloseToTray ?? false,
                v =>
                {
                    config.General.EnableCloseToTray = v;
                    ui.Commit(nameof(GeneralConfig.EnableCloseToTray));
                },
                automationName: "关闭到托盘开关"),
            SettingsCardKit.Toggle(
                i18n.T("settings.startMinimizedTitle", "游戏开始时最小化"),
                i18n.T("settings.startMinimizedDesc", "检测到对局开始后自动最小化主窗口"),
                config.General.EnableGameStartMinimize,
                v =>
                {
                    config.General.EnableGameStartMinimize = v;
                    ui.Commit(nameof(GeneralConfig.EnableGameStartMinimize));
                }),
            clearCacheCard,
            SettingsCardKit.Toggle(
                i18n.T("settings.checkUpdateTitle", "检查更新"),
                i18n.T("settings.checkUpdateDesc", "启动时检查新版本，发现后侧边栏「设置」会亮起红点提示"),
                config.General.EnableCheckUpdate,
                v =>
                {
                    config.General.EnableCheckUpdate = v;
                    ui.Commit(nameof(GeneralConfig.EnableCheckUpdate));

                    if (!v)
                    {
                        // 关掉之后不再有任何检测点：红点必须同步熄灭，
                        // 否则它指向的是一个永远不会刷新的旧结果。
                        ui.UpdateNotice.Clear();
                        return;
                    }

                    // 打开时立即补查一次：启动检查在「关闭」状态下已被跳过，
                // 不补查就要等到下一个复查周期（最长 6 小时）才可能有提示。
                _ = CheckOnceAsync(ui);
            }),
            UpdateCard(ui, update));
    }
    /// <summary>云端服务（对应旧 CloudServiceSection.vue）：LCU 实时开关进页头，URL / userid / 连接状态留在展开区。</summary>
    private static Element CloudCard(SettingsUi ui, CloudTextUi cloud, Action<string?, string?> requestCloudTextCommit, SignalrStatus signalrStatus)
    {
        var config = ui.Config;
        var signalr = ui.Services.Services.GetRequiredService<SignalrService>();
        var i18n = ui.Services.I18n;

        string BadgeText(SignalrStatus status) => status.State switch
        {
            SignalrConnectionState.Connected => i18n.T("settings.cloudConnected", "已连接"),
            SignalrConnectionState.Connecting => i18n.T("settings.cloudConnecting", "连接中"),
            SignalrConnectionState.Error => i18n.T("settings.cloudConnectFailed", "连接失败"),
            _ => i18n.T("settings.cloudDisconnected", "未连接"),
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
                i18n.T("settings.signalrStatus", "SignalR 连接状态"),
                i18n.T("settings.signalrStatusDesc", "LCU 实时远程查询通道状态"),
                BadgeElement(signalrStatus)));
        }

        items.Add(SettingsCardKit.Card(
            i18n.T("settings.uploadUrlTitle", "服务器 API 地址"),
            i18n.T("settings.uploadUrlDesc", "对局数据上传的服务端地址"),
            TextBox(
                cloud.Url ?? config.General.UploadApiUrl,
                v => requestCloudTextCommit(v, null),
                "http://example.com")
                .Width(240)
                .AutomationName("服务器 API 地址")));

        items.Add(SettingsCardKit.Card(
            i18n.T("settings.licenseKeyTitle", "userid"),
            i18n.T("settings.licenseKeyDesc", "上报数据使用的用户标识"),
            TextBox(
                cloud.UserId ?? config.General.SignalrUserId,
                v => requestCloudTextCommit(null, v),
                i18n.T("settings.useridPlaceholder", "留空默认使用 lcu_user_001"))
                .Width(240)
                .AutomationName("SignalR userid")));

        if (signalrStatus.State == SignalrConnectionState.Error && !string.IsNullOrEmpty(signalrStatus.Error))
        {
            items.Add(SettingsCardKit.ContentCard(
                Caption($"{i18n.T("settings.connectionError", "连接异常: ")}{signalrStatus.Error}")
                    .Foreground(Theme.SystemCritical)));
        }

        return SettingsCardKit.Expander(
            i18n.T("settings.cloudServiceTitle", "云端服务"),
            i18n.T("settings.cloudServiceDesc", "对局数据上传与 LCU 实时远程查询（SignalR）"),
            config.Functions.LcuRealtimeEnabled
                ? signalrStatus.State == SignalrConnectionState.Connected
                    ? SettingsStatus.Connected
                    : SettingsStatus.Disconnected
                : string.IsNullOrWhiteSpace(config.General.UploadApiUrl)
                    ? SettingsStatus.Unconfigured
                    : SettingsStatus.Enabled,
            headerControl: SettingsCardKit.Switch(config.Functions.LcuRealtimeEnabled, v =>
            {
                config.Functions.LcuRealtimeEnabled = v;
                ui.Commit(nameof(FunctionsConfig.LcuRealtimeEnabled));
                // 即时应用：开启 → 开始连接（徽标经状态流转 connecting），关闭 → 停止
                _ = signalr.ApplyConfigAsync();
            }, "LCU 实时查询开关"),
            innerItems: [.. items]);
    }

    /// <summary>软件更新（对应旧 UpdateSection.vue + UpdateDialog.vue；检查/下载执行态由 UseMutation 提供，R7）。</summary>
    private static Element UpdateCard(SettingsUi ui, UpdateUi update)
    {
        var info = update.Info;
        var i18n = ui.Services.I18n;

        // 「尚未检查」必须与「已是最新」区分开：两者 Info 都是 null，
        // 单看 Info 会让刚装好的用户以为已经确认过没有新版本。
        var updateCardStatus = update.Check.IsPending
            ? i18n.T("settings.checkingUpdate", "检查中")
            : info is { } found
                ? i18n.T("settings.newVersionFound", "发现新版") + $" v{found.Version}"
                : update.Checked ? i18n.T("settings.alreadyLatest", "已是最新") : i18n.T("settings.notChecked", "尚未检查");

        // 主操作卡：已知有更新时，第一落点必须是「下载 / 安装」，而不是让用户再点一次「检查更新」——
        // 版本已经在检查阶段确认过，再检查一次没有任何信息增量。
        var primaryCard = info is null
            ? SettingsCardKit.Card(
                i18n.T("settings.checkUpdateTitle", "检查更新"),
                i18n.T("settings.checkUpdateDesc", "手动检查并下载新版本"),
                Button(update.Check.IsPending ? i18n.T("settings.checkingUpdate", "检查中...") : i18n.T("settings.checkUpdateBtn", "检查更新"), () => _ = update.Check.RunAsync(null))
                    .IsEnabled(!update.Check.IsPending)
                    .AutomationName("检查更新按钮"))
            : SettingsCardKit.Card(
                i18n.T("settings.newVersionFound", "发现新版本") + $" v{info.Version}",
                i18n.T("settings.updateAvailableDesc", "当前 v{current}，可更新到 v{version}")
                    .Replace("{current}", info.CurrentVersion)
                    .Replace("{version}", info.Version),
                PrimaryAction(ui, update, info));

        var items = new List<Element> { primaryCard };

        if (update.Error is not null)
        {
            items.Add(SettingsCardKit.ContentCard(
                Body($"{i18n.T("settings.updateCheckFailed", "检查/下载失败: ")}{update.Error}")
                    .Foreground(Theme.SystemCritical)));
        }

        if (info is not null)
        {
            items.Add(SettingsCardKit.ContentCard(VStack(8,
                SettingsVisuals.InfoRow(i18n.T("settings.newVersionLabel", "新版本"), $"v{info.Version}"),
                SettingsVisuals.InfoRow(i18n.T("settings.currentVersionLabel", "当前版本"), $"v{info.CurrentVersion}"),
                // 发布说明可能很长（某些版本带整段 changelog）：限高后可滚动，
                // 否则一两百行的说明会把整页推下去。
                string.IsNullOrEmpty(info.Notes)
                    ? Empty()
                    : ScrollViewer(
                        Caption(info.Notes)
                            .Foreground(Theme.SecondaryText)
                            .TextWrapping())
                        .MaxHeight(160)
                        .IsTabStop(true),
                // 只在「想再确认一次」时才需要重新检查：主操作已经上移到标题卡，
                // 这里放低频动作，避免两个同等重量的按钮抢注意力。
                Button(update.Check.IsPending ? i18n.T("settings.checkingUpdate", "检查中...") : i18n.T("settings.recheckBtn", "重新检查"), () => _ = update.Check.RunAsync(null))
                    .IsEnabled(!update.Check.IsPending)
                    .AutomationName("重新检查更新按钮"))));
        }

        items.Add(SettingsCardKit.Card(
            i18n.T("settings.releasesPage", "开源发布页"),
            i18n.T("settings.releasesDesc", "在浏览器中打开 GitHub Releases"),
            Button(i18n.T("settings.goToReleases", "前往 GitHub Releases"), () =>
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

        var updateDesc = ui.Services.Paths.IsPortable
            ? i18n.T("settings.portableUpdateDesc", "便携版通过 zip 覆盖方式更新；检查到新版后手动下载应用")
            : i18n.T("settings.setupUpdateDesc", "安装版通过 setup 静默重装；检查到新版后手动下载应用");

        return SettingsCardKit.Expander(
            i18n.T("settings.softwareUpdateGroup", "软件更新"),
            updateDesc,
            SettingsStatus.Custom,
            headerControl: null,
            innerItems: [.. items],
            statusText: updateCardStatus);
    }

    /// <summary>
    /// 打开「检查更新」开关后立即补查一次，并把结果发布到全局提示状态。
    ///
    /// 与页面上「检查更新」按钮的区别：这条路径是开关的副作用，用户没有主动查看结果的预期，
    /// 因此失败只记日志、不弹 Toast——免得只是打开一个开关就被网络错误弹窗打断。
    ///
    /// 同样因为不是「用户要一个确定结论」，这里 <c>forceRefresh</c> 保持 false：
    /// 反复拨动开关不该把 GitHub 未认证 API 的 60 次/小时配额消耗掉；
    /// 真正需要「现在就查」的入口是设置页那个按钮（见 <c>SettingsPage.checkUpdate</c>）。
    /// </summary>
    private static async Task CheckOnceAsync(SettingsUi ui)
    {
        try
        {
            var service = ui.Services.Services.GetRequiredService<YuumiX.Core.Updates.UpdateService>();
            var info = await service.CheckForUpdateAsync(ui.Services.Paths.IsPortable, forceRefresh: false).ConfigureAwait(false);
            ui.UpdateNotice.Publish(info);
        }
        catch (Exception ex)
        {
            ui.Logger.LogWarning(ex, "打开「检查更新」后的补查失败（离线或网络受限属常态）");
        }
    }

    /// <summary>
    /// 更新主操作：已下载 → 安装并重启；下载中 → 进度；否则 → 下载。
    ///
    /// 三种状态共用一个按钮位的原因：它们互斥且都表达「把这次更新做完」，
    /// 分散成多张卡会让用户在「下载/安装」之间来回找。
    /// <c>DownloadUrl</c> 为空表示该版本没有当前安装形态的资产（例如只发了 zip 却用安装版运行），
    /// 此时不渲染下载按钮——点下去必然抛「该版本没有可下载的资产」，只能引导去发布页。
    /// </summary>
    private static Element PrimaryAction(SettingsUi ui, UpdateUi update, YuumiX.Core.Updates.UpdateInfo info)
    {
        var i18n = ui.Services.I18n;
        if (update.DownloadedPath is { } path)
        {
            return Button(
                    ui.Services.Paths.IsPortable
                        ? i18n.T("settings.applyAndRestart", "应用更新并重启")
                        : i18n.T("settings.installAndRestart", "安装更新并重启"),
                    () => ApplyPendingUpdate(ui, update, path))
                .AutomationName("安装更新按钮");
        }

        if (update.Progress is { } progress)
        {
            var downloadedStr = $"{progress.Downloaded / 1024.0 / 1024.0:F1} MB";
            var totalStr = progress.Total is { } total ? $" / {total / 1024.0 / 1024.0:F1} MB" : string.Empty;
            return VStack(4,
                Progress(progress.Percent ?? 0),
                Caption(i18n.T("settings.downloadedProgress", "已下载 {progress}").Replace("{progress}", downloadedStr + totalStr)));
        }

        if (info.DownloadUrl is not null)
        {
            // 下载中禁用：进度条出现前有几百毫秒窗口，连点会并发写同一个 .tmp（FileShare.None 直接报错）。
            return Button(i18n.T("settings.downloadUpdate", "下载更新"), () => _ = update.Download.RunAsync(info))
                .IsEnabled(!update.Download.IsPending)
                .AutomationName("下载更新按钮");
        }

        return Caption(i18n.T("settings.noAssetForPackage", "该版本未提供适用于当前安装形态的更新包，请在下方发布页手动下载。"))
            .Foreground(Theme.SecondaryText)
            .TextWrapping();
    }

    private static void ApplyPendingUpdate(SettingsUi ui, UpdateUi update, string path)
    {
        try
        {
            bool willRestart = YuumiX.Core.Updates.UpdateService.ApplyPendingUpdate(
                path, ui.Services.Paths.IsPortable, ui.Services.Paths.DataDirectory);
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
