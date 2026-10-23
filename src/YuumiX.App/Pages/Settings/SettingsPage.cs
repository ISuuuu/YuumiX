using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
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
    private const int CareerGamesMax = 200;
    private const int ApiConcurrencyMin = 1;
    private const int ApiConcurrencyMax = 32;

    /// <summary>
    /// 取色器的合并写盘延迟。ColorPicker 的 ColorChanged 在拖动时每帧触发，
    /// 逐次同步写盘既打断拖动又没有信息增量；停手 400 ms 后写一次就够了。
    /// </summary>
    private const int ColorCommitDelayMs = 400;

    /// <summary>
    /// 渲染序号。用于在「改了 POCO 但 Reactor 不知道」时强制重渲染。
    /// 用实例字段而非读取 <c>UseState</c> 的当前值再加一：事件回调里捕获的旧值可能是过期的。
    /// </summary>
    private int _renderTick;

    /// <summary>颜色合并写盘的序号，见 <see cref="ColorCommitDelayMs"/>。</summary>
    private int _colorTick;

    /// <summary>待写回的颜色字段名（连续取色只写最后一次）。</summary>
    private string? _pendingColorField;

    /// <summary>代理地址草稿：只在点「应用」时读它，不参与渲染，避免受控文本框的光标跳动。</summary>
    private string? _proxyDraft;

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;

    private ILogger<SettingsPage>? _logger;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var config = services.Config;

        // threadSafe: true —— 保存与重渲染的调用点虽在 UI 线程，但保留统一语义，
        // 以免将来把写回挪到线程池时漏改（ADR-07 的成本是崩溃，不是降级）。
        var (_, setTick) = UseState(0, threadSafe: true);
        var (savedAt, setSavedAt) = UseState<DateTime?>(null, threadSafe: true);
        var (backupName, setBackupName) = UseState<string?>(null, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);

        // 本页自身也订阅主题状态：强调色变化时要跟着重渲染，否则状态条这类
        // 消费 palette.Accent 的元素会停在旧颜色上（主题模式由 AppShell 统一施加，不在此列）。
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);

        // 颜色合并写盘用的序号；每次取色变化 +1，被防抖 effect 消费。
        var (colorTick, setColorTick) = UseState(0, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        var logger = _logger ??= services.LoggerFactory.CreateLogger<SettingsPage>();

        void Rerender() => setTick(++_renderTick);

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

                logger.LogInformation("设置已写回: {Field}", field);
            }
            catch (Exception ex)
            {
                // 写失败必须让用户看见：静默吞掉会让人以为设置已保存（ConfigStore.Save 的约定）
                setError(ex.Message);
                setSavedAt(null);
                logger.LogError(ex, "设置写回失败: {Field}", field);
            }

            Rerender();
        }

        Element BoolRow(string label, string description, bool value, Action<bool> apply, string field, string? note = null) =>
            SettingRow(
                label,
                description,
                HStack(12,
                    note is null ? Empty() : NoteChip(note),
                    ToggleSwitch(value, v =>
                    {
                        if (v == value)
                        {
                            return;
                        }

                        apply(v);
                        Commit(field);
                    }, header: null, onContent: "", offContent: "").AutomationName(label)));

        Element EnumRow(string label, string description, string[] options, int selectedIndex, Action<int> apply, string field, string? note = null) =>
            SettingRow(
                label,
                description,
                HStack(12,
                    note is null ? Empty() : NoteChip(note),
                    ComboBox(options, selectedIndex, index =>
                    {
                        if (index == selectedIndex)
                        {
                            return;
                        }

                        apply(index);
                        Commit(field);
                    }).Width(160).AutomationName(label)));

        Element NumberRow(string label, string description, int value, int min, int max, Action<int> apply, string field, string? note = null) =>
            SettingRow(
                label,
                description,
                HStack(12,
                    note is null ? Empty() : NoteChip(note),
                    NumberBox(value, v =>
                    {
                        // 清空输入框时 NumberBox 会给出 NaN；NaN 与整数比较恒为 false，
                        // 若不拦截，(int)NaN 会落到 0 并被写回配置。
                        if (double.IsNaN(v))
                        {
                            return;
                        }

                        // NumberBoxElement 没有 Minimum/Maximum 修饰符，范围只能在这里夹紧
                        var next = (int)Math.Clamp(v, min, max);
                        if (next == value)
                        {
                            return;
                        }

                        apply(next);
                        Commit(field);
                    }).Width(140).AutomationName(label)));

        // 颜色改动统一出口：先落到内存配置与主题状态（界面即时生效），再由防抖 effect 落盘。
        void ApplyColor(string field, string value, Action<string> assign)
        {
            assign(value);
            theme.Apply(config.Personalization);
            RequestColorCommit(field);
        }

        // 单个颜色编辑块：色块预览 + 当前值 + 原生取色器。
        // 取色器的值来自配置，自己不留状态；拖动时配置同步变化，因此不会出现「回头跳色」。
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

            // 类型特定修饰符要在 Width/AutomationName 这类泛型修饰符之前（见 architecture.md 已知坑）
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
            Expander("主题色", VStack(14,
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
            Expander("战绩卡片配色", VStack(16,
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
            Expander("死亡数字配色", VStack(16,
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

        // 代理地址是文本输入：草稿只存在字段里（不触发渲染），点「应用」才写回，
        // 避免逐字符写盘，也避免受控文本框在重渲染时丢光标位置。
        Element ProxyAddressRow()
        {
            static bool LooksValid(string value) =>
                value.Length == 0
                || (value.Contains(':') && !value.Contains(' ') && Uri.TryCreate("http://" + value, UriKind.Absolute, out _));

            return SettingRow(
                "代理地址",
                "形如 127.0.0.1:7897；留空表示用系统默认。改了要重启才生效",
                HStack(12,
                    TextBox(_proxyDraft ?? config.General.HttpProxyAddr, v => _proxyDraft = v)
                        .Width(180)
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
                    })));
        }

        return ScrollView(
            VStack(20,
                VStack(4,
                    Heading("设置"),
                    Caption("对应原实现：src/views/Settings.vue")),

                SaveStatusBanner(savedAt, backupName, error, palette.Accent),

                Section("常规",
                    "客户端路径、启动行为与日志",
                    VStack(12,
                        ReadOnlyRow("英雄联盟路径", JoinPaths(config.General.LolPath)),
                        ReadOnlyRow("WeGame 路径", OrDash(config.General.WegamePath)),
                        BoolRow(
                            "随应用启动客户端",
                            "启动 YuumiX 时一并拉起英雄联盟客户端（含 WeGame 形态）",
                            config.General.EnableStartLolWithApp,
                            v => config.General.EnableStartLolWithApp = v,
                            nameof(GeneralConfig.EnableStartLolWithApp)),
                        TriStateRow(
                            "关闭到托盘",
                            "关闭窗口时最小化到托盘而不是退出；未设置时按平台默认处理",
                            config.General.EnableCloseToTray,
                            v =>
                            {
                                config.General.EnableCloseToTray = v;
                                Commit(nameof(GeneralConfig.EnableCloseToTray));
                            }),
                        BoolRow(
                            "游戏开始时最小化",
                            "检测到对局开始后自动最小化主窗口",
                            config.General.EnableGameStartMinimize,
                            v => config.General.EnableGameStartMinimize = v,
                            nameof(GeneralConfig.EnableGameStartMinimize)),
                        BoolRow(
                            "检查更新",
                            "启动时检查新版本（便携版走整包覆盖）",
                            config.General.EnableCheckUpdate,
                            v => config.General.EnableCheckUpdate = v,
                            nameof(GeneralConfig.EnableCheckUpdate)),
                        EnumRow(
                            "日志等级",
                            "写入 <exe>/log 的详细程度，改完立即生效",
                            LogLevelLabels,
                            IndexOf(LogLevelValues, config.General.LogLevel, LogLevelValues.Length - 1),
                            index =>
                            {
                                var level = LogLevelValues[index];
                                config.General.LogLevel = level;
                                services.ApplyLogLevel(level);
                            },
                            nameof(GeneralConfig.LogLevel),
                            "即时生效"),
                        BoolRow(
                            "HTTP 代理",
                            $"当前地址 {config.General.HttpProxyAddr}",
                            config.General.EnableHttpProxy,
                            v => config.General.EnableHttpProxy = v,
                            nameof(GeneralConfig.EnableHttpProxy),
                            "重启后生效"),
                        ProxyAddressRow())),

                Section("个性化",
                    "主题、语言与战绩卡片配色",
                    VStack(12,
                        BoolRow(
                            "Mica 背景",
                            "使用系统亚克力/云母材质作为窗口背景",
                            config.Personalization.MicaEnabled,
                            v =>
                            {
                                config.Personalization.MicaEnabled = v;
                                theme.Apply(config.Personalization);
                            },
                            nameof(PersonalizationConfig.MicaEnabled),
                            "即时生效"),
                        EnumRow(
                            "主题模式",
                            "浅色 / 深色 / 跟随系统",
                            ThemeModeLabels,
                            IndexOf(ThemeModeValues, config.Personalization.ThemeMode, ThemeModeValues.Length - 1),
                            index =>
                            {
                                config.Personalization.ThemeMode = ThemeModeValues[index];
                                theme.Apply(config.Personalization);
                            },
                            nameof(PersonalizationConfig.ThemeMode),
                            "即时生效"),
                        ReadOnlyRow(
                            "语言",
                            $"{config.Personalization.Language}（本地化资源尚未接入）"),
                        ReadOnlyRow(
                            "缩放",
                            $"{config.Personalization.DpiScale}（窗口级缩放随独立窗口一起做）"),
                        ThemeColorEditor(),
                        CardColorEditor(),
                        DeathColorEditor())),

                Section("功能开关",
                    "自动化与数据抓取的关键参数",
                    VStack(12,
                        NumberRow(
                            "生涯战绩场数",
                            $"生涯页默认拉取的场数（{CareerGamesMin}–{CareerGamesMax}）",
                            config.Functions.CareerGamesNumber,
                            CareerGamesMin,
                            CareerGamesMax,
                            v => config.Functions.CareerGamesNumber = v,
                            nameof(FunctionsConfig.CareerGamesNumber)),
                        NumberRow(
                            "LCU 接口并发",
                            $"同时发起的 LCU 请求上限（{ApiConcurrencyMin}–{ApiConcurrencyMax}）",
                            config.Functions.ApiConcurrencyNumber,
                            ApiConcurrencyMin,
                            ApiConcurrencyMax,
                            v => config.Functions.ApiConcurrencyNumber = v,
                            nameof(FunctionsConfig.ApiConcurrencyNumber),
                            "重启后生效"),                        BoolRow(
                            "自动接受对局",
                            "匹配到对局后自动确认",
                            config.Functions.EnableAutoAcceptMatching,
                            v => config.Functions.EnableAutoAcceptMatching = v,
                            nameof(FunctionsConfig.EnableAutoAcceptMatching)),
                        BoolRow(
                            "自动重连",
                            "掉线后自动重连回对局",
                            config.Functions.EnableAutoReconnect,
                            v => config.Functions.EnableAutoReconnect = v,
                            nameof(FunctionsConfig.EnableAutoReconnect)),
                        BoolRow(
                            "自动选人",
                            "按分路偏好自动选择英雄（不含锁定）",
                            config.Functions.EnableAutoSelectChampion,
                            v => config.Functions.EnableAutoSelectChampion = v,
                            nameof(FunctionsConfig.EnableAutoSelectChampion)),
                        BoolRow(
                            "自动禁用",
                            "按分路偏好自动禁用英雄",
                            config.Functions.EnableAutoBanChampion,
                            v => config.Functions.EnableAutoBanChampion = v,
                            nameof(FunctionsConfig.EnableAutoBanChampion)),
                        BoolRow(
                            "大乱斗悬浮窗",
                            "极地大乱斗板凳席悬浮窗",
                            config.Functions.EnableBenchOverlay,
                            v => config.Functions.EnableBenchOverlay = v,
                            nameof(FunctionsConfig.EnableBenchOverlay)),
                        ReadOnlyRow("截图保存目录", OrDash(config.Functions.ScreenshotSavePath)))),

                Section("关于",
                    "运行环境与数据位置",
                    VStack(12,
                        InfoRow("配置版本", $"v{config.Version}"),
                        InfoRow("配置文件", services.ConfigStore.FilePath),
                        InfoRow("数据目录", services.Paths.DataDirectory),
                        InfoRow("便携模式", OnOff(services.Paths.IsPortable)),
                        InfoRow("日志目录", services.Paths.LogDirectory),
                        InfoRow("写回备份", $"{services.ConfigStore.ListBackups().Count} 份（保留最近 {ConfigStore.MaxBackups} 份）")))
            ).Padding(28).HAlign(HorizontalAlignment.Stretch)
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
