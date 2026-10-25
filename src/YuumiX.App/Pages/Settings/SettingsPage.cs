using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media.Animation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Services;
using YuumiX.App.Theming;
using YuumiX.Core.Automation;
using YuumiX.App.Windowing;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Persistence;
using YuumiX.Core.Realtime;

namespace YuumiX.App.Pages.Settings;

/// <summary>
/// 设置页（阶段 5 后半：编辑与写回）。
///
/// 写回路径（ADR-12）：控件回调 → 改内存 <see cref="AppConfig"/> → 应用即时副作用
/// （主题 / 日志等级）→ <see cref="ConfigStore.SaveWithBackup"/>（会话首写自动备份）→ 状态条反馈。
///
/// 三个刻意的选择：
/// <list type="number">
///   <item><b>同步写回</b>：配置全文约 3 KB，原子替换亚毫秒级；异步除了要处理跨线程改状态（ADR-07），
///         还会在「改完立刻切页」时丢掉最后一次修改。原 Vue 实现为此写了专门的卸载兜底。</item>
///   <item><b>回调第一行比对旧值</b>：受控控件（ToggleSwitch / ComboBox / NumberBox）
///         在程序化赋值时同样会触发变更通知，不比对就会形成「渲染 → 改配置 → 写盘 → 重渲染」自激。</item>
///   <item><b>只读项不隐藏</b>：路径、颜色、语言等尚未接编辑的字段照常展示并注明原因，
///         避免用户以为字段消失；颜色类控件的选择器排在第二轮（ADR-12 决策四）。</item>
/// </list>
/// </summary>
public sealed class SettingsPage : Component
{
    private static readonly string[] ThemeModeLabels = ["浅色", "深色", "跟随系统"];
    private static readonly string[] ThemeModeValues = ["Light", "Dark", "Auto"];

    private static readonly string[] LogLevelLabels = ["调试（Debug）", "信息（Info）", "错误（Error）"];
    private static readonly int[] LogLevelValues = [0, 1, 2];

    private const int CareerGamesMin = 1;
    private const int CareerGamesMax = 100;
    private const int ApiConcurrencyMin = 1;
    private const int ApiConcurrencyMax = 10;

    /// <summary>开源仓库地址（工程 git remote 与 README 一致）。</summary>
    private const string RepositoryUrl = "https://github.com/ISuuuu/YuumiX";

    /// <summary>应用版本（入口程序集的文件版本，显示为 v主.次.修订）。</summary>
    private static string AppVersion =>
        "v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>
    /// 取色器的合并写盘延迟。ColorPicker 的 ColorChanged 在拖动时每帧触发，
    /// 逐次同步写盘既打断拖动又没有信息增量；停手 400 ms 后写一次就够了。
    /// </summary>
    private const int ColorCommitDelayMs = 400;

    /// <summary>
    /// 路径条目的合并写盘延迟。TextBox 的文本回调逐字符触发，逐字符落盘会把半截路径写进配置；
    /// 停手 600 ms 后写一次（取色器同款机制，见 ColorCommitDelayMs）。
    /// </summary>
    private const int PathCommitDelayMs = 600;

    /// <summary>
    /// 渲染序号。用于在「改了 POCO 但 Reactor 不知道」时强制重渲染。
    /// 用实例字段而非读取 <c>UseState</c> 的当前值再加一：事件回调里捕获的旧值可能是过期的。
    /// </summary>
    private static long _nextToastId;
    private int _renderTick;

    /// <summary>颜色合并写盘的序号，见 <see cref="ColorCommitDelayMs"/>。</summary>
    private int _colorTick;

    /// <summary>待写回的颜色字段名（连续取色只写最后一次）。</summary>
    private string? _pendingColorField;

    /// <summary>代理地址草稿：只在点「应用」时读它，不参与渲染，避免受控文本框的光标跳动。</summary>
    private string? _proxyDraft;

    /// <summary>路径条目编辑草稿（见 <see cref="PathCommitDelayMs"/>），只由防抖定时器消费。</summary>
    private string? _pathEditDraft;

    /// <summary>正在编辑的路径条目索引；-1 表示没有待提交的编辑。</summary>
    private int _pathEditIndex = -1;

    /// <summary>正在编辑的是否为 WeGame 条目（true 时草稿写 <c>WegamePath</c> 而非路径列表）。</summary>
    private bool _pathEditIsWeGame;

    /// <summary>路径编辑防抖的递增序号（与 <see cref="_colorTick"/> 同款机制）。</summary>
    private int _pathEditTick;

    /// <summary>云端服务文本草稿：URL 与 userid（防抖机制与路径条目相同）。</summary>
    private string? _cloudUrlDraft;
    private string? _cloudUserIdDraft;

    /// <summary>云端文本编辑防抖的递增序号。</summary>
    private int _cloudTextTick;

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly List<Timer> _toastTimers = [];
    private readonly List<ToastItem> _activeToasts = [];

    private ILogger<SettingsPage>? _logger;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var menu = UseContext(AppHost.MenuContext);
        var config = services.Config;

        // threadSafe: true —— 保存与重渲染的调用点虽在 UI 线程，但保留统一语义，
        // 以免将来把写回挪到线程池时漏改（ADR-07 的成本是崩溃，不是降级）。
        var (_, setTick) = UseState(0, threadSafe: true);
        var (savedAt, setSavedAt) = UseState<DateTime?>(null, threadSafe: true);
        var (backupName, setBackupName) = UseState<string?>(null, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);

        // 多 Toast 堆叠队列（对齐旧系统 Naive UI n-message-provider :max="5"）
        var initialToasts = UseMemo(() => (IReadOnlyList<ToastItem>)Array.Empty<ToastItem>(), 0);
        var (toasts, setToasts) = UseState(initialToasts, threadSafe: true);

