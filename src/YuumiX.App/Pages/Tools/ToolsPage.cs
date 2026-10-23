using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 工具箱页（完整对齐原实现：src/views/Tools.vue 与 quickactions 子模块）。
///
/// 模块结构：
/// 1. 英雄选择组（自动接受对局、自动交换请求、自动选用/预选、自动禁用、自动召唤师技能）
/// 2. 游戏组（自动游戏流程、观战、好友清理、大乱斗板凳席、自动重连、自动创建大厅、游戏设置锁定）
/// 3. 客户端组（客户端缩放修复、快速重启客户端）
/// 4. 个人主页组（个性签名、生涯背景、段位伪装、在线状态、挑战勋章与边框）
/// </summary>
public sealed class ToolsPage : Component
{
    private static readonly string[] SpoofTiers =
    [
        "CHALLENGER", "GRANDMASTER", "MASTER", "DIAMOND", "EMERALD",
        "PLATINUM", "GOLD", "SILVER", "BRONZE", "IRON", "UNRANKED"
    ];
    private static readonly string[] SpoofTierLabels =
    [
        "王者 (Challenger)", "宗师 (Grandmaster)", "大师 (Master)", "钻石 (Diamond)",
        "翡翠 (Emerald)", "铂金 (Platinum)", "黄金 (Gold)", "白银 (Silver)", "青铜 (Bronze)", "黑铁 (Iron)", "未定级 (Unranked)"
    ];
    private static readonly string[] SpoofDivisions = ["I", "II", "III", "IV"];
    private static readonly string[] SpoofDivisionLabels = ["第 1 阶 (I)", "第 2 阶 (II)", "第 3 阶 (III)", "第 4 阶 (IV)"];
    private static readonly string[] SpoofQueues = ["RANKED_SOLO_5x5", "RANKED_FLEX_SR", "RANKED_TFT"];
    private static readonly string[] SpoofQueueLabels = ["单双排排位", "灵活排位", "云顶之弈"];

    private static readonly string[] SpectateMethodLabels =
    [
        "CMD 方式 (直接启动客户端进程，绕开限制)",
        "LCU 方式 (客户端内置观战)"
    ];

    private static readonly int[] LobbyModes = [2400, 2450, 450, 430, 420, 440, 900, 1020, 1300, 1700, 4300];
    private static readonly string[] LobbyModeLabels =
    [
        "海克斯大乱斗 (2400)", "经典海斗 (2450)", "极地大乱斗 (450)", "匹配模式 (430)",
        "单双排位 (420)", "灵活排位 (440)", "无限火力 (900)", "克隆模式 (1020)",
        "极限闪击 (1300)", "斗魂竞技场 (1700)", "经典模式 (4300)"
    ];

    private static int IndexOfLobbyMode(int mode)
    {
        for (var i = 0; i < LobbyModes.Length; i++)
        {
            if (LobbyModes[i] == mode) return i;
        }
        return 0;
    }

    private static long _nextToastId;
    private int _renderTick;
    private ILogger<ToolsPage>? _logger;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly List<Timer> _toastTimers = [];
    private readonly List<ToastItem> _activeToasts = [];

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var config = services.Config;
        var toolsService = services.Services.GetRequiredService<IToolsService>();
        var friendCleanerService = services.Services.GetRequiredService<IFriendCleanerService>();

