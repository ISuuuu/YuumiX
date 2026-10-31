using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings.Sections;

/// <summary>
/// 设置页「日志与网络」分组：日志等级 / 日志文件 / 软件 HTTP 代理。
/// 代理地址草稿由页面以 UseRef 持有（刻意不参与渲染，避免受控文本框光标跳动）。
/// </summary>
internal static class LogNetworkSection
{
    private static readonly string[] LogLevelLabels = ["调试（Debug）", "信息（Info）", "错误（Error）"];
    private static readonly int[] LogLevelValues = [0, 1, 2];

    public static Element Render(SettingsUi ui, Microsoft.UI.Reactor.Core.Ref<string?> proxyDraft)
    {
        var config = ui.Config;
        var i18n = ui.Services.I18n;

        var logLevelLabels = new[]
        {
            i18n.T("settings.logLevelDebug", "调试（Debug）"),
            i18n.T("settings.logLevelInfo", "信息（Info）"),
            i18n.T("settings.logLevelError", "错误（Error）"),
        };

        var logLevelControl = PixelComboBox.Render(logLevelLabels, SettingsVisuals.IndexOf(LogLevelValues, config.General.LogLevel, LogLevelValues.Length - 1), index =>
        {
            if (index < 0 || index >= LogLevelValues.Length)
            {
                return;
            }

            var level = LogLevelValues[index];
            config.General.LogLevel = level;
            ui.Services.ApplyLogLevel(level);
            ui.Commit(nameof(GeneralConfig.LogLevel));
        }, automationName: "日志等级选择", width: 160);
        // 【临时调试，测完删除】a5fcac6 引入自绘 PixelComboBox 之前的原生 ComboBox 版本逐字还原
        // （回调真实生效：与自绘下拉同写 config，选中后两者保持同步）。默认隐藏，
        // 长按卡片 2 秒显示在自绘下拉左侧（见文件尾部 LogLevelRevealHost）。
        var legacyLogLevelControl = ComboBox(logLevelLabels, SettingsVisuals.IndexOf(LogLevelValues, config.General.LogLevel, LogLevelValues.Length - 1), index =>
        {
            if (index < 0 || index >= LogLevelValues.Length)
            {
                return;
            }

            var level = LogLevelValues[index];
            config.General.LogLevel = level;
            ui.Services.ApplyLogLevel(level);
            ui.Commit(nameof(GeneralConfig.LogLevel));
        }).Width(160).AutomationName("日志等级选择");

        var logLevelCard = Component<LogLevelRevealHost, LogLevelRevealHostProps>(new LogLevelRevealHostProps(
            i18n.T("settings.logLevelTitle", "日志等级"),
            i18n.T("settings.logLevelDesc", "写入 <exe>/log 的日志记录详细程度，修改后立即生效"),
            logLevelControl,
            legacyLogLevelControl));

        var logDirPrompt = i18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs
            ? $"Stored at {ui.Services.Paths.LogDirectory}"
            : (i18n.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.ZhTw
                ? $"存放於 {ui.Services.Paths.LogDirectory}"
                : $"存放于 {ui.Services.Paths.LogDirectory}");

        var logDirCard = SettingsCardKit.Card(
            i18n.T("settings.logFileTitle", "日志文件"),
            logDirPrompt,
            Button(i18n.T("settings.openFolderBtn", "打开目录"), () =>
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", ui.Services.Paths.LogDirectory);
                }
                catch (Exception ex)
                {
                    ui.Logger.LogWarning(ex, "打开日志目录失败");
                }
            }).AutomationName("打开日志目录"));

        var httpProxyCard = SettingsCardKit.Expander(
            i18n.T("settings.httpProxyGroup", "软件 HTTP 代理"),
            i18n.T("settings.httpProxyDesc", "软件更新、TFT 数据、OP.GG 服务访问走此 HTTP 代理通道"),
            config.General.EnableHttpProxy ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            headerControl: SettingsCardKit.Switch(config.General.EnableHttpProxy, v =>
            {
                config.General.EnableHttpProxy = v;
                ui.Commit(nameof(GeneralConfig.EnableHttpProxy));
            }, "HTTP代理开关"),
            innerContent: SettingsCardKit.Card(
                i18n.T("settings.proxyAddress", "代理地址"),
                i18n.T("settings.proxyAddressDesc", "形如 127.0.0.1:7897；开关关闭时不可编辑"),
                ProxyAddressRow(ui, proxyDraft)));

        return VStack(8, logLevelCard, logDirCard, httpProxyCard);
    }

    private static Element ProxyAddressRow(SettingsUi ui, Microsoft.UI.Reactor.Core.Ref<string?> proxyDraft)
    {
        static bool LooksValid(string value) =>
            value.Length == 0
            || (value.Contains(':') && !value.Contains(' ') && Uri.TryCreate("http://" + value, UriKind.Absolute, out _));

        var config = ui.Config;
        var i18n = ui.Services.I18n;
        return HStack(8,
            // 开关关闭时禁用输入（对齐旧 ProxySection 的 disabled 联动）
            TextBox(proxyDraft.Current ?? config.General.HttpProxyAddr, v => proxyDraft.Current = v)
                .Width(240)
                .IsEnabled(config.General.EnableHttpProxy)
                .AutomationName("代理地址"),
            Button(i18n.T("settings.applyBtn", "应用"), () =>
            {
                var next = (proxyDraft.Current ?? config.General.HttpProxyAddr).Trim();
                if (!LooksValid(next))
                {
                    ui.SetError(i18n.T("settings.proxyAddressInvalid", "代理地址格式不对，应形如 127.0.0.1:7897"));
                    return;
                }

                if (next == config.General.HttpProxyAddr)
                {
                    return;
                }

                config.General.HttpProxyAddr = next;
                ui.Commit(nameof(GeneralConfig.HttpProxyAddr));
            })
            .IsEnabled(config.General.EnableHttpProxy)
            .AutomationName("应用代理"));
    }
}

