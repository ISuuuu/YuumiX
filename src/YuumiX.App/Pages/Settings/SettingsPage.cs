using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Common;
using YuumiX.Core.Persistence;

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

    /// <summary>
    /// 取色器的合并写盘延迟。ColorPicker 的 ColorChanged 在拖动时每帧触发，
    /// 逐次同步写盘既打断拖动又没有信息增量；停手 400 ms 后写一次就够了。
    /// </summary>
    private const int ColorCommitDelayMs = 400;

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

        // 本页自身也订阅主题状态：强调色变化时要跟着重渲染，否则状态条这类
        // 消费 palette.Accent 的元素会停在旧颜色上（主题模式由 AppShell 统一施加，不在此列）。
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);

        // 可展开卡片记录（默认全部收起，对齐工具箱与 Fluent 规范）
        var initialExpanded = UseMemo(() => new HashSet<string>(), 0);
        var (expandedKeys, setExpandedKeys) = UseState(initialExpanded, threadSafe: true);

        // 颜色合并写盘用的序号；每次取色变化 +1，被防抖 effect 消费。
        var (colorTick, setColorTick) = UseState(0, threadSafe: true);
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

        // 卸载兜底：防抖窗口内切走页面时，直接把待写颜色落盘（不碰 UI 状态，避免卸载后 setState）。
        UseEffect(
            () => () =>
            {
                if (_pendingColorField is null)
                {
                    return;
                }

                try
                {
                    services.ConfigStore.SaveWithBackup(config);
                    _pendingColorField = null;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "离开设置页时补写颜色失败");
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

        void ToggleExpanded(string key)
        {
            var next = new HashSet<string>(expandedKeys);
            if (!next.Add(key))
            {
                next.Remove(key);
            }
            setExpandedKeys(next);
        }

        Element CardRow(string label, Element control) =>
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    TextBlock(label)
                        .FontSize(13)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    control.Grid(row: 0, column: 1),
                ])
            .Padding(new Thickness(0, 8, 0, 8));

        Element GroupHeader(string title) =>
            TextBlock(title)
                .FontSize(13)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(Theme.SecondaryText)
                .Margin(new Thickness(4, 16, 0, 6));

        // 单行直接操作卡片（不含展开，右侧直接展示操作按钮或状态开关）
        Element ActionCard(string iconGlyph, string title, string description, Element control) =>
            Border(
                Grid(
                    [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        Border(
                            TextBlock(iconGlyph)
                                .FontFamily("Segoe Fluent Icons")
                                .FontSize(15)
                                .Foreground(palette.Accent)
                                .HAlign(HorizontalAlignment.Center)
                                .VAlign(VerticalAlignment.Center)
                        )
                        .Width(34)
                        .Height(34)
                        .CornerRadius(17)
                        .Background(Theme.Ref("LayerFillColorDefaultBrush"))
                        .Margin(new Thickness(0, 0, 14, 0))
                        .Grid(row: 0, column: 0),

                        VStack(2,
                            TextBlock(title)
                                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                                .FontSize(14),
                            TextBlock(description)
                                .FontSize(12)
                                .Foreground(Theme.SecondaryText)
                        ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1),

                        control.VAlign(VerticalAlignment.Center).Grid(row: 0, column: 2),
                    ]
                ).Padding(new Thickness(16, 12, 16, 12))
            )
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);

        Element SettingToggleCard(string iconGlyph, string title, string description, bool value, Action<bool> apply, string field, string? note = null) =>
            ActionCard(
                iconGlyph,
                title,
                note is null ? description : $"{description} · {note}",
                ToggleSwitch(value, v =>
                {
                    if (v == value) return;
                    apply(v);
                    Commit(field);
                }, header: null, onContent: "", offContent: "").AutomationName(title));

        Element SettingNumberCard(string iconGlyph, string title, string description, int value, int min, int max, Action<int> apply, string field, string? note = null) =>
            ActionCard(
                iconGlyph,
                title,
                note is null ? description : $"{description} · {note}",
                NumberBox(value, v =>
                {
                    if (double.IsNaN(v)) return;
                    var next = (int)Math.Clamp(v, min, max);
                    if (next == value) return;
                    apply(next);
                    Commit(field);
                }).Width(130).AutomationName(title));

        Element SettingExpandableCard(
            string key,
            string iconGlyph,
            string title,
            string description,
            string statusText,
            Element innerContent)
        {
            var isExpanded = expandedKeys.Contains(key);

            var headerContent = Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                [
                    Border(
                        TextBlock(iconGlyph)
                            .FontFamily("Segoe Fluent Icons")
                            .FontSize(15)
                            .Foreground(palette.Accent)
                            .HAlign(HorizontalAlignment.Center)
                            .VAlign(VerticalAlignment.Center)
                    )
                    .Width(34)
                    .Height(34)
                    .CornerRadius(17)
                    .Background(Theme.Ref("LayerFillColorDefaultBrush"))
                    .Margin(new Thickness(0, 0, 14, 0))
                    .Grid(row: 0, column: 0),

                    VStack(2,
                        TextBlock(title)
                            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                            .FontSize(14),
                        TextBlock(description)
                            .FontSize(12)
                            .Foreground(Theme.SecondaryText)
                    ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1),

                    TextBlock(statusText)
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Margin(new Thickness(12, 0, 10, 0))
                        .Grid(row: 0, column: 2),

                    TextBlock(isExpanded ? "\uE70D" : "\uE76C")
                        .FontFamily("Segoe Fluent Icons")
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 3),
                ]
            ).HAlign(HorizontalAlignment.Stretch);

            var headerButton = Button(headerContent, () => ToggleExpanded(key))
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(0)
                .Padding(new Thickness(16, 12, 16, 12))
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .CornerRadius(8)
                .AutomationName(title);

            Element body;
            if (isExpanded)
            {
                var divider = Border(Empty())
                    .Height(1)
                    .Background(Theme.CardStroke)
                    .Margin(new Thickness(16, 0, 16, 4));

                var paddedContent = Border(innerContent)
                    .Padding(new Thickness(16, 4, 16, 12));

                body = VStack(0, headerButton, divider, paddedContent);
            }
            else
            {
                body = headerButton;
            }

            return Border(body)
                .CornerRadius(8)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
                .HAlign(HorizontalAlignment.Stretch);
        }

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

        Element ThemeColorEditor() =>
            SettingExpandableCard("themeColor", "\uE790", "主题强调色", "界面主色调（不透明）；修改后立即全局生效", ColorHex.NormalizeArgb(config.Personalization.ThemeColor, YuumiTheme.Accent),
                VStack(14,
                    ColorEditor(
                        "主题色",
                        "界面强调色（不透明）；改完立即生效",
                        config.Personalization.ThemeColor,
                        withAlpha: false,
                        c => ColorHex.ToRgb(c.R, c.G, c.B),
                        v => config.Personalization.ThemeColor = v,
                        nameof(PersonalizationConfig.ThemeColor),
                        YuumiTheme.Accent),
                    Button("恢复默认", () => ApplyColor(
                        nameof(PersonalizationConfig.ThemeColor),
                        YuumiTheme.Accent,
                        v => config.Personalization.ThemeColor = v))));

        Element CardColorEditor() =>
            SettingExpandableCard("cardColors", "\uE721", "战绩卡片配色", "胜利、失败与重开卡片的底色与透明度", "自定义配色",
                VStack(16,
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
                    })));

        Element DeathColorEditor() =>
            SettingExpandableCard("deathColors", "\uE711", "死亡数字配色", "战绩列表与详情中死亡数字的颜色", "自定义配色",
                VStack(16,
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
                    })));

        Element ProxyAddressRow()
        {
            static bool LooksValid(string value) =>
                value.Length == 0
                || (value.Contains(':') && !value.Contains(' ') && Uri.TryCreate("http://" + value, UriKind.Absolute, out _));

            return Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    TextBox(_proxyDraft ?? config.General.HttpProxyAddr, v => _proxyDraft = v)
                        .Margin(new Thickness(0, 0, 8, 0))
                        .AutomationName("代理地址")
                        .Grid(row: 0, column: 0),
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
                    }).AutomationName("应用代理").Grid(row: 0, column: 1),
                ]
            );
        }

        // 状态反馈浮条
        Element? feedbackBanner = null;
        if (error is not null)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(Theme.Ref("SystemFillColorCriticalBrush")),
                    TextBlock($"保存失败: {error}").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).FontSize(12).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }
        else if (savedAt is { } time)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(palette.Accent),
                    TextBlock($"已保存 · {time:HH:mm:ss}").FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }

        // ══════════════════════════════════════════════════════
        // 设置页卡片装配（完全对齐工具箱设计规范）
        // ══════════════════════════════════════════════════════
        var lolPathStatus = config.General.LolPath.Count == 0 ? "未配置" : $"已设置 {config.General.LolPath.Count} 个路径";
        var lolPathCard = SettingExpandableCard("lolPath", "\uE7B8", "英雄联盟客户端路径", "设置英雄联盟客户端所在目录的路径", lolPathStatus,
            VStack(8,
                TextBlock(JoinPaths(config.General.LolPath)).Foreground(Theme.SecondaryText).TextWrapping(),
                TextBlock("启动时将自动检测并提取客户端锁文件凭据；支持配置多个安装目录").FontSize(12).Foreground(Theme.SecondaryText)));

        var wegamePathCard = ActionCard(
            "\uE7F4", "WeGame 路径", "关联的 WeGame 客户端程序安装目录",
            TextBlock(OrDash(config.General.WegamePath)).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));

        var clearCacheCard = ActionCard(
            "\uE74D", "清除缓存", "删除所有游戏资源（英雄图标/装备/符文）与 OP.GG 数据的磁盘缓存",
            Button("清除缓存", () =>
            {
                try
                {
                    services.AssetCache.Clear();
                    services.OpggHttpClient.ClearMemoryCache();
                    services.OpggHttpClient.ClearDiskCache();
                    logger.LogInformation("已清空游戏资源与 OP.GG 缓存");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "清除游戏资源缓存失败");
                }
            }).AutomationName("清除缓存按钮"));

        var hideMenuEnabled = config.Functions.HideTft || config.Functions.HideSavedPlayers;
        var hideMenuCard = SettingExpandableCard("hideMenu", "\uE712", "隐藏菜单", "控制侧边栏云顶之弈与路人集功能入口的显隐", hideMenuEnabled ? "已启用" : "未启用",
            VStack(4,
                CardRow("隐藏云顶之弈入口:",
                    ToggleSwitch(config.Functions.HideTft, v =>
                    {
                        if (v == config.Functions.HideTft) return;
                        config.Functions.HideTft = v;
                        // 先推给 AppShell（立即生效），再落盘；写盘失败时侧边栏也已更新，
                        // 与颜色设置「先应用主题后防抖写盘」的语义一致。
                        menu.Apply(config.Functions.HideTft, config.Functions.HideSavedPlayers);
                        Commit(nameof(FunctionsConfig.HideTft));
                    }, header: null, onContent: "", offContent: "").AutomationName("隐藏云顶之弈")),
                CardRow("隐藏路人集入口:",
                    ToggleSwitch(config.Functions.HideSavedPlayers, v =>
                    {
                        if (v == config.Functions.HideSavedPlayers) return;
                        config.Functions.HideSavedPlayers = v;
                        menu.Apply(config.Functions.HideTft, config.Functions.HideSavedPlayers);
                        Commit(nameof(FunctionsConfig.HideSavedPlayers));
                    }, header: null, onContent: "", offContent: "").AutomationName("隐藏路人集"))));

        var screenshotCard = SettingExpandableCard("screenshot", "\uE722", "多杀自动截图", "对局中达成三杀/四杀/五杀时自动保存精彩瞬间", config.Functions.EnableScreenshotOnMultikill ? "已启用" : "未启用",
            VStack(8,
                CardRow("启用多杀自动截图:",
                    ToggleSwitch(config.Functions.EnableScreenshotOnMultikill, v =>
                    {
                        if (v == config.Functions.EnableScreenshotOnMultikill) return;
                        config.Functions.EnableScreenshotOnMultikill = v;
                        Commit(nameof(FunctionsConfig.EnableScreenshotOnMultikill));
                    }, header: null, onContent: "", offContent: "").AutomationName("多杀截图开关")),
                CardRow("截图保存目录:",
                    TextBlock(OrDash(config.Functions.ScreenshotSavePath)).Foreground(Theme.SecondaryText))));

        var logLevelCard = ActionCard(
            "\uE7BA", "日志等级", "写入 <exe>/log 的日志记录详细程度，修改后立即生效",
            ComboBox(LogLevelLabels, IndexOf(LogLevelValues, config.General.LogLevel, LogLevelValues.Length - 1), index =>
            {
                var level = LogLevelValues[index];
                config.General.LogLevel = level;
                services.ApplyLogLevel(level);
                Commit(nameof(GeneralConfig.LogLevel));
            }).Width(150).AutomationName("日志等级选择"));

        var logDirCard = ActionCard(
            "\uE8B7", "日志文件", $"存放于 {services.Paths.LogDirectory}",
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

        var httpProxyCard = SettingExpandableCard("httpProxy", "\uE774", "软件 HTTP 代理", "软件更新、TFT 数据、OP.GG 服务访问走此 HTTP 代理通道", config.General.EnableHttpProxy ? "已启用" : "未启用",
            VStack(8,
                CardRow("启用 HTTP 代理:",
                    ToggleSwitch(config.General.EnableHttpProxy, v =>
                    {
                        if (v == config.General.EnableHttpProxy) return;
                        config.General.EnableHttpProxy = v;
                        Commit(nameof(GeneralConfig.EnableHttpProxy));
                    }, header: null, onContent: "", offContent: "").AutomationName("HTTP代理开关")),
                ProxyAddressRow()));

        var themeModeCard = ActionCard(
            "\uE708", "主题模式", "浅色 / 深色 / 跟随系统主题自适应切换",
            ComboBox(ThemeModeLabels, IndexOf(ThemeModeValues, config.Personalization.ThemeMode, ThemeModeValues.Length - 1), index =>
            {
                config.Personalization.ThemeMode = ThemeModeValues[index];
                theme.Apply(config.Personalization);
                Commit(nameof(PersonalizationConfig.ThemeMode));
            }).Width(140).AutomationName("主题模式选择"));

        var aboutCard = SettingExpandableCard("about", "\uE946", "关于 YuumiX", $"当前版本 v{config.Version} · 运行环境与存储位置", $"v{config.Version}",
            VStack(10,
                InfoRow("配置版本", $"v{config.Version}"),
                InfoRow("配置文件", services.ConfigStore.FilePath),
                InfoRow("数据目录", services.Paths.DataDirectory),
                InfoRow("便携模式", OnOff(services.Paths.IsPortable)),
                InfoRow("日志目录", services.Paths.LogDirectory),
                InfoRow("写回备份", $"{services.ConfigStore.ListBackups().Count} 份（保留最近 {ConfigStore.MaxBackups} 份）")));

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
                                TextBlock(iconGlyph)
                                    .FontFamily("Segoe Fluent Icons")
                                    .FontSize(11)
                                    .Foreground(iconColor)
                                    .HAlign(HorizontalAlignment.Center)
                                    .VAlign(VerticalAlignment.Center)
                            )
                            .Width(22)
                            .Height(22)
                            .CornerRadius(11)
                            .Background(Theme.Ref("LayerFillColorDefaultBrush")),

                            TextBlock(item.Message)
                                .FontSize(13)
                                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                                .VAlign(VerticalAlignment.Center)
                        )
                    )
                    .Padding(new Thickness(16, 9, 20, 9))
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

        var mainScroll = ScrollView(
            VStack(8,
                VStack(4,
                    Heading("设置"),
                    Caption("系统参数、OP.GG 辅助、客户端通用行为与外观个性化（对应原实现：src/views/Settings.vue）")
                ).Margin(new Thickness(0, 0, 0, 6)),

                // 1. 功能
                GroupHeader("功能"),
                SettingNumberCard("\uE713", "LCU API 并发数", "该值越大数据加载速度越快，但越可能引起客户端闪退", config.Functions.ApiConcurrencyNumber, ApiConcurrencyMin, ApiConcurrencyMax, v => config.Functions.ApiConcurrencyNumber = v, nameof(FunctionsConfig.ApiConcurrencyNumber), "重启后生效"),
                SettingNumberCard("\uE81E", "默认对局数量", "调整在个人生涯界面中显示的最大对局数量", config.Functions.CareerGamesNumber, CareerGamesMin, CareerGamesMax, v => config.Functions.CareerGamesNumber = v, nameof(FunctionsConfig.CareerGamesNumber)),
                SettingToggleCard("\uE71C", "对局信息过滤", "基于你当前游戏模式（地图/队列）筛选战绩，只显示相同模式的玩家历史数据", config.Functions.GameInfoFilter, v => config.Functions.GameInfoFilter = v, nameof(FunctionsConfig.GameInfoFilter)),
                SettingToggleCard("\uE7FC", "保留对局信息界面内容", "保留上一局的对局信息内容直到下一次对局开始", config.Functions.EnableReserveGameinfo, v => config.Functions.EnableReserveGameinfo = v, nameof(FunctionsConfig.EnableReserveGameinfo)),
                SettingToggleCard("\uE734", "对局详情中显示段位", "在搜索界面对局详情界面中显示段位，启动该选项将影响加载界面的速度", config.Functions.ShowTierInGameInfo, v => config.Functions.ShowTierInGameInfo = v, nameof(FunctionsConfig.ShowTierInGameInfo)),

                // 2. OP.GG
                GroupHeader("OP.GG"),
                SettingToggleCard("\uE737", "自动显示 OP.GG 窗口", "在英雄选择开始时自动显示 OP.GG 窗口", config.Functions.AutoShowOpgg, v => config.Functions.AutoShowOpgg = v, nameof(FunctionsConfig.AutoShowOpgg)),
                SettingToggleCard("\uE840", "置顶 OP.GG 窗口", "在英雄选择时将 OP.GG 窗口置顶", config.Functions.EnableOpggOnTop, v =>
                {
                    config.Functions.EnableOpggOnTop = v;
                    OpggWindowHost.UpdateAlwaysOnTop(v);
                }, nameof(FunctionsConfig.EnableOpggOnTop)),

                // 3. 通用设置
                GroupHeader("通用设置"),
                lolPathCard,
                wegamePathCard,
                SettingToggleCard("\uE768", "随应用启动客户端", "启动 YuumiX 时一并拉起英雄联盟客户端（含 WeGame 形态）", config.General.EnableStartLolWithApp, v => config.General.EnableStartLolWithApp = v, nameof(GeneralConfig.EnableStartLolWithApp)),
                ActionCard("\uE711", "关闭到托盘", "关闭窗口时最小化到托盘而不是退出；未设置时按平台默认处理",
                    ThreeStateCheckBox(config.General.EnableCloseToTray, v =>
                    {
                        config.General.EnableCloseToTray = v;
                        Commit(nameof(GeneralConfig.EnableCloseToTray));
                    }).AutomationName("关闭到托盘开关")),
                SettingToggleCard("\uE740", "游戏开始时最小化", "检测到对局开始后自动最小化主窗口", config.General.EnableGameStartMinimize, v => config.General.EnableGameStartMinimize = v, nameof(GeneralConfig.EnableGameStartMinimize)),
                clearCacheCard,
                SettingToggleCard("\uE895", "检查更新", "启动时检查新版本（便携版走整包覆盖）", config.General.EnableCheckUpdate, v => config.General.EnableCheckUpdate = v, nameof(GeneralConfig.EnableCheckUpdate)),

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
                SettingToggleCard("\uE790", "Mica 背景", "使用系统亚克力/云母材质作为窗口背景", config.Personalization.MicaEnabled, v =>
                {
                    config.Personalization.MicaEnabled = v;
                    theme.Apply(config.Personalization);
                }, nameof(PersonalizationConfig.MicaEnabled), "即时生效"),
                themeModeCard,
                ThemeColorEditor(),
                CardColorEditor(),
                DeathColorEditor(),

                // 7. 关于
                GroupHeader("关于"),
                aboutCard
            ).Padding(24).HAlign(HorizontalAlignment.Stretch)
        );

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                mainScroll.Grid(row: 0, column: 0),
                RenderToasts().Grid(row: 0, column: 0),
            ]
        );
    }

    /// <summary>
    /// 保存状态条：未改动 / 已保存（含备份文件名） / 保存失败。
    /// 三态而非布尔——把失败显示成「已保存」比不显示状态更糟（ADR-12）。
    /// </summary>
    private static Element SaveStatusBanner(DateTime? savedAt, string? backupName, string? error, string accent)
    {
        Element body;
        if (error is not null)
        {
            body = VStack(4,
                TextBlock("保存失败，配置未写入").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .Foreground(Theme.Ref("SystemFillColorCriticalBrush")),
                Caption(error));
        }
        else if (savedAt is { } time)
        {
            body = VStack(4,
                TextBlock($"已保存 · {time:HH:mm:ss}").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                Caption(backupName is null
                    ? "本次启动已完成首次备份，后续写回不再重复备份。"
                    : $"写回前已备份到 {backupName}"));
        }
        else
        {
            body = VStack(4,
                TextBlock("修改即写回").FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                Caption($"改动会立即写入 config.json；本次启动的首次写回前自动备份，保留最近 {ConfigStore.MaxBackups} 份。"));
        }

        // 左侧竖条取主题色：这是「主题色改动即时反映到界面」的最小可视化证据，
        // 其余业务组件后续同样从 ThemeState 取强调色（ADR-12 决策三）。
        return Border(
            HStack(12,
                Border(Empty()).Width(3).Height(34).CornerRadius(2).Background(accent),
                body))
            .Padding(14)
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    /// <summary>三态开关（<c>bool?</c>）：<c>null</c> 表示老配置未表态，必须与「关」区分开。</summary>
    private Element TriStateRow(string label, string description, bool? value, Action<bool?> apply) =>
        SettingRow(
            label,
            description,
            ThreeStateCheckBox(value, v =>
            {
                if (v == value)
                {
                    return;
                }

                apply(v);
            }).AutomationName(label));

    private static Element SettingRow(string label, string description, Element control) =>
        HStack(16,
            VStack(4,
                TextBlock(label),
                Caption(description)).Width(320),
            control);

    /// <summary>字段生效时机的标记，只出现在真正即时生效或需要重启的字段上。</summary>
    private static Element NoteChip(string text) =>
        Caption($"· {text}").Foreground(Theme.SecondaryText);

    /// <summary>尚未接编辑的字段：明确写出原因，避免被当成缺陷。</summary>
    private static Element ReadOnlyRow(string label, string value, Element? trailing = null) =>
        HStack(16,
            VStack(4,
                TextBlock(label),
                Caption("暂不可编辑")).Width(320),
            HStack(12,
                trailing ?? Empty(),
                TextBlock(value).Foreground(Theme.SecondaryText).TextWrapping()));

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

    private static Element Section(string title, string note, Element content) =>
        Border(
            VStack(12,
                VStack(4,
                    SubHeading(title),
                    Caption(note)),
                content)
        ).Padding(18)
         .CornerRadius(8)
         .Background(Theme.CardBackground)
         .WithBorder(Theme.CardStroke, 1);

    private static string OnOff(bool value) => value ? "已开启" : "已关闭";

    private static string JoinPaths(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? "未配置" : string.Join(" ; ", paths);

    private static string OrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "未配置" : value;

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