        // 本页自身也订阅主题状态：配色（战绩卡片/死亡数字）变化时编号步进器等
        // 消费 palette 的元素要跟着重渲染（主题模式由 AppShell 统一施加，不在此列）。
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);

        // 清除缓存确认弹窗（旧版 clear_game_cache 前的确认步骤）
        var (clearDialogOpen, setClearDialogOpen) = UseState(false, threadSafe: true);

        // 云端服务：SignalR 四态徽标状态（EventStream 推送来自后台线程，threadSafe 消费，ADR-07）
        var signalr = services.Services.GetRequiredService<YuumiX.Core.Realtime.SignalrService>();
        var (signalrStatus, setSignalrStatus) = UseState(signalr.Current, threadSafe: true);

        // 云端文本（URL/userid）合并写盘序号
        var (cloudTextTick, setCloudTextTick) = UseState(0, threadSafe: true);

        // 软件更新：检查中 / 检查结果 / 错误 / 下载进度 / 已就绪的安装包路径
        var updateService = services.Services.GetRequiredService<YuumiX.Core.Updates.UpdateService>();
        var (isCheckingUpdate, setCheckingUpdate) = UseState(false, threadSafe: true);
        var (updateInfo, setUpdateInfo) = UseState<YuumiX.Core.Updates.UpdateInfo?>(null, threadSafe: true);
        var (updateCheckError, setUpdateCheckError) = UseState<string?>(null, threadSafe: true);
        var (updateProgress, setUpdateProgress) = UseState<YuumiX.Core.Updates.DownloadProgress?>(null, threadSafe: true);
        var (downloadedUpdatePath, setDownloadedUpdatePath) = UseState<string?>(null, threadSafe: true);

        // 颜色合并写盘用的序号；每次取色变化 +1，被防抖 effect 消费。
        var (colorTick, setColorTick) = UseState(0, threadSafe: true);

        // 路径条目合并写盘用的序号；每次路径文本编辑 +1，被防抖 effect 消费。
        var (pathEditTick, setPathEditTick) = UseState(0, threadSafe: true);

        // 正文列宽度：跟随滚动区宽度收缩，超过上限后固定并居中（原因见 ContentColumn 注释）。
        var (columnWidth, setColumnWidth) = UseState(ContentColumn.MaxWidth, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () =>
                {
                    subscription.Dispose();
                    lock (_toastTimers)
                    {
                        foreach (var t in _toastTimers)
                        {
                            t.Dispose();
                        }
                        _toastTimers.Clear();
                    }
                };
            },
            0);

        var logger = _logger ??= services.LoggerFactory.CreateLogger<SettingsPage>();

        void Rerender() => setTick(++_renderTick);

        void ShowToast(string message, bool isError = false)
        {
            var id = Interlocked.Increment(ref _nextToastId);
            var item = new ToastItem(id, message, isError);

            lock (_activeToasts)
            {
                _activeToasts.Add(item);
                if (_activeToasts.Count > 5)
                {
                    _activeToasts.RemoveAt(0);
                }
                setToasts(_activeToasts.ToList());
            }

            _dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                _dispatcher?.TryEnqueue(() =>
                {
                    lock (_activeToasts)
                    {
                        _activeToasts.RemoveAll(t => t.Id == id);
                        setToasts(_activeToasts.ToList());
                    }
                    lock (_toastTimers)
                    {
                        if (timer is not null)
                        {
                            _toastTimers.Remove(timer);
                            timer.Dispose();
                        }
                    }
                });
            }, null, 2500, Timeout.Infinite);

            lock (_toastTimers)
            {
                _toastTimers.Add(timer);
            }
        }

        // 取色变化：内存与界面即时生效，写盘延后合并（见 ColorCommitDelayMs）。
        void RequestColorCommit(string field)
        {
            _pendingColorField = field;
            setColorTick(++_colorTick);
        }

        // 路径条目文本变化：先记草稿（渲染时回填用），写盘延后合并（见 PathCommitDelayMs）。
        void RequestPathCommit(int index, bool isWeGame, string draft)
        {
            _pathEditIndex = index;
            _pathEditIsWeGame = isWeGame;
            _pathEditDraft = draft;
            setPathEditTick(++_pathEditTick);
        }

        // 云端服务文本变化：记草稿，防抖后落盘并触发 Hub 重启/停止
        void RequestCloudTextCommit(string? urlDraft, string? userIdDraft)
        {
            if (urlDraft is not null)
            {
                _cloudUrlDraft = urlDraft;
            }

            if (userIdDraft is not null)
            {
                _cloudUserIdDraft = userIdDraft;
            }

            setCloudTextTick(++_cloudTextTick);
        }

        // 把待提交的路径草稿写入配置（空值跳过，对齐旧 handleEditPathDirect）。
        void ApplyPathEdit()
        {
            if (_pathEditIndex < 0)
            {
                return;
            }

            var value = (_pathEditDraft ?? string.Empty).Trim();
            var index = _pathEditIndex;
            var isWeGame = _pathEditIsWeGame;
            _pathEditIndex = -1;
            _pathEditDraft = null;
            _pathEditIsWeGame = false;

            if (isWeGame)
            {
                if ((config.General.WegamePath ?? string.Empty) == value)
                {
                    return;
                }

                config.General.WegamePath = value.Length == 0 ? null : value;
                Commit(nameof(GeneralConfig.WegamePath));
                return;
            }

            var paths = config.General.LolPath;
            if (index >= paths.Count || value.Length == 0 || paths[index] == value)
            {
                return;
            }

            paths[index] = value;
            Commit(nameof(GeneralConfig.LolPath));
        }

        // 防抖：pathEditTick 每变一次就重置定时器，只有最后一次会真正落盘（与颜色防抖同一套机制）。
        UseEffect(
            () =>
            {
                if (pathEditTick == 0)
                {
                    return static () => { };
                }

                _dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var timer = new Timer(
                    _ => _dispatcher?.TryEnqueue(ApplyPathEdit),
                    null,
                    PathCommitDelayMs,
                    Timeout.Infinite);
                return () => timer.Dispose();
            },
            pathEditTick);

        // SignalR 状态流订阅（四态徽标）
        UseEffect(
            () =>
            {
                var subscription = signalr.Status.Subscribe(setSignalrStatus);
                return () => subscription.Dispose();
            },
            0);

        // 把云端文本草稿写入配置并触发 Hub 重启/停止（旧 update_config 的差异副作用）。
        void ApplyCloudTextEdit()
        {
            var url = _cloudUrlDraft;
            var userId = _cloudUserIdDraft;
            _cloudUrlDraft = null;
            _cloudUserIdDraft = null;
            _cloudTextTick = 0;

            if (url is not null && url.Trim() != config.General.UploadApiUrl)
            {
                config.General.UploadApiUrl = url.Trim();
                Commit(nameof(GeneralConfig.UploadApiUrl));
            }

            if (userId is not null && userId.Trim() != config.General.SignalrUserId)
            {
                config.General.SignalrUserId = userId.Trim();
                Commit(nameof(GeneralConfig.SignalrUserId));
            }

            _ = signalr.ApplyConfigAsync();
        }

        // 防抖：云端文本停止输入 600ms 后落盘（同款机制）
        UseEffect(
            () =>
            {
                if (cloudTextTick == 0)
                {
                    return static () => { };
                }

                _dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var timer = new Timer(
                    _ => _dispatcher?.TryEnqueue(ApplyCloudTextEdit),
                    null,
                    PathCommitDelayMs,
                    Timeout.Infinite);
                return () => timer.Dispose();
            },
            cloudTextTick);

        // 防抖：colorTick 每变一次就重置定时器，只有最后一次会真正落盘。
        // 定时器回调在线程池，写盘与改状态一律 TryEnqueue 回 UI 线程（ADR-12 决策一）。
        UseEffect(
            () =>
            {
                if (colorTick == 0)
                {
                    return static () => { };
                }

                _dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var timer = new Timer(
                    _ => _dispatcher?.TryEnqueue(() =>
                    {
                        var field = _pendingColorField;
                        if (field is null)
                        {
                            return;
                        }

                        _pendingColorField = null;
                        Commit(field);
                    }),
                    null,
                    ColorCommitDelayMs,
                    Timeout.Infinite);
                return () => timer.Dispose();
            },
            colorTick);

        // 卸载兜底：防抖窗口内切走页面时，直接把待写颜色/路径落盘（不碰 UI 状态，避免卸载后 setState）。
        UseEffect(
            () => () =>
            {
                var dirty = false;
                if (_pendingColorField is not null)
                {
                    _pendingColorField = null;
                    dirty = true;
                }

                if (_pathEditIndex >= 0)
                {
                    // 卸载时没有重渲染可依赖，直接把草稿写进配置对象
                    var value = (_pathEditDraft ?? string.Empty).Trim();
                    if (_pathEditIsWeGame)
                    {
                        config.General.WegamePath = value.Length == 0 ? null : value;
                    }
                    else if (value.Length > 0 && _pathEditIndex < config.General.LolPath.Count)
                    {
                        config.General.LolPath[_pathEditIndex] = value;
                    }

                    _pathEditIndex = -1;
                    _pathEditDraft = null;
                    _pathEditIsWeGame = false;
                    dirty = true;
                }

                if (_cloudUrlDraft is not null)
                {
                    config.General.UploadApiUrl = _cloudUrlDraft.Trim();
                    _cloudUrlDraft = null;
                    dirty = true;
                }

                if (_cloudUserIdDraft is not null)
                {
                    config.General.SignalrUserId = _cloudUserIdDraft.Trim();
                    _cloudUserIdDraft = null;
                    dirty = true;
                }

                if (!dirty)
                {
                    return;
                }

                try
                {
                    services.ConfigStore.SaveWithBackup(config);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "离开设置页时补写防抖中的设置失败");
                }
            },
            0);

        // 单次写回：先落盘（必要时备份），成功与失败都只改状态，不抛给渲染流程。
        void Commit(string field)
        {
            try
            {
                var backup = services.ConfigStore.SaveWithBackup(config);
                setError(null);
                setSavedAt(DateTime.Now);
                if (backup is not null)
                {
                    setBackupName(Path.GetFileName(backup));
                }

                ShowToast("设置已自动保存");
                logger.LogInformation("设置已写回: {Field}", field);
            }
            catch (Exception ex)
            {
                // 写失败必须让用户看见：静默吞掉会让人以为设置已保存（ConfigStore.Save 的约定）
                setError(ex.Message);
                setSavedAt(null);
                ShowToast($"设置保存失败: {ex.Message}", isError: true);
                logger.LogError(ex, "设置写回失败: {Field}", field);
            }

            Rerender();
        }

        // 分组标题只给语义层级与间距，字重/字号/颜色由 Subtitle 与主题提供。
        Element GroupHeader(string title) =>
            Subtitle(title)
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .Margin(top: 16);

        // 颜色改动统一出口：先落到内存配置与主题状态（界面即时生效），再由防抖 effect 落盘。
        void ApplyColor(string field, string value, Action<string> assign)
        {
            assign(value);
            theme.Apply(config.Personalization);
            RequestColorCommit(field);
        }

        // 单个颜色编辑块：色块预览 + 当前值 + 原生取色器。
        Element ColorEditor(
            string label,
            string description,
            string current,
            bool withAlpha,
            Func<Windows.UI.Color, string> toStorage,
            Action<string> assign,
            string field,
            string resetValue)
        {
            var normalized = ColorHex.NormalizeArgb(current, resetValue);
            ColorHex.TryParseArgb(current, out var a, out var r, out var g, out var b);

            var picker = ColorPicker(
                Windows.UI.Color.FromArgb(a, r, g, b),
                color =>
                {
                    var next = toStorage(color);
                    if (ColorHex.NormalizeArgb(next, resetValue) == normalized)
                    {
                        return;
                    }

                    ApplyColor(field, next, assign);
                });

            if (withAlpha)
            {
                picker = picker.AlphaEnabled(true);
            }

            return VStack(8,
                HStack(12,
                    ColorSwatch(normalized),
                    VStack(2,
                        TextBlock(label),
                        Caption(description)),
                    TextBlock(normalized).Foreground(Theme.SecondaryText)),
                picker.Width(320).AutomationName(label));
        }

        Element CardColorEditor() =>
            SettingsCardKit.Expander(
                "战绩卡片配色",
                "胜利、失败与重开卡片的底色与透明度",
                SettingsStatus.Custom,
                headerControl: null,
                innerContent: VStack(16,
                    ColorEditor(
                        "胜利卡片",
                        "战绩列表里胜利一方的底色（含透明度）",
                        config.Personalization.WinCardColor,
                        withAlpha: true,
                        c => ColorHex.ToArgb(c.A, c.R, c.G, c.B),
                        v => config.Personalization.WinCardColor = v,
                        nameof(PersonalizationConfig.WinCardColor),
                        YuumiTheme.WinCard),
                    ColorEditor(
                        "失败卡片",
                        "失败一方的底色（含透明度）",
                        config.Personalization.LoseCardColor,
                        withAlpha: true,
                        c => ColorHex.ToArgb(c.A, c.R, c.G, c.B),
                        v => config.Personalization.LoseCardColor = v,
                        nameof(PersonalizationConfig.LoseCardColor),
                        YuumiTheme.LoseCard),
                    ColorEditor(
                        "重赛卡片",
                        "重赛一方的底色（含透明度）",
                        config.Personalization.RemakeCardColor,
                        withAlpha: true,
                        c => ColorHex.ToArgb(c.A, c.R, c.G, c.B),
                        v => config.Personalization.RemakeCardColor = v,
                        nameof(PersonalizationConfig.RemakeCardColor),
                        YuumiTheme.RemakeCard),
                    Button("恢复默认", () =>
                    {
                        config.Personalization.WinCardColor = YuumiTheme.WinCard;
                        config.Personalization.LoseCardColor = YuumiTheme.LoseCard;
                        config.Personalization.RemakeCardColor = YuumiTheme.RemakeCard;
                        theme.Apply(config.Personalization);
                        RequestColorCommit(nameof(PersonalizationConfig.WinCardColor));
                    })),
                statusText: "自定义配色");

        Element DeathColorEditor() =>
            SettingsCardKit.Expander(
                "死亡数字配色",
                "战绩列表与详情中死亡数字的颜色",
                SettingsStatus.Custom,
                headerControl: null,
                innerContent: VStack(16,
                    ColorEditor(
                        "浅色主题",
                        "浅色主题下每局死亡数的颜色",
                        config.Personalization.LightDeathsNumberColor,
                        withAlpha: false,
                        c => ColorHex.ToArgb(0xFF, c.R, c.G, c.B),
                        v => config.Personalization.LightDeathsNumberColor = v,
                        nameof(PersonalizationConfig.LightDeathsNumberColor),
                        YuumiTheme.LightDeath),
                    ColorEditor(
                        "深色主题",
                        "深色主题下每局死亡数的颜色",
                        config.Personalization.DarkDeathsNumberColor,
                        withAlpha: false,
                        c => ColorHex.ToArgb(0xFF, c.R, c.G, c.B),
                        v => config.Personalization.DarkDeathsNumberColor = v,
                        nameof(PersonalizationConfig.DarkDeathsNumberColor),
                        YuumiTheme.DarkDeath),
                    Button("恢复默认", () =>
                    {
                        config.Personalization.LightDeathsNumberColor = YuumiTheme.LightDeath;
                        config.Personalization.DarkDeathsNumberColor = YuumiTheme.DarkDeath;
                        theme.Apply(config.Personalization);
                        RequestColorCommit(nameof(PersonalizationConfig.LightDeathsNumberColor));
                    })),
                statusText: "自定义配色");

        Element ProxyAddressRow()
        {
            static bool LooksValid(string value) =>
                value.Length == 0
                || (value.Contains(':') && !value.Contains(' ') && Uri.TryCreate("http://" + value, UriKind.Absolute, out _));

            return HStack(8,
                // 开关关闭时禁用输入（对齐旧 ProxySection 的 disabled 联动）
                TextBox(_proxyDraft ?? config.General.HttpProxyAddr, v => _proxyDraft = v)
                    .Width(240)
                    .IsEnabled(config.General.EnableHttpProxy)
                    .AutomationName("代理地址"),
                Button("应用", () =>
                {
                    var next = (_proxyDraft ?? config.General.HttpProxyAddr).Trim();
                    if (!LooksValid(next))
                    {
                        setError("代理地址格式不对，应形如 127.0.0.1:7897");
                        Rerender();
                        return;
                    }

                    if (next == config.General.HttpProxyAddr)
                    {
                        return;
                    }

                    config.General.HttpProxyAddr = next;
                    Commit(nameof(GeneralConfig.HttpProxyAddr));
                })
                .IsEnabled(config.General.EnableHttpProxy)
                .AutomationName("应用代理"));
        }

        // ══════════════════════════════════════════════════════
        // 设置页卡片装配（全部走 SettingsCardKit）
        // ══════════════════════════════════════════════════════
        var lolPathStatus = config.General.LolPath.Count == 0
            ? "未配置"
            : $"已设置 {config.General.LolPath.Count} 个路径";

        // 路径条目行：普通条目 = 可编辑输入框 + 删除；WeGame 条目 = 徽章 + WegamePath 输入框 + 删除。
        // 输入框值优先取编辑草稿（防抖期间的重渲染不能打断正在输入的文本）。
        Element PathRow(int index, string path)
        {
            var isWeGame = path == AppConfig.WeGameMarker;
            var draftActive = _pathEditIndex == index && _pathEditIsWeGame == isWeGame;
            var textValue = draftActive
                ? _pathEditDraft ?? string.Empty
                : isWeGame ? config.General.WegamePath ?? string.Empty : path;

            Element input = isWeGame
                ? TextBox(textValue, v => RequestPathCommit(index, true, v), "WeGame 安装目录")
                    .AutomationName("WeGame 路径")
                : TextBox(textValue, v => RequestPathCommit(index, false, v), "英雄联盟客户端安装目录")
                    .AutomationName($"客户端路径 {index + 1}");

            var rowChildren = new List<Element>();
            if (isWeGame)
            {
                rowChildren.Add(
                    Border(
                        Caption("WeGame")
                            .SemiBold()
                            .Foreground(Theme.AccentText)
                    )
                    .Padding(new Thickness(8, 4, 8, 4))
                    .CornerRadius(4)
                    .Background(Theme.Accent)
                    .VAlign(VerticalAlignment.Center));
            }

            rowChildren.Add(input.HAlign(HorizontalAlignment.Stretch).Grid(column: 0));
            rowChildren.Add(
                Button("✕", () =>
                {
                    // 删除 WeGame 条目时联动清空 WegamePath（对齐旧 handleRemovePath）
                    var removed = config.General.LolPath[index];
                    config.General.LolPath.RemoveAt(index);
                    if (removed == AppConfig.WeGameMarker)
                    {
                        config.General.WegamePath = null;
                    }

                    Commit(nameof(GeneralConfig.LolPath));
                })
                .Padding(new Thickness(6, 2, 6, 2))
                .MinWidth(0)
                .AutomationName($"删除路径 {index + 1}")
                .VAlign(VerticalAlignment.Center)
                .Grid(column: 1));

            return Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [.. rowChildren])
                .Padding(new Thickness(0, 4, 0, 4))
                .WithKey($"path-{index}-{path}");
        }

        Element LolPathEditor()
        {
            var rows = new List<Element>();
            for (var i = 0; i < config.General.LolPath.Count; i++)
            {
                rows.Add(PathRow(i, config.General.LolPath[i]));
            }

            var content = new List<Element>
            {
                // 空列表提示（对齐旧 pathEmpty 文案）
                rows.Count == 0
                    ? Caption("暂无客户端路径").Foreground(Theme.SecondaryText).HAlign(HorizontalAlignment.Center).Margin(top: 4, bottom: 8)
                    : VStack(0, [.. rows]),
            };

            // 说明文案
            content.Add(Caption("启动时将自动检测并提取客户端锁文件凭据；支持配置多个安装目录")
                .Foreground(Theme.SecondaryText)
                .Margin(top: 4, bottom: 8));

            // 操作按钮行：自动检测 / 浏览文件夹 / 添加 WeGame 启动项
            content.Add(HStack(8,
                Button("自动检测", async () =>
                {
                    var detected = await Task.Run(() => services.Services
                        .GetRequiredService<LolPathDetector>()
                        .DetectLolPath());
                    if (detected is null)
                    {
                        ShowToast("未检测到运行中的英雄联盟客户端", isError: true);
                        return;
                    }

                    if (config.General.LolPath.Contains(detected, StringComparer.Ordinal))
                    {
                        ShowToast("该路径已存在");
                        return;
                    }

                    config.General.LolPath.Add(detected);
                    Commit(nameof(GeneralConfig.LolPath));
                    ShowToast($"已添加: {detected}");
                }).AutomationName("自动检测客户端路径"),
                Button("浏览文件夹", async () =>
                {
                    var picked = await Services.FolderPickerService.PickFolderAsync();
                    if (picked is null)
                    {
                        return;
                    }

                    // 与路径列表其余条目同格式：正斜杠 + 盘符大写
                    var normalized = LolPathDetector.NormalizePath(picked);
                    if (config.General.LolPath.Contains(normalized, StringComparer.Ordinal))
                    {
                        ShowToast("该路径已存在");
                        return;
                    }

                    config.General.LolPath.Add(normalized);
                    Commit(nameof(GeneralConfig.LolPath));
                    ShowToast($"已添加: {normalized}");
                }).AutomationName("浏览客户端文件夹"),
                Button("添加 WeGame 启动项", async () =>
                {
                    if (config.General.LolPath.Contains(AppConfig.WeGameMarker))
                    {
                        ShowToast("WeGame 启动项已存在");
                        return;
                    }

                    var detected = await Task.Run(() => services.Services
                        .GetRequiredService<LolPathDetector>()
                        .DetectWeGameDirectory());
                    if (detected is null)
                    {
                        ShowToast("未检测到 WeGame 安装路径，请先安装 WeGame", isError: true);
                        return;
                    }

                    config.General.LolPath.Add(AppConfig.WeGameMarker);
                    config.General.WegamePath = detected;
                    Commit(nameof(GeneralConfig.LolPath));
                    ShowToast($"已添加 WeGame 启动项 ({detected})");
                }).AutomationName("添加 WeGame 启动项")).HAlign(HorizontalAlignment.Right));

            return VStack(4, [.. content]);
        }

        var lolPathCard = SettingsCardKit.Expander(
            "英雄联盟客户端路径",
            "设置英雄联盟客户端所在目录的路径",
            config.General.LolPath.Count == 0 ? SettingsStatus.Unconfigured : SettingsStatus.Custom,
            headerControl: null,
            innerContent: LolPathEditor(),
            statusText: lolPathStatus);

        var clearCacheCard = SettingsCardKit.Card(
            "清除缓存", "删除所有游戏资源（英雄图标/装备/符文）与 OP.GG 数据的磁盘缓存",
            Button("清除缓存", () => setClearDialogOpen(true)).AutomationName("清除缓存按钮"));

        // 软件更新（对应旧 UpdateSection.vue + UpdateDialog.vue；本轮交付最小形态：
        // 检查按钮 + 新版信息卡 + 进度/错误 + 安装入口，弹窗细节留待下一步）
        async Task CheckForUpdateAsync()
        {
            if (isCheckingUpdate) return;
            setCheckingUpdate(true);
            setUpdateCheckError(null);
            setUpdateInfo(null);
            try
            {
                var info = await updateService.CheckForUpdateAsync(services.Paths.IsPortable);
                setUpdateInfo(info);
                ShowToast(info is null ? "当前已是最新版本" : $"发现新版本 v{info.Version}");
            }
            catch (Exception ex)
            {
                setUpdateCheckError(ex.Message);
                ShowToast($"检查更新失败: {ex.Message}", isError: true);
            }
            finally
            {
                setCheckingUpdate(false);
            }
        }

        async Task DownloadUpdateAsync()
        {
            var info = updateInfo;
            if (info is null) return;
            setUpdateCheckError(null);
            var progress = new Progress<YuumiX.Core.Updates.DownloadProgress>(p =>
            {
                // Progress<T> 已封送回 UI 线程（ADR-07 无需 threadSafe 处理，但保留统一语义）
                setUpdateProgress(p);
            });
            try
            {
                string path = await updateService.DownloadUpdateAsync(info, progress);
                setDownloadedUpdatePath(path);
                ShowToast("更新下载完成，可立即安装");
            }
            catch (Exception ex)
            {
                setUpdateCheckError(ex.Message);
                ShowToast($"下载更新失败: {ex.Message}", isError: true);
            }
        }

        void ApplyPendingUpdate()
        {
            var path = downloadedUpdatePath;
            if (path is null) return;
            try
            {
                YuumiX.Core.Updates.UpdateService.ApplyPendingUpdate(path, services.Paths.IsPortable);
            }
            catch (Exception ex)
            {
                setUpdateCheckError(ex.Message);
                ShowToast($"安装更新失败: {ex.Message}", isError: true);
            }
        }

        var updateCardStatus = updateInfo is { } found ? $"发现新版 v{found.Version}" : "已是最新";
        var updateCardBody = VStack(8,
            SettingsCardKit.Card("检查更新", "手动检查并下载新版本",
                Button(isCheckingUpdate ? "检查中..." : "检查更新", () => _ = CheckForUpdateAsync())
                    .IsEnabled(!isCheckingUpdate)
                    .AutomationName("检查更新按钮")),
            updateCheckError is not null
                ? Body($"检查/下载失败： {updateCheckError}")
                    .Foreground(Theme.SystemCritical)
                : Empty(),
            updateInfo is not null
                ? VStack(4,
                    InfoRow("新版本", $"v{updateInfo.Version}"),
                    InfoRow("当前版本", $"v{updateInfo.CurrentVersion}"),
                    string.IsNullOrEmpty(updateInfo.Notes)
                        ? Empty()
                        : Caption(updateInfo.Notes).Foreground(Theme.SecondaryText).TextWrapping(),
                    // 下载与安装区：下载中显示进度，就绪后切换为安装按钮
                    downloadedUpdatePath is not null
                        ? Button(services.Paths.IsPortable ? "应用更新并重启" : "立即重启安装", ApplyPendingUpdate)
                            .AutomationName("安装更新按钮")
                        : updateProgress is { } p
                            ? VStack(4,
                                Progress(p.Percent ?? 0),
                                Caption($"已下载 {p.Downloaded / 1024.0 / 1024.0:F1} MB" + (p.Total is { } t ? $" / {t / 1024.0 / 1024.0:F1} MB" : string.Empty)))
                            : Button("下载更新", () => _ = DownloadUpdateAsync()).AutomationName("下载更新按钮"))
                : Empty(),
            SettingsCardKit.Card("开源发布页", "在浏览器中打开 GitHub Releases",
                Button("前往 GitHub Releases", () =>
                {
                    try
                    {
                        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = $"https://github.com/{YuumiX.Core.Updates.GitHubClient.Repository}/releases/latest",
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "打开 GitHub Releases 失败");
                    }
                }).AutomationName("前往 GitHub Releases")));

        var updateCard = SettingsCardKit.Expander(
            "软件更新",
            services.Paths.IsPortable
                ? "便携版通过 zip 覆盖方式更新；检查到新版后手动下载应用"
                : "检查新版本并后台下载，就绪后重启安装",
            SettingsStatus.Custom,
            headerControl: null,
            innerContent: updateCardBody,
            statusText: updateCardStatus);

        // 云端服务（对应旧 CloudServiceSection.vue）：LCU 实时开关进页头，URL / userid / 连接状态留在展开区。
        string SignalrBadgeText(SignalrStatus status) => status.State switch
        {
            SignalrConnectionState.Connected => "已连接",
            SignalrConnectionState.Connecting => "连接中",
            SignalrConnectionState.Error => "连接失败",
            _ => "未连接",
        };

        Element SignalrBadgeElement(SignalrStatus status)
        {
            Element Dot(ThemeRef color) =>
                Border(Empty()).Width(8).Height(8).CornerRadius(4).Background(color);

            var color = status.State switch
            {
                SignalrConnectionState.Connected => Theme.SystemSuccess,
                SignalrConnectionState.Connecting => Theme.SystemCaution,
                SignalrConnectionState.Error => Theme.SystemCritical,
                _ => Theme.SystemNeutral,
            };

            return HStack(8,
                Dot(color),
                Caption(SignalrBadgeText(status)).Foreground(color));
        }

        var cloudCardBody = VStack(8,
            // SignalR 四态徽标行（仅实时开启时展示，对齐旧头部徽标）
            config.Functions.LcuRealtimeEnabled
                ? HStack(12,
                    SignalrBadgeElement(signalrStatus),
                    Caption("SignalR 连接状态").Foreground(Theme.SecondaryText))
                : Empty(),
            SettingsCardKit.Card("服务器 API 地址", "对局数据上传的服务端地址",
                TextBox(
                    _cloudUrlDraft ?? config.General.UploadApiUrl,
                    v => RequestCloudTextCommit(v, null),
                    "http://example.com")
                    .Width(240)
                    .AutomationName("服务器 API 地址")),
            SettingsCardKit.Card("userid", "上报数据使用的用户标识",
                TextBox(
                    _cloudUserIdDraft ?? config.General.SignalrUserId,
                    v => RequestCloudTextCommit(null, v),
                    "留空默认使用 lcu_user_001")
                    .Width(240)
                    .AutomationName("SignalR userid")),
            // error 态附红字错误详情（对齐旧 setting-error-tip）
            signalrStatus.State == SignalrConnectionState.Error && !string.IsNullOrEmpty(signalrStatus.Error)
                ? Caption($"连接异常： {signalrStatus.Error}")
                    .Foreground(Theme.SystemCritical)
                : Empty());

        var cloudCard = SettingsCardKit.Expander(
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
                Commit(nameof(FunctionsConfig.LcuRealtimeEnabled));
                // 即时应用：开启 → 开始连接（徽标经状态流转 connecting），关闭 → 停止
                _ = signalr.ApplyConfigAsync();
            }, header: null, onContent: null, offContent: null)
                .AutomationName("LCU 实时查询开关"),
            innerContent: cloudCardBody);

        var hideMenuEnabled = config.Functions.HideTft || config.Functions.HideSavedPlayers;
        var hideMenuCard = SettingsCardKit.Expander(
            "隐藏菜单",
            "控制侧边栏云顶之弈与路人集功能入口的显隐",
            hideMenuEnabled ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            // 两项入口的公共开关：关掉即同时显示两个入口，单项仍可在展开区单独控制。
            headerControl: ToggleSwitch(hideMenuEnabled, v =>
            {
                if (v == hideMenuEnabled) return;
                config.Functions.HideTft = v;
                config.Functions.HideSavedPlayers = v;
                menu.Apply(v, v);
                Commit(nameof(FunctionsConfig.HideTft));
            }, header: null, onContent: null, offContent: null)
                .AutomationName("隐藏菜单开关"),
            innerItems:
            [
                SettingsCardKit.Card("隐藏云顶之弈入口", "侧边栏不显示云顶之弈功能入口",
                    ToggleSwitch(config.Functions.HideTft, v =>
                    {
                        if (v == config.Functions.HideTft) return;
                        config.Functions.HideTft = v;
                        // 先推给 AppShell（立即生效），再落盘；写盘失败时侧边栏也已更新，
                        // 与颜色设置「先应用主题后防抖写盘」的语义一致。
                        menu.Apply(config.Functions.HideTft, config.Functions.HideSavedPlayers);
                        Commit(nameof(FunctionsConfig.HideTft));
                    }, header: null, onContent: null, offContent: null).AutomationName("隐藏云顶之弈")),
                SettingsCardKit.Card("隐藏路人集入口", "侧边栏不显示路人集功能入口",
                    ToggleSwitch(config.Functions.HideSavedPlayers, v =>
                    {
                        if (v == config.Functions.HideSavedPlayers) return;
                        config.Functions.HideSavedPlayers = v;
                        menu.Apply(config.Functions.HideTft, config.Functions.HideSavedPlayers);
                        Commit(nameof(FunctionsConfig.HideSavedPlayers));
                    }, header: null, onContent: null, offContent: null).AutomationName("隐藏路人集")),
            ]);

        // 截图触发事件复选组：3=三杀 4=四杀 5=五杀 8=超神（写入 ScreenshotOnMultikillLevels）
        Element ScreenshotLevelCheckBox(int level, string label)
        {
            var levels = config.Functions.ScreenshotOnMultikillLevels;
            return CheckBox(
                levels.Contains(level),
                v =>
                {
                    var changed = false;
                    if (v && !levels.Contains(level))
                    {
                        levels.Add(level);
                        changed = true;
                    }
                    else if (!v && levels.Contains(level))
                    {
                        levels.Remove(level);
                        changed = true;
                    }

                    if (changed)
                    {
                        Commit(nameof(FunctionsConfig.ScreenshotOnMultikillLevels));
                    }
                },
                label).AutomationName(label);
        }

        var screenshotSavePathRow = HStack(8,
            // 只读展示（旧版 n-input readonly 同款），placeholder 提示默认位置
            TextBox(config.Functions.ScreenshotSavePath, _ => { }, "默认保存在系统「图片」目录下的 Yuumi_Screenshots 文件夹")
                .Width(240)
                .IsReadOnly()
                .AutomationName("截图保存目录"),
            Button("浏览", async () =>
            {
                var picked = await FolderPickerService.PickFolderAsync();
                if (picked is null)
                {
                    return;
                }

                config.Functions.ScreenshotSavePath = LolPathDetector.NormalizePath(picked);
                Commit(nameof(FunctionsConfig.ScreenshotSavePath));
                ShowToast("已成功更新截图保存目录");
            }).AutomationName("浏览截图目录"),
            Button("打开目录", () =>
            {
                try
                {
                    // 与旧 open_screenshot_folder 一致：目录不存在时自动创建
                    var dir = LiveEventMatcher.ResolveSaveDirectory(config.Functions.ScreenshotSavePath);
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "打开截图目录失败");
                    ShowToast($"无法打开截图文件夹: {ex.Message}", isError: true);
                }
            }).AutomationName("打开截图目录"));

        var screenshotCard = SettingsCardKit.Expander(
            "多杀自动截图",
            "对局中达成多杀或超神时自动保存精彩瞬间",
            config.Functions.EnableScreenshotOnMultikill ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            headerControl: ToggleSwitch(config.Functions.EnableScreenshotOnMultikill, v =>
            {
                if (v == config.Functions.EnableScreenshotOnMultikill) return;
                config.Functions.EnableScreenshotOnMultikill = v;
                Commit(nameof(FunctionsConfig.EnableScreenshotOnMultikill));
            }, header: null, onContent: null, offContent: null)
                .AutomationName("多杀截图开关"),
            // 开关关闭时子项不可编辑（对齐旧 ScreenshotSection.vue 的「开启后才显示」）
            innerItems:
            [
                SettingsCardKit.Card("截图触发事件", "达成以下任一事件时保存截图",
                        HStack(12,
                            ScreenshotLevelCheckBox(3, "三杀"),
                            ScreenshotLevelCheckBox(4, "四杀"),
                            ScreenshotLevelCheckBox(5, "五杀"),
                            ScreenshotLevelCheckBox(8, "超神")))
                    .IsEnabled(config.Functions.EnableScreenshotOnMultikill),
                SettingsCardKit.Card("截图保存位置", "截图文件写入的目录", screenshotSavePathRow)
                    .IsEnabled(config.Functions.EnableScreenshotOnMultikill),
            ]);

        var logLevelCard = SettingsCardKit.Card(
            "日志等级", "写入 <exe>/log 的日志记录详细程度，修改后立即生效",
            ComboBox(LogLevelLabels, IndexOf(LogLevelValues, config.General.LogLevel, LogLevelValues.Length - 1), index =>
            {
                var level = LogLevelValues[index];
                config.General.LogLevel = level;
                services.ApplyLogLevel(level);
                Commit(nameof(GeneralConfig.LogLevel));
            }).Width(160).AutomationName("日志等级选择"));

        var logDirCard = SettingsCardKit.Card(
            "日志文件", $"存放于 {services.Paths.LogDirectory}",
            Button("打开目录", () =>
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", services.Paths.LogDirectory);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "打开日志目录失败");
                }
            }).AutomationName("打开日志目录"));

        var httpProxyCard = SettingsCardKit.Expander(
            "软件 HTTP 代理",
            "软件更新、TFT 数据、OP.GG 服务访问走此 HTTP 代理通道",
            config.General.EnableHttpProxy ? SettingsStatus.Enabled : SettingsStatus.Disabled,
            headerControl: ToggleSwitch(config.General.EnableHttpProxy, v =>
            {
                if (v == config.General.EnableHttpProxy) return;
                config.General.EnableHttpProxy = v;
                Commit(nameof(GeneralConfig.EnableHttpProxy));
            }, header: null, onContent: null, offContent: null)
                .AutomationName("HTTP代理开关"),
            innerContent: SettingsCardKit.Card("代理地址", "形如 127.0.0.1:7897；开关关闭时不可编辑", ProxyAddressRow()));

        var themeModeCard = SettingsCardKit.Card(
            "主题模式", "浅色 / 深色 / 跟随系统主题自适应切换",
            ComboBox(ThemeModeLabels, IndexOf(ThemeModeValues, config.Personalization.ThemeMode, ThemeModeValues.Length - 1), index =>
            {
                config.Personalization.ThemeMode = ThemeModeValues[index];
                theme.Apply(config.Personalization);
                Commit(nameof(PersonalizationConfig.ThemeMode));
            }).Width(160).AutomationName("主题模式选择"));

        var aboutCard = SettingsCardKit.Expander(
            "关于 YuumiX",
            $"当前版本 {AppVersion} · 运行环境与存储位置",
            SettingsStatus.Custom,
            headerControl: null,
            innerContent: VStack(10,
                InfoRow("应用版本", AppVersion),
                InfoRow("配置版本", $"v{config.Version}"),
                InfoRow("配置文件", services.ConfigStore.FilePath),
                InfoRow("数据目录", services.Paths.DataDirectory),
                InfoRow("便携模式", OnOff(services.Paths.IsPortable)),
                InfoRow("日志目录", services.Paths.LogDirectory),
                InfoRow("写回备份", $"{services.ConfigStore.ListBackups().Count} 份（保留最近 {ConfigStore.MaxBackups} 份）"),
                HStack(8,
                    Button("开源仓库", () =>
                    {
                        try
                        {
                            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = RepositoryUrl,
                                UseShellExecute = true,
                            });
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "打开开源仓库页面失败");
                        }
                    }).AutomationName("打开开源仓库"))),
            statusText: AppVersion);

        Element RenderToasts()
        {
            if (toasts.Count == 0) return Empty();

            return VStack(8,
                toasts.Select(item =>
                {
                    var iconGlyph = item.IsError ? "\uE783" : "\uE73E";
                    var iconColor = item.IsError
                        ? Theme.Ref("SystemFillColorCriticalBrush")
                        : Theme.Ref("SystemFillColorSuccessBrush");

                    return Border(
                        HStack(10,
                            Border(
                                Caption(iconGlyph)
                                    .FontFamily("Segoe Fluent Icons")
                                    .Foreground(iconColor)
                                    .HAlign(HorizontalAlignment.Center)
                                    .VAlign(VerticalAlignment.Center)
                            )
                            .Width(24)
                            .Height(24)
                            .CornerRadius(12)
                            .Background(Theme.Ref("LayerFillColorDefaultBrush")),

                            BodyStrong(item.Message)
                                .VAlign(VerticalAlignment.Center)
                        )
                    )
                    .Padding(new Thickness(16, 8, 20, 8))
                    .CornerRadius(20)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .HAlign(HorizontalAlignment.Center)
                    .WithKey(item.Id.ToString());
                }).ToArray()
            )
            .WithTransitions([
                new AddDeleteThemeTransition(),
                new RepositionThemeTransition()
            ])
            .VAlign(VerticalAlignment.Top)
            .HAlign(HorizontalAlignment.Center)
            .Margin(new Thickness(0, 16, 0, 0));
        }

        var mainScroll = ScrollViewer(
            VStack(8,
                // 页面标题比分组标题（Subtitle 20）高一级，保持视觉层级。
                Title("设置")
                    .HeadingLevel(AutomationHeadingLevel.Level1)
                    .Margin(bottom: 8),

                // 1. 功能
                GroupHeader("功能"),
                SettingsCardKit.Stepper("LCU API 并发数", "该值越大数据加载速度越快，但越可能引起客户端闪退 · 重启后生效", config.Functions.ApiConcurrencyNumber, ApiConcurrencyMin, ApiConcurrencyMax, v =>
                {
                    config.Functions.ApiConcurrencyNumber = v;
                    Commit(nameof(FunctionsConfig.ApiConcurrencyNumber));
                }, palette),
                SettingsCardKit.Stepper("默认对局数量", "调整在个人生涯界面中显示的最大对局数量", config.Functions.CareerGamesNumber, CareerGamesMin, CareerGamesMax, v =>
                {
                    config.Functions.CareerGamesNumber = v;
                    Commit(nameof(FunctionsConfig.CareerGamesNumber));
                }, palette),
                SettingsCardKit.Toggle("对局信息过滤", "基于你当前游戏模式（地图/队列）筛选战绩，只显示相同模式的玩家历史数据", config.Functions.GameInfoFilter, v =>
                {
                    config.Functions.GameInfoFilter = v;
                    Commit(nameof(FunctionsConfig.GameInfoFilter));
                }),
                SettingsCardKit.Toggle("保留对局信息界面内容", "保留上一局的对局信息内容直到下一次对局开始", config.Functions.EnableReserveGameinfo, v =>
                {
                    config.Functions.EnableReserveGameinfo = v;
                    Commit(nameof(FunctionsConfig.EnableReserveGameinfo));
                }),
                SettingsCardKit.Toggle("对局详情中显示段位", "在搜索界面对局详情界面中显示段位，启动该选项将影响加载界面的速度", config.Functions.ShowTierInGameInfo, v =>
                {
                    config.Functions.ShowTierInGameInfo = v;
                    Commit(nameof(FunctionsConfig.ShowTierInGameInfo));
                }),

                // 2. OP.GG
                GroupHeader("OP.GG"),
                SettingsCardKit.Toggle("自动显示 OP.GG 窗口", "在英雄选择开始时自动显示 OP.GG 窗口", config.Functions.AutoShowOpgg, v =>
                {
                    config.Functions.AutoShowOpgg = v;
                    Commit(nameof(FunctionsConfig.AutoShowOpgg));
                }),
                SettingsCardKit.Toggle("置顶 OP.GG 窗口", "在英雄选择时将 OP.GG 窗口置顶", config.Functions.EnableOpggOnTop, v =>
                {
                    config.Functions.EnableOpggOnTop = v;
                    OpggWindowHost.UpdateAlwaysOnTop(v);
                    Commit(nameof(FunctionsConfig.EnableOpggOnTop));
                }),

                // 3. 通用设置
                GroupHeader("通用设置"),
                lolPathCard,
                cloudCard,
                SettingsCardKit.Toggle("随应用启动客户端", "启动 YuumiX 时一并拉起英雄联盟客户端（含 WeGame 形态）", config.General.EnableStartLolWithApp, v =>
                {
                    config.General.EnableStartLolWithApp = v;
                    Commit(nameof(GeneralConfig.EnableStartLolWithApp));
                }),
                SettingsCardKit.Card("关闭到托盘", "关闭窗口时最小化到托盘而不是退出；未设置时按平台默认处理",
                    ThreeStateCheckBox(config.General.EnableCloseToTray, v =>
                    {
                        config.General.EnableCloseToTray = v;
                        Commit(nameof(GeneralConfig.EnableCloseToTray));
                    }).AutomationName("关闭到托盘开关")),
                SettingsCardKit.Toggle("游戏开始时最小化", "检测到对局开始后自动最小化主窗口", config.General.EnableGameStartMinimize, v =>
                {
                    config.General.EnableGameStartMinimize = v;
                    Commit(nameof(GeneralConfig.EnableGameStartMinimize));
                }),
                clearCacheCard,
                SettingsCardKit.Toggle("检查更新", "启动时检查新版本（便携版走整包覆盖）", config.General.EnableCheckUpdate, v =>
                {
                    config.General.EnableCheckUpdate = v;
                    Commit(nameof(GeneralConfig.EnableCheckUpdate));
                }),
                updateCard,

                // 4. 高级与隐藏
                GroupHeader("高级与隐藏"),
                hideMenuCard,
                screenshotCard,

                // 5. 日志与网络
                GroupHeader("日志与网络"),
                logLevelCard,
                logDirCard,
                httpProxyCard,

                // 6. 外观与个性化
                GroupHeader("外观与个性化"),
                SettingsCardKit.Toggle("Mica 背景", "使用系统亚克力/云母材质作为窗口背景 · 即时生效", config.Personalization.MicaEnabled, v =>
                {
                    config.Personalization.MicaEnabled = v;
                    theme.Apply(config.Personalization);
                    Commit(nameof(PersonalizationConfig.MicaEnabled));
                }),
                themeModeCard,
                // 强调色跟随系统，原「主题强调色」卡片已删除；这里只剩业务配色（战绩卡片 / 死亡数字）。
                CardColorEditor(),
                DeathColorEditor(),

                // 7. 关于
                GroupHeader("关于"),
                aboutCard
            )
            // 正文列：显式宽度 + 居中（不要写成 MaxWidth + Stretch，原因见 ContentColumn 注释）。
            .Width(columnWidth)
            .HAlign(HorizontalAlignment.Center)
            .Padding(24)
        )
        // 点击页面空白处时，WinUI 会把焦点下发给滚动容器内容里的「第一个可聚焦控件」，
        // 本页即「LCU API 并发数」的 NumberBox —— 光标会莫名其妙跳进那个输入框。
        // 让滚动容器自身成为可聚焦元素后，焦点停在容器上，不再下发给子控件。
        // 用修饰符而非 .Set(...)：控件走池回收，Set 写入会在重渲染时被重置（REACTOR_POOL_001）。
        .IsTabStop(true);
        mainScroll = mainScroll.OnSizeChanged((_, e) =>
        {
            var target = ContentColumn.Fit(e.NewSize.Width);
            if (Math.Abs(target - columnWidth) >= 0.5)
            {
                setColumnWidth(target);
            }
        });

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                mainScroll.Grid(row: 0, column: 0),
                RenderToasts().Grid(row: 0, column: 0),
                // 清除缓存确认弹窗（声明式：IsOpen 驱动显隐）
                // 必须走 Factories.ContentDialog：Reactor 的 handler 注册是惰性的，只有工厂调用
                // 才会把 ContentDialogElement 注册进 ControlRegistry；直接
                // new Microsoft.UI.Reactor.Core.ContentDialogElement(...) 能编译但在挂载时抛
                // InvalidOperationException（No handler is registered for element type ...）。
                (ContentDialog(
                    "清除缓存",
                    Body("确定要清除所有游戏资源缓存吗？清除后需要重新加载游戏资源。")
                        .TextWrapping(),
                    primaryButtonText: "确定") with
                {
                    IsOpen = clearDialogOpen,
                    CloseButtonText = "取消",
                })
                .Closed(result =>
                {
                    setClearDialogOpen(false);
                    if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    {
                        return;
                    }

                    try
                    {
                        var removed = services.AssetCache.Clear();
                        try
                        {
                            services.OpggHttpClient?.ClearMemoryCache();
                            services.OpggHttpClient?.ClearDiskCache();
                        }
                        catch
                        {
                        }
                        logger.LogInformation("已清空游戏资源与 OP.GG 缓存: {Count} 个文件", removed);
                        ShowToast(removed == 0 ? "缓存目录不存在，无需清除" : $"已清除 {removed} 个缓存文件");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "清除游戏资源缓存失败");
                        ShowToast($"清除缓存失败: {ex.Message}", isError: true);
                    }
                }),
            ]
        );
    }

    /// <summary>纯信息行（关于页），没有「可编辑」语义。</summary>
    private static Element InfoRow(string label, string value) =>
        HStack(16,
            TextBlock(label).Width(320),
            TextBlock(value).Foreground(Theme.SecondaryText).TextWrapping());

    private static Element ColorSwatch(string color) =>
        Border(Empty())
            .Width(56)
            .Height(24)
            .CornerRadius(4)
            .Background(color)
            .WithBorder(Theme.CardStroke, 1);

    private static string OnOff(bool value) => value ? "已开启" : "已关闭";

    private static int IndexOf(IReadOnlyList<int> values, int current, int fallback)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] == current)
            {
                return i;
            }
        }

        return fallback;
    }

    private static int IndexOf(IReadOnlyList<string> values, string? current, int fallback)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], current, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return fallback;
    }
}