/// <summary>
/// 【临时调试，测完删除】显形状态的进程级存根：界面缩放跨过 100% 档位时，
/// AppShell 会在 Viewbox 与裸内容容器之间切换元素结构，整棵内容子树
/// （含 NavigationHost 页面缓存与全部组件 UseState）被重建，显形状态随之丢失；
/// 把最新值镜像到静态字段，重建后按它复位，改缩放不再需要重新长按。
/// </summary>
internal static class LogLevelRevealState
{
    internal static bool Revealed;
}

/// <summary>
/// 【临时调试，测完删除】日志等级卡片宿主：默认渲染形态与线上完全一致；
/// 长按卡片 2 秒后，在自绘下拉左侧显示旧版原生 ComboBox（弹层文字清晰度对比）。
/// 按下期间任意释放 / 离开 / 系统打断 / 指针捕获转移都会取消计时；
/// 显示状态存于本组件 UseState，写回时镜像到 <see cref="LogLevelRevealState"/>（缩放切档整树重建后恢复）。
/// </summary>
public sealed record LogLevelRevealHostProps(string Title, string Description, Element PixelCombo, Element LegacyCombo);

public sealed class LogLevelRevealHost : Component<LogLevelRevealHostProps>
{
    private static readonly TimeSpan LongPressDuration = TimeSpan.FromSeconds(2);

    public override Element Render()
    {
        var (revealed, setRevealedState) = UseState(LogLevelRevealState.Revealed);
        void SetRevealed(bool value)
        {
            LogLevelRevealState.Revealed = value;
            setRevealedState(value);
        }

        var pressTimerRef = UseRef<Microsoft.UI.Dispatching.DispatcherQueueTimer?>(null);

        void StopPressTimer()
        {
            pressTimerRef.Current?.Stop();
            pressTimerRef.Current = null;
        }

        void StartPressTimer()
        {
            StopPressTimer();
            // 指针事件固定在 UI 线程触发，GetForCurrentThread 在此必得（同 SettingsPage.StartDebounce 的前提）
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = LongPressDuration;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => SetRevealed(true);
            timer.Start();
            pressTimerRef.Current = timer;
        }

        // 事件委托必须跨渲染同一实例（§5.7，-= 才摘得掉），处理体经 ref 中转、每次渲染刷新闭包；
        // 订阅不能走 Reactor 的 OnPointerPressed 修饰符：SettingsCard 是 ButtonBase 派生，
        // 类处理器会把 PointerPressed 标记 Handled，普通订阅收不到（同 NavIconPressFeedback 的结论），
        // 必须 AddHandler(handledEventsToo: true)。
        var startRef = UseRef<Action?>(null);
        var stopRef = UseRef<Action?>(null);
        startRef.Current = StartPressTimer;
        stopRef.Current = StopPressTimer;
        var pressedRef = UseRef<Microsoft.UI.Xaml.Input.PointerEventHandler?>(null);
        var releasedRef = UseRef<Microsoft.UI.Xaml.Input.PointerEventHandler?>(null);
        pressedRef.Current ??= (_, _) => startRef.Current?.Invoke();
        releasedRef.Current ??= (_, _) => stopRef.Current?.Invoke();

        var row = revealed
            ? HStack(8, Props.LegacyCombo, Props.PixelCombo)
            : Props.PixelCombo;

        return SettingsCardKit.Card(Props.Title, Props.Description, row)
            .OnMountAdd(element =>
            {
                element.AddHandler(UIElement.PointerPressedEvent, pressedRef.Current!, handledEventsToo: true);
                element.AddHandler(UIElement.PointerReleasedEvent, releasedRef.Current!, handledEventsToo: true);
                element.AddHandler(UIElement.PointerCaptureLostEvent, releasedRef.Current!, handledEventsToo: true);
                element.AddHandler(UIElement.PointerCanceledEvent, releasedRef.Current!, handledEventsToo: true);
            })
            .OnUnmountAdd(element =>
            {
                element.RemoveHandler(UIElement.PointerPressedEvent, pressedRef.Current!);
                element.RemoveHandler(UIElement.PointerReleasedEvent, releasedRef.Current!);
                element.RemoveHandler(UIElement.PointerCaptureLostEvent, releasedRef.Current!);
                element.RemoveHandler(UIElement.PointerCanceledEvent, releasedRef.Current!);
                StopPressTimer();
            });
    }
}