        var (_, setTick) = UseState(0, threadSafe: true);
        var (savedAt, setSavedAt) = UseState<DateTime?>(null, threadSafe: true);
        var (error, setError) = UseState<string?>(null, threadSafe: true);
        var (actionFeedback, setActionFeedback) = UseState<string?>(null, threadSafe: true);
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);

        // 多 Toast 堆叠队列（对齐旧系统 Naive UI n-message-provider :max="5"）
        var initialToasts = UseMemo(() => (IReadOnlyList<ToastItem>)Array.Empty<ToastItem>(), 0);
        var (toasts, setToasts) = UseState(initialToasts, threadSafe: true);

        // 记录展开的卡片 key 集合（默认全部收起，对齐原版设计）
        var initialExpanded = UseMemo(() => new HashSet<string>(), 0);
        var (expandedKeys, setExpandedKeys) = UseState(initialExpanded, threadSafe: true);

        // 动作输入草稿
        var (spectateName, setSpectateName) = UseState(string.Empty, threadSafe: true);
        var (spectateMethodIndex, setSpectateMethodIndex) = UseState(0, threadSafe: true);
        var (signatureDraft, setSignatureDraft) = UseState(string.Empty, threadSafe: true);
        var (skinIdDraft, setSkinIdDraft) = UseState(string.Empty, threadSafe: true);
        var (spoofTierIndex, setSpoofTierIndex) = UseState(0, threadSafe: true);
        var (spoofQueueIndex, setSpoofQueueIndex) = UseState(0, threadSafe: true);
        var (spoofDivisionIndex, setSpoofDivisionIndex) = UseState(0, threadSafe: true);

        // 好友清理状态
        var (allFriends, setAllFriends) = UseState<IReadOnlyList<FriendEntry>>(Array.Empty<FriendEntry>(), threadSafe: true);
        var (isScanningFriends, setIsScanningFriends) = UseState(false, threadSafe: true);
        var (isDeletingFriends, setIsDeletingFriends) = UseState(false, threadSafe: true);
        var (friendScanCompleted, setFriendScanCompleted) = UseState(0, threadSafe: true);
        var (friendScanTotal, setFriendScanTotal) = UseState(0, threadSafe: true);
        var (friendSearchQuery, setFriendSearchQuery) = UseState(string.Empty, threadSafe: true);
        var (friendFilterIndex, setFriendFilterIndex) = UseState(0, threadSafe: true);
        var initialSelectedFriends = UseMemo(() => new HashSet<string>(), 0);
        var (selectedFriendIds, setSelectedFriendIds) = UseState(initialSelectedFriends, threadSafe: true);
        var (isConfirmingDeleteFriends, setIsConfirmingDeleteFriends) = UseState(false, threadSafe: true);

        // 生涯背景皮肤选择器
        var (bgChampInput, setBgChampInput) = UseState("1", threadSafe: true);
        var (bgSkins, setBgSkins) = UseState<IReadOnlyList<ChampionSkinItem>>(Array.Empty<ChampionSkinItem>(), threadSafe: true);
        var (bgSkinIndex, setBgSkinIndex) = UseState(0, threadSafe: true);
        var (bgLoading, setBgLoading) = UseState(false, threadSafe: true);

        // 挑战勋章与称号
        var (challengesList, setChallengesList) = UseState<IReadOnlyList<ChallengeItem>>(Array.Empty<ChallengeItem>(), threadSafe: true);
        var (challengesLoading, setChallengesLoading) = UseState(false, threadSafe: true);
        var (badgeSearchQuery, setBadgeSearchQuery) = UseState(string.Empty, threadSafe: true);
        var (badgeTierIndex, setBadgeTierIndex) = UseState(0, threadSafe: true);
        var (badgeTitleInput, setBadgeTitleInput) = UseState(string.Empty, threadSafe: true);
        var initialSelectedBadges = UseMemo(() => new List<long>(), 0);
        var (selectedBadgeIds, setSelectedBadgeIds) = UseState(initialSelectedBadges, threadSafe: true);

        // 游戏设置锁定状态
        var (isSettingsLocked, setIsSettingsLocked) = UseState<bool?>(null, threadSafe: true);

        var lolPaths = config.General.LolPath;

        if (isSettingsLocked is null)
        {
            try
            {
                var locked = toolsService.GetGameSettingsReadonly(lolPaths);
                setIsSettingsLocked(locked);
            }
            catch
            {
                setIsSettingsLocked(false);
            }
        }

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

        var logger = _logger ??= services.LoggerFactory.CreateLogger<ToolsPage>();

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

        void Commit(string field)
        {
            try
            {
                services.ConfigStore.SaveWithBackup(config);
                setError(null);
                setSavedAt(DateTime.Now);
                ShowToast("设置已自动保存");
                logger.LogInformation("功能配置已写回: {Field}", field);
            }
            catch (Exception ex)
            {
                setError(ex.Message);
                setSavedAt(null);
                ShowToast($"设置保存失败: {ex.Message}", isError: true);
                logger.LogError(ex, "功能配置写回失败: {Field}", field);
            }

            Rerender();
        }

        async void SendLcu(Func<ILcuTransport, Task> action, string successMessage)
        {
            try
            {
                var transport = services.Services.GetService<LcuConnection>()?.Transport;
                if (transport is null)
                {
                    ShowToast("LCU 客户端未连接，请先启动英雄联盟客户端", isError: true);
                    Rerender();
                    return;
                }

                await action(transport);
                ShowToast(successMessage);
                logger.LogInformation("LCU 操作成功: {Message}", successMessage);
            }
            catch (Exception ex)
            {
                ShowToast($"操作失败: {ex.Message}", isError: true);
                logger.LogError(ex, "LCU 操作异常");
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

        // 单行直接操作卡片（不折叠，右侧直接展示操作按钮或状态开关）
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

        Element ToolCard(
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
                    // 左侧圆角图标徽章
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

                    // 中间标题与说明
                    VStack(2,
                        TextBlock(title)
                            .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                            .FontSize(14),
                        TextBlock(description)
                            .FontSize(12)
                            .Foreground(Theme.SecondaryText)
                    ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1),

                    // 右侧状态摘要
                    TextBlock(statusText)
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Margin(new Thickness(12, 0, 10, 0))
                        .Grid(row: 0, column: 2),

                    // 展开指示器（下箭头 / 右箭头）
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

        Element GroupHeader(string title) =>
            TextBlock(title)
                .FontSize(12)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground(Theme.SecondaryText)
                .Margin(new Thickness(4, 12, 0, 4));

        // 1. 自动接受对局卡片
        var autoAcceptStatus = config.Functions.EnableAutoAcceptMatching
            ? $"已启用，延迟: {config.Functions.AutoAcceptMatchingDelay} 秒"
            : "未启用";

        var autoAcceptCard = ToolCard(
            "autoAccept",
            "\uE73E",
            "自动接受对局",
            "在你设置的秒数之后自动接受对局匹配",
            autoAcceptStatus,
            VStack(4,
                CardRow("在对局找到后接受对局前延迟的秒数:",
                    NumberBox(config.Functions.AutoAcceptMatchingDelay, v =>
                    {
                        if (double.IsNaN(v)) return;
                        var next = (int)Math.Clamp(v, 0, 11);
                        if (next == config.Functions.AutoAcceptMatchingDelay) return;
                        config.Functions.AutoAcceptMatchingDelay = next;
                        Commit(nameof(FunctionsConfig.AutoAcceptMatchingDelay));
                    }).Width(120).AutomationName("自动接受延迟")),
                CardRow("启用自动接受对局:",
                    ToggleSwitch(config.Functions.EnableAutoAcceptMatching, v =>
                    {
                        if (v == config.Functions.EnableAutoAcceptMatching) return;
                        config.Functions.EnableAutoAcceptMatching = v;
                        Commit(nameof(FunctionsConfig.EnableAutoAcceptMatching));
                    }, header: null, onContent: "", offContent: "").AutomationName("启用自动接受对局"))
            ));

        // 2. 自动接受交换请求卡片
        var autoSwapEnabled = config.Functions.AutoAcceptCeilSwap || config.Functions.AutoAcceptChampTrade;
        var autoSwapStatus = autoSwapEnabled ? "已启用" : "未启用";

        var autoSwapCard = ToolCard(
            "autoSwap",
            "\uE8D4",
            "自动接受交换请求",
            "自动接受队友的交换楼层或英雄的请求",
            autoSwapStatus,
            VStack(4,
                CardRow("自动接受楼层交换请求:",
                    ToggleSwitch(config.Functions.AutoAcceptCeilSwap, v =>
                    {
                        if (v == config.Functions.AutoAcceptCeilSwap) return;
                        config.Functions.AutoAcceptCeilSwap = v;
                        Commit(nameof(FunctionsConfig.AutoAcceptCeilSwap));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动接受楼层交换")),
                CardRow("自动接受英雄交换请求:",
                    ToggleSwitch(config.Functions.AutoAcceptChampTrade, v =>
                    {
                        if (v == config.Functions.AutoAcceptChampTrade) return;
                        config.Functions.AutoAcceptChampTrade = v;
                        Commit(nameof(FunctionsConfig.AutoAcceptChampTrade));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动接受英雄交换"))
            ));

        // 3. 自动选用/预选英雄卡片
        var autoHoverEnabled = config.Functions.EnableAutoSelectChampion || config.Functions.EnableAutoHoverChampion;
        var autoHoverStatus = autoHoverEnabled ? "已启用" : "未启用";

        var autoHoverCard = ToolCard(
            "autoHover",
            "\uE739",
            "自动选用/预选英雄",
            "在你进入英雄选择时自动亮起/预选英雄",
            autoHoverStatus,
            VStack(4,
                CardRow("启用自动亮起/预选:",
                    ToggleSwitch(config.Functions.EnableAutoHoverChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoHoverChampion) return;
                        config.Functions.EnableAutoHoverChampion = v;
                        Commit(nameof(FunctionsConfig.EnableAutoHoverChampion));
                    }, header: null, onContent: "", offContent: "").AutomationName("启用自动预选")),
                CardRow("在结束时确认选择 (防秒退):",
                    ToggleSwitch(config.Functions.AutoSelectConfirmOnTimeout, v =>
                    {
                        if (v == config.Functions.AutoSelectConfirmOnTimeout) return;
                        config.Functions.AutoSelectConfirmOnTimeout = v;
                        Commit(nameof(FunctionsConfig.AutoSelectConfirmOnTimeout));
                    }, header: null, onContent: "", offContent: "").AutomationName("超时自动确认")),
                CardRow("选人阶段自动选择英雄:",
                    ToggleSwitch(config.Functions.EnableAutoSelectChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoSelectChampion) return;
                        config.Functions.EnableAutoSelectChampion = v;
                        Commit(nameof(FunctionsConfig.EnableAutoSelectChampion));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动选择英雄")),
                CardRow("在对局信息中显示秒退按钮:",
                    ToggleSwitch(config.Functions.EnableDodgeInGameInfo, v =>
                    {
                        if (v == config.Functions.EnableDodgeInGameInfo) return;
                        config.Functions.EnableDodgeInGameInfo = v;
                        Commit(nameof(FunctionsConfig.EnableDodgeInGameInfo));
                    }, header: null, onContent: "", offContent: "").AutomationName("对局信息秒退按钮"))
            ));

        // 4. 自动禁用英雄卡片
        var autoBanStatus = config.Functions.EnableAutoBanChampion ? "已启用" : "未启用";

        var autoBanCard = ToolCard(
            "autoBan",
            "\uE711",
            "自动禁用英雄",
            "在你的禁用环节开始时自动禁用英雄",
            autoBanStatus,
            VStack(4,
                CardRow("禁用环节自动禁用设定英雄:",
                    ToggleSwitch(config.Functions.EnableAutoBanChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoBanChampion) return;
                        config.Functions.EnableAutoBanChampion = v;
                        Commit(nameof(FunctionsConfig.EnableAutoBanChampion));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动禁用开关")),
                CardRow("避开队友已预选的英雄 (假禁用):",
                    ToggleSwitch(config.Functions.PretendBan, v =>
                    {
                        if (v == config.Functions.PretendBan) return;
                        config.Functions.PretendBan = v;
                        Commit(nameof(FunctionsConfig.PretendBan));
                    }, header: null, onContent: "", offContent: "").AutomationName("避开队友预选")),
                CardRow("自动禁用前等待延迟 (秒):",
                    NumberBox(config.Functions.AutoBanDelay, v =>
                    {
                        if (double.IsNaN(v)) return;
                        var next = Math.Clamp(v, 0, 10);
                        if (Math.Abs(next - config.Functions.AutoBanDelay) < 0.01) return;
                        config.Functions.AutoBanDelay = next;
                        Commit(nameof(FunctionsConfig.AutoBanDelay));
                    }).Width(120).AutomationName("自动禁用延迟"))
            ));

        // 5. 自动设置召唤师技能卡片
        var autoSpellsStatus = config.Functions.EnableAutoSetSpells ? "已启用" : "未启用";

        var autoSpellsCard = ToolCard(
            "autoSpells",
            "\uE74C",
            "自动设置召唤师技能",
            "当你的英雄选择开始时自动设置召唤师技能",
            autoSpellsStatus,
            VStack(4,
                CardRow("锁定英雄后自动写入配置好的技能组:",
                    ToggleSwitch(config.Functions.EnableAutoSetSpells, v =>
                    {
                        if (v == config.Functions.EnableAutoSetSpells) return;
                        config.Functions.EnableAutoSetSpells = v;
                        Commit(nameof(FunctionsConfig.EnableAutoSetSpells));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动召唤师技能"))
            ));

        // 6. 自动游戏流程卡片
        var autoFlowEnabled = config.Functions.EnableAutoHandleInvite
            || config.Functions.EnableAutoHonor
            || config.Functions.EnableAutoPlayAgain
            || config.Functions.EnableAutoAramTeamSide
            || config.Functions.EnableAutoTagReminder;
        var autoFlowStatus = autoFlowEnabled ? "已启用" : "未启用";

        var autoGameflowCard = ToolCard(
            "autoGameflow",
            "\uE768",
            "自动游戏流程",
            "自动处理邀请、点赞、再来一局与 ARAM 报边",
            autoFlowStatus,
            VStack(4,
                CardRow("自动接受好友邀请:",
                    ToggleSwitch(config.Functions.EnableAutoHandleInvite, v =>
                    {
                        if (v == config.Functions.EnableAutoHandleInvite) return;
                        config.Functions.EnableAutoHandleInvite = v;
                        Commit(nameof(FunctionsConfig.EnableAutoHandleInvite));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动接受邀请")),
                CardRow("对局结束后自动点赞:",
                    ToggleSwitch(config.Functions.EnableAutoHonor, v =>
                    {
                        if (v == config.Functions.EnableAutoHonor) return;
                        config.Functions.EnableAutoHonor = v;
                        Commit(nameof(FunctionsConfig.EnableAutoHonor));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动点赞")),
                CardRow("对局结束后自动再来一局:",
                    ToggleSwitch(config.Functions.EnableAutoPlayAgain, v =>
                    {
                        if (v == config.Functions.EnableAutoPlayAgain) return;
                        config.Functions.EnableAutoPlayAgain = v;
                        Commit(nameof(FunctionsConfig.EnableAutoPlayAgain));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动再来一局")),
                CardRow("ARAM 选人时自动报边:",
                    ToggleSwitch(config.Functions.EnableAutoAramTeamSide, v =>
                    {
                        if (v == config.Functions.EnableAutoAramTeamSide) return;
                        config.Functions.EnableAutoAramTeamSide = v;
                        Commit(nameof(FunctionsConfig.EnableAutoAramTeamSide));
                    }, header: null, onContent: "", offContent: "").AutomationName("ARAM报边")),
                CardRow("报边消息对队友可见:",
                    ToggleSwitch(config.Functions.AramTeamSideVisibleToTeam, v =>
                    {
                        if (v == config.Functions.AramTeamSideVisibleToTeam) return;
                        config.Functions.AramTeamSideVisibleToTeam = v;
                        Commit(nameof(FunctionsConfig.AramTeamSideVisibleToTeam));
                    }, header: null, onContent: "", offContent: "").AutomationName("报边队友可见")),
                CardRow("选人阶段自动提醒标记玩家:",
                    ToggleSwitch(config.Functions.EnableAutoTagReminder, v =>
                    {
                        if (v == config.Functions.EnableAutoTagReminder) return;
                        config.Functions.EnableAutoTagReminder = v;
                        Commit(nameof(FunctionsConfig.EnableAutoTagReminder));
                    }, header: null, onContent: "", offContent: "").AutomationName("标记玩家提醒"))
            ));

        async void StartFriendScan()
        {
            setIsScanningFriends(true);
            setFriendScanCompleted(0);
            setFriendScanTotal(0);
            try
            {
                var list = await friendCleanerService.ScanFriendsAsync((done, total) =>
                {
                    setFriendScanCompleted(done);
                    setFriendScanTotal(total);
                });
                setAllFriends(list);
                ShowToast($"好友扫描完成，共找到 {list.Count} 位好友");
            }
            catch (Exception ex)
            {
                ShowToast($"好友扫描失败: {ex.Message}", isError: true);
            }
            finally
            {
                setIsScanningFriends(false);
            }
        }

        async void LoadChallenges()
        {
            setChallengesLoading(true);
            try
            {
                var list = await toolsService.GetPlayerChallengesAsync();
                setChallengesList(list);
                ShowToast($"已加载 {list.Count} 个挑战勋章");
            }
            catch (Exception ex)
            {
                ShowToast($"加载挑战勋章失败: {ex.Message}", isError: true);
            }
            finally
            {
                setChallengesLoading(false);
            }
        }

        // 7. 观战卡片
        var spectateCard = ToolCard(
            "spectate",
            "\uE7B3",
            "观战",
            "观战同大区玩家正在进行的实时游戏 (支持 CMD 与 LCU 双通道)",
            "点击展开",
            VStack(8,
                TextBlock("输入目标召唤师的 Riot ID 或游戏名称，启动实时对局观战：")
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText),
                CardRow("观战通道:",
                    ComboBox(SpectateMethodLabels, spectateMethodIndex, idx => setSpectateMethodIndex(idx))
                        .Width(340)
                        .AutomationName("观战方式选择")),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBox(spectateName, v => setSpectateName(v), "输入召唤师名称，如 Hide on bush#KR1")
                            .Margin(new Thickness(0, 0, 8, 0))
                            .AutomationName("观战目标输入")
                            .Grid(row: 0, column: 0),
                        Button("启动观战", async () =>
                        {
                            if (string.IsNullOrWhiteSpace(spectateName))
                            {
                                ShowToast("请输入有效的召唤师名称", isError: true);
                                return;
                            }

                            var target = spectateName.Trim();
                            var method = spectateMethodIndex == 0 ? SpectateMethod.Cmd : SpectateMethod.Lcu;
                            ShowToast($"正在发起对「{target}」的观战请求...");
                            try
                            {
                                var res = await toolsService.LaunchSpectateAsync(target, method);
                                ShowToast(res.Message, isError: !res.Success);
                            }
                            catch (Exception ex)
                            {
                                ShowToast($"观战异常: {ex.Message}", isError: true);
                            }
                        }).AutomationName("启动观战按钮").Grid(row: 0, column: 1),
                    ])
            ));

        // 8. 好友管理与僵尸号清理卡片
        var friendCleanerStatus = allFriends.Count > 0 ? $"已加载 {allFriends.Count} 位好友" : "点击展开";
        var friendCleanerCard = ToolCard(
            "friendCleaner",
            "\uE716",
            "好友管理与僵尸号清理",
            "扫描好友列表，筛选并批量清理长期未上线的离线好友 (支持安全保护)",
            friendCleanerStatus,
            FriendCleanerView.Render(
                allFriends,
                isScanningFriends,
                isDeletingFriends,
                friendScanCompleted,
                friendScanTotal,
                friendSearchQuery,
                q => setFriendSearchQuery(q),
                friendFilterIndex,
                idx => setFriendFilterIndex(idx),
                selectedFriendIds,
                id =>
                {
                    var next = new HashSet<string>(selectedFriendIds);
                    if (!next.Add(id)) next.Remove(id);
                    setSelectedFriendIds(next);
                },
                () =>
                {
                    var filtered = FriendCleanerView.FilterFriends(allFriends, friendSearchQuery, friendFilterIndex);
                    var next = new HashSet<string>(selectedFriendIds);
                    foreach (var f in filtered)
                    {
                        if (f.CanDelete) next.Add(f.Friend.Id);
                    }
                    setSelectedFriendIds(next);
                },
                () => setSelectedFriendIds([]),
                isConfirmingDeleteFriends,
                () => setIsConfirmingDeleteFriends(true),
                () => setIsConfirmingDeleteFriends(false),
                async () =>
                {
                    if (selectedFriendIds.Count == 0) return;
                    setIsDeletingFriends(true);
                    try
                    {
                        var count = await friendCleanerService.BatchDeleteFriendsAsync(selectedFriendIds.ToList());
                        ShowToast($"成功清理 {count} 位离线好友");
                        setSelectedFriendIds([]);
                        setIsConfirmingDeleteFriends(false);
                        StartFriendScan();
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"删除好友失败: {ex.Message}", isError: true);
                    }
                    finally
                    {
                        setIsDeletingFriends(false);
                    }
                },
                StartFriendScan,
                palette
            ));

        var benchOverlayCard = ActionCard(
            "\uE790", "大乱斗板凳席悬浮窗", "在大乱斗选人阶段显示半透明置顶悬浮窗，可无 CD 抢板凳席英雄",
            ToggleSwitch(config.Functions.EnableBenchOverlay, v =>
            {
                if (v == config.Functions.EnableBenchOverlay) return;
                config.Functions.EnableBenchOverlay = v;
                Commit(nameof(FunctionsConfig.EnableBenchOverlay));
            }, header: null, onContent: "", offContent: "").AutomationName("大乱斗悬浮窗开关"));

        var autoReconnectCard = ActionCard(
            "\uE72C", "自动重连", "当你意外掉线退出游戏时自动重新连接",
            ToggleSwitch(config.Functions.EnableAutoReconnect, v =>
            {
                if (v == config.Functions.EnableAutoReconnect) return;
                config.Functions.EnableAutoReconnect = v;
                Commit(nameof(FunctionsConfig.EnableAutoReconnect));
            }, header: null, onContent: "", offContent: "").AutomationName("自动重连开关"));

        var autoLobbyCard = ToolCard(
            "autoLobby", "\uE715", "自动创建大厅", "启动 LOL 客户端后自动创建默认模式的大厅", config.Functions.EnableAutoCreateLobby ? "已启用" : "未启用",
            VStack(4,
                CardRow("客户端就绪后自动拉入指定大厅房间:",
                    ToggleSwitch(config.Functions.EnableAutoCreateLobby, v =>
                    {
                        if (v == config.Functions.EnableAutoCreateLobby) return;
                        config.Functions.EnableAutoCreateLobby = v;
                        Commit(nameof(FunctionsConfig.EnableAutoCreateLobby));
                    }, header: null, onContent: "", offContent: "").AutomationName("自动创建大厅开关")),
                CardRow("默认模式选择:",
                    ComboBox(LobbyModeLabels, IndexOfLobbyMode(config.Functions.DefaultGameMode), idx =>
                    {
                        var mode = LobbyModes[idx];
                        if (mode == config.Functions.DefaultGameMode) return;
                        config.Functions.DefaultGameMode = mode;
                        Commit(nameof(FunctionsConfig.DefaultGameMode));
                    }).Width(220).AutomationName("默认队列选择")),
                HStack(8,
                    Button("立即创建该模式大厅", async () =>
                    {
                        try
                        {
                            await toolsService.CreateLobbyAsync(config.Functions.DefaultGameMode);
                            ShowToast($"已创建模式 [{config.Functions.DefaultGameMode}] 大厅");
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"创建大厅失败: {ex.Message}", isError: true);
                        }
                    }).AutomationName("立即创建大厅按钮")
                ).Margin(new Thickness(0, 4, 0, 4))
            ));

        var lockStatusText = isSettingsLocked switch
        {
            true => "已锁定 (只读保护中)",
            false => "未锁定 (可写入)",
            null => "检测中",
        };

        var lockSettingsCard = ActionCard(
            "\uE72E", "游戏设置锁定", $"锁定游戏内配置 (PersistedSettings.json)，防止被意外篡改 [{lockStatusText}]",
            Button(isSettingsLocked == true ? "解除锁定" : "立即锁定", () =>
            {
                var nextState = !(isSettingsLocked ?? false);
                try
                {
                    toolsService.SetGameSettingsReadonly(lolPaths, nextState);
                    setIsSettingsLocked(nextState);
                    ShowToast(nextState ? "已锁定游戏配置文件 (只读保护生效)" : "已解除锁定 (配置恢复可写)");
                }
                catch (Exception ex)
                {
                    ShowToast($"设置锁定失败: {ex.Message}", isError: true);
                }
            }).AutomationName("锁定游戏配置"));

        // ══════════════════════════════════════════════════════
        // 3. 客户端组
        // ══════════════════════════════════════════════════════
        var fixWindowCard = ActionCard(
            "\uE740", "修复客户端窗口", "修复客户端错误的窗口大小与黑边缩放（重设 LeagueClientUx 尺寸）",
            Button("立即修复", async () =>
            {
                try
                {
                    var msg = await toolsService.FixClientWindowAsync();
                    ShowToast(msg);
                }
                catch (Exception ex)
                {
                    ShowToast($"窗口修复失败: {ex.Message}", isError: true);
                }
            }).AutomationName("修复客户端窗口"));

        var restartClientCard = ActionCard(
            "\uE777", "快速重启客户端", "重新引导 LeagueClientUx，无需重新排队或重新输入账号密码",
            Button("重启客户端", () =>
            {
                SendLcu(async t =>
                {
                    await t.SendAsync(LcuRequest.Post("/riotclient/kill-and-restart-ux", "{}"));
                }, "已向 Riot Client 发送重启指令，客户端正在重新引导...");
            }).AutomationName("快速重启客户端"));

        // ══════════════════════════════════════════════════════
        // 4. 个人主页组
        // ══════════════════════════════════════════════════════
        var signatureCard = ToolCard(
            "signature", "\uE70F", "个性签名", "自定义修改你在客户端好友列表中展示的个性状态签名", "点击展开",
            VStack(8,
                TextBlock("输入你想展示的个性化签名并应用：")
                    .FontSize(12).Foreground(Theme.SecondaryText),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBox(signatureDraft, v => setSignatureDraft(v), "输入新个性签名内容...")
                            .Margin(new Thickness(0, 0, 8, 0))
                            .AutomationName("签名内容输入")
                            .Grid(row: 0, column: 0),
                        Button("更新签名", async () =>
                        {
                            if (string.IsNullOrWhiteSpace(signatureDraft))
                            {
                                ShowToast("请输入有效的签名内容", isError: true);
                                return;
                            }
                            try
                            {
                                await toolsService.SetStatusMessageAsync(signatureDraft.Trim());
                                ShowToast("个性签名已成功更新");
                            }
                            catch (Exception ex)
                            {
                                ShowToast($"更新个性签名失败: {ex.Message}", isError: true);
                            }
                        }).AutomationName("更新签名按钮").Grid(row: 0, column: 1),
                    ])
            ));

        var profileBgCard = ToolCard(
            "profileBg", "\uEB9F", "生涯背景", "在个人生涯主页展示任意指定的英雄皮肤插画背景", "点击展开",
            VStack(10,
                TextBlock("浏览英雄皮肤并预览应用，或直接输入皮肤 ID：")
                    .FontSize(12).Foreground(Theme.SecondaryText),
                SkinPickerView.Render(
                    bgChampInput,
                    v => setBgChampInput(v),
                    bgLoading,
                    bgSkins,
                    bgSkinIndex,
                    idx => setBgSkinIndex(idx),
                    async skinId =>
                    {
                        try
                        {
                            await toolsService.SetProfileBackgroundAsync(skinId);
                            ShowToast($"已成功将生涯背景设置为皮肤 [{skinId}]");
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"设置背景失败: {ex.Message}", isError: true);
                        }
                    },
                    async () =>
                    {
                        if (!int.TryParse(bgChampInput.Trim(), out var champId))
                        {
                            ShowToast("请输入有效的英雄数字 ID", isError: true);
                            return;
                        }
                        setBgLoading(true);
                        try
                        {
                            var skins = await toolsService.GetChampionSkinsAsync(champId);
                            setBgSkins(skins);
                            setBgSkinIndex(0);
                            ShowToast($"获取到 {skins.Count} 款皮肤");
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"获取皮肤列表失败: {ex.Message}", isError: true);
                        }
                        finally
                        {
                            setBgLoading(false);
                        }
                    },
                    palette
                ),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 4, 0, 4)),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBox(skinIdDraft, v => setSkinIdDraft(v), "或直接输入皮肤 ID，如 1001")
                            .Margin(new Thickness(0, 0, 8, 0))
                            .AutomationName("皮肤ID输入")
                            .Grid(row: 0, column: 0),
                        Button("直接应用皮肤 ID", async () =>
                        {
                            if (!int.TryParse(skinIdDraft.Trim(), out var skinId))
                            {
                                ShowToast("请输入有效的数字皮肤 ID", isError: true);
                                return;
                            }
                            try
                            {
                                await toolsService.SetProfileBackgroundAsync(skinId);
                                ShowToast($"已设置个人生涯背景为皮肤 [{skinId}]");
                            }
                            catch (Exception ex)
                            {
                                ShowToast($"设置背景失败: {ex.Message}", isError: true);
                            }
                        }).AutomationName("直接应用皮肤ID").Grid(row: 0, column: 1),
                    ])
            ));

        var rankSpoofCard = ToolCard(
            "rankSpoof", "\uE734", "段位伪装", "在本地客户端及好友列表中伪造段位展示 (仅限视觉效果)", "点击展开",
            VStack(10,
                CardRow("排位队列类型:",
                    ComboBox(SpoofQueueLabels, spoofQueueIndex, index => setSpoofQueueIndex(index))
                        .Width(180).AutomationName("伪装队列选择")),
                CardRow("伪装段位级别:",
                    ComboBox(SpoofTierLabels, spoofTierIndex, index => setSpoofTierIndex(index))
                        .Width(220).AutomationName("伪装段位选择")),
                CardRow("伪装小段级别 (Division):",
                    ComboBox(SpoofDivisionLabels, spoofDivisionIndex, index => setSpoofDivisionIndex(index))
                        .Width(180).AutomationName("伪装小段选择")),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        TextBlock("应用后好友列表中的名片即时显示此段位").FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
                        Button("应用伪装", async () =>
                        {
                            var q = SpoofQueues[spoofQueueIndex];
                            var t = SpoofTiers[spoofTierIndex];
                            var d = SpoofDivisions[spoofDivisionIndex];
                            try
                            {
                                await toolsService.SetRankSpoofAsync(q, t, d);
                                ShowToast($"段位伪装已生效: {SpoofTierLabels[spoofTierIndex]} {d}");
                            }
                            catch (Exception ex)
                            {
                                ShowToast($"应用段位伪装失败: {ex.Message}", isError: true);
                            }
                        }).AutomationName("应用段位伪装").Grid(row: 0, column: 1),
                    ])
            ));

        var onlineStatusCard = ActionCard(
            "\uE701", "在线状态切换", "在好友列表中切换你的展示状态（在线 / 离开 / 隐身）",
            HStack(8,
                Button("在线", async () =>
                {
                    try
                    {
                        await toolsService.SetOnlineStatusAsync("chat");
                        ShowToast("已切换为在线状态");
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"切换在线状态失败: {ex.Message}", isError: true);
                    }
                }).AutomationName("设为在线"),
                Button("离开", async () =>
                {
                    try
                    {
                        await toolsService.SetOnlineStatusAsync("away");
                        ShowToast("已切换为离开状态");
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"切换离开状态失败: {ex.Message}", isError: true);
                    }
                }).AutomationName("设为离开"),
                Button("隐身", async () =>
                {
                    try
                    {
                        await toolsService.SetOnlineStatusAsync("offline");
                        ShowToast("已切换为隐身离线状态");
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"切换隐身状态失败: {ex.Message}", isError: true);
                    }
                }).AutomationName("设为隐身")));

        var badgesCard = ToolCard(
            "badges", "\uE735", "挑战勋章与头像框", "装配/卸下展示的挑战勋章、称号与头像框", "点击展开",
            VStack(10,
                HStack(8,
                    Button("卸下全部勋章", async () =>
                    {
                        try
                        {
                            await toolsService.ClearChallengeBadgesAsync();
                            ShowToast("已卸下展示的全部挑战勋章");
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"卸下勋章失败: {ex.Message}", isError: true);
                        }
                    }).AutomationName("卸下勋章"),
                    Button("卸下头像框", async () =>
                    {
                        try
                        {
                            await toolsService.ClearProfileTokensAsync();
                            ShowToast("已卸下生涯头像框");
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"卸下头像框失败: {ex.Message}", isError: true);
                        }
                    }).AutomationName("卸下头像框")
                ),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 4, 0, 4)),
                ChallengePreferencesView.Render(
                    challengesList,
                    challengesLoading,
                    badgeSearchQuery,
                    q => setBadgeSearchQuery(q),
                    badgeTierIndex,
                    idx => setBadgeTierIndex(idx),
                    badgeTitleInput,
                    v => setBadgeTitleInput(v),
                    selectedBadgeIds,
                    id =>
                    {
                        var next = new List<long>(selectedBadgeIds);
                        if (next.Contains(id))
                        {
                            next.Remove(id);
                        }
                        else
                        {
                            if (next.Count >= 3)
                            {
                                ShowToast("最多只能同时佩戴 3 枚挑战勋章", isError: true);
                                return;
                            }
                            next.Add(id);
                        }
                        setSelectedBadgeIds(next);
                    },
                    async () =>
                    {
                        try
                        {
                            await toolsService.UpdatePlayerPreferencesAsync(badgeTitleInput.Trim(), selectedBadgeIds);
                            ShowToast("已成功更新挑战称号与勋章配置");
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"保存挑战配置失败: {ex.Message}", isError: true);
                        }
                    },
                    LoadChallenges,
                    palette
                )
            ));

        Element? feedbackBanner = null;
        if (error is not null)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(Theme.Ref("SystemFillColorCriticalBrush")),
                    TextBlock($"配置错误: {error}").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).FontSize(12).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }
        else if (actionFeedback is not null)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(palette.Accent),
                    TextBlock(actionFeedback).FontSize(12).Foreground(palette.Accent).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }
        else if (savedAt is { } time)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(palette.Accent),
                    TextBlock($"配置已保存 · {time:HH:mm:ss}").FontSize(12).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }

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
                    Heading("工具箱"),
                    Caption("英雄选择自动化、游戏流程、客户端维护与个人主页功能（对应原实现：src/views/Tools.vue）")
                ).Margin(new Thickness(0, 0, 0, 6)),

                // 1. 英雄选择
                GroupHeader("英雄选择"),
                autoAcceptCard,
                autoSwapCard,
                autoHoverCard,
                autoBanCard,
                autoSpellsCard,

                // 2. 游戏
                GroupHeader("游戏"),
                autoGameflowCard,
                spectateCard,
                friendCleanerCard,
                benchOverlayCard,
                autoReconnectCard,
                autoLobbyCard,
                lockSettingsCard,

                // 3. 客户端
                GroupHeader("客户端"),
                fixWindowCard,
                restartClientCard,

                // 4. 个人主页
                GroupHeader("个人主页"),
                signatureCard,
                profileBgCard,
                rankSpoofCard,
                onlineStatusCard,
                badgesCard
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
}
