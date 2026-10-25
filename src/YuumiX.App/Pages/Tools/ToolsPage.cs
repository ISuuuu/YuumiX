using System.Text.Json;
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
        "LCU 方式 (客户端内置观战，失败自动切换 CMD)",
        "CMD 方式 (直接启动客户端进程，绕开限制)"
    ];

    private static readonly string[] LaneLabels = ["默认", "上单 (Top)", "打野 (Jungle)", "中单 (Mid)", "下路 (Bot)", "辅助 (Support)"];

    private static List<int> GetPickListByLane(FunctionsConfig cfg, int laneIndex) => laneIndex switch
    {
        1 => cfg.AutoSelectChampionTop,
        2 => cfg.AutoSelectChampionJug,
        3 => cfg.AutoSelectChampionMid,
        4 => cfg.AutoSelectChampionBot,
        5 => cfg.AutoSelectChampionSup,
        _ => cfg.AutoSelectChampion,
    };

    private static List<int> GetBanListByLane(FunctionsConfig cfg, int laneIndex) => laneIndex switch
    {
        1 => cfg.AutoBanChampionTop,
        2 => cfg.AutoBanChampionJug,
        3 => cfg.AutoBanChampionMid,
        4 => cfg.AutoBanChampionBot,
        5 => cfg.AutoBanChampionSup,
        _ => cfg.AutoBanChampion,
    };

    private static List<int> GetSpellListByLane(FunctionsConfig cfg, int laneIndex) => laneIndex switch
    {
        1 => cfg.AutoSetSummonerSpellTop,
        2 => cfg.AutoSetSummonerSpellJug,
        3 => cfg.AutoSetSummonerSpellMid,
        4 => cfg.AutoSetSummonerSpellBot,
        5 => cfg.AutoSetSummonerSpellSup,
        _ => cfg.AutoSetSummonerSpell,
    };

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

        // 英雄选用/禁用/技能分路状态
        var (pickLaneIndex, setPickLaneIndex) = UseState(0, threadSafe: true);
        var (banLaneIndex, setBanLaneIndex) = UseState(0, threadSafe: true);
        var (spellLaneIndex, setSpellLaneIndex) = UseState(0, threadSafe: true);
        var (spellSlotIndex, setSpellSlotIndex) = UseState(0, threadSafe: true);

        var (championSummaryList, setChampionSummaryList) = UseState<IReadOnlyList<ChampionSummaryItem>>(Array.Empty<ChampionSummaryItem>(), threadSafe: true);
        var (championSummaryLoading, setChampionSummaryLoading) = UseState(false, threadSafe: true);
        var (summonerSpellsList, setSummonerSpellsList) = UseState<IReadOnlyList<SummonerSpellItem>>(Array.Empty<SummonerSpellItem>(), threadSafe: true);
        var (summonerSpellsLoading, setSummonerSpellsLoading) = UseState(false, threadSafe: true);

        var (pickSearchQuery, setPickSearchQuery) = UseState(string.Empty, threadSafe: true);
        var (banSearchQuery, setBanSearchQuery) = UseState(string.Empty, threadSafe: true);

        // 二次确认状态
        var (isConfirmingRestartClient, setIsConfirmingRestartClient) = UseState(false, threadSafe: true);
        var (isConfirmingClearBadges, setIsConfirmingClearBadges) = UseState(false, threadSafe: true);
        var (isConfirmingClearBorder, setIsConfirmingClearBorder) = UseState(false, threadSafe: true);

        // 游戏设置锁定状态
        var (isSettingsLocked, setIsSettingsLocked) = UseState<bool?>(null, threadSafe: true);

        // 正文列宽度：跟随滚动区宽度收缩，超过上限后固定并居中（原因见 ContentColumn 注释）。
        var (columnWidth, setColumnWidth) = UseState(ContentColumn.MaxWidth, threadSafe: true);

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

        // 连接态必须订阅：原来直接读 lcu.CurrentState，客户端启动或退出都不会触发重渲染，
        // 「离线禁用」就会一直停在进页那一刻的判断上。
        // 声明位置也刻意在下面的 CleanToggle / ActionCard / ToolCard 之前——
        // 这些局部函数要捕获 isLcuConnected，C# 不允许捕获在其之后声明的局部变量。
        var lcuConnection = services.Services.GetRequiredService<LcuConnection>();
        var (connState, setConnState) = UseState(lcuConnection.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcuConnection.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);
        bool isLcuConnected = connState.Status == LcuConnectionStatus.Connected;

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
                    Body(label)
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    control.VAlign(VerticalAlignment.Center).HAlign(HorizontalAlignment.Right).Grid(row: 0, column: 1),
                ])
            .Padding(new Thickness(0, 8, 0, 8));

        // 离线守卫：客户端没开时开关一律禁用。旧系统是整页 opacity 0.5 + pointer-events:none，
        // 本版改为按控件禁用——半透明叠在 Mica + 半透明卡片上会把 12~14px 中文压成灰雾（见 offlineBanner 注释）。
        Element CleanToggle(bool value, Action<bool> onChange, string? automationName = null) =>
            ToggleSwitch(value, v => onChange(v), header: null, onContent: null, offContent: null)
                .MinWidth(0)
                .HAlign(HorizontalAlignment.Right)
                .IsEnabled(isLcuConnected)
                .AutomationName(automationName ?? string.Empty);

        // 离线守卫：客户端没开时右侧控件一律禁用（改了也不会生效，只会留下脏配置）。
        //
        // IsEnabled 的语义是「设置 Control.IsEnabled」，只对真正的控件有效；
        // 传进来的若是一组按钮的容器（StackPanel），它没有这个属性，
        // 这类组合控件必须由调用方逐个按钮再禁一次（见 restartClientCard / onlineStatusCard）。
        // 旧系统对应的是 .offline-disabled 的 pointer-events:none + opacity:0.5，
        // 本版用控件的 Disabled 视觉态，既拦住交互又不把正文压成灰雾。
        Element Guard(Element control) =>
            control is StackElement ? control : control.IsEnabled(isLcuConnected);

        // 单行直接操作卡片（不折叠，右侧直接展示操作按钮或状态开关）。
        Element ActionCard(string title, string description, Element control) =>
            Border(
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        VStack(2,
                            Body(title).Foreground(Theme.PrimaryText),
                            Body(description)
                                .Foreground(Theme.SecondaryText)
                                .Margin(new Thickness(0, 3, 0, 0))
                        ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),

                        Guard(control)
                            .VAlign(VerticalAlignment.Center)
                            .HAlign(HorizontalAlignment.Right)
                            .Grid(row: 0, column: 1),
                    ]
                ).Padding(new Thickness(16, 12, 16, 12))
            )
            .CornerRadius(8)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1)
            .HAlign(HorizontalAlignment.Stretch);

        Element ToolCard(
            string key,
            string title,
            string description,
            string statusText,
            bool isStatusEnabled,
            Element innerContent)
        {
            // 离线守卫：未连接时既不展开、也不接受展开点击。
            // 只禁按钮不够——已经展开的卡片会继续渲染里面的开关与输入框，
            // 所以这里连展开态本身一起按连接态夹紧，展开记忆（expandedKeys）保留，
            // 客户端连上后自动恢复原来的展开状态。
            var isExpanded = isLcuConnected && expandedKeys.Contains(key);

            // 状态点颜色只由调用方给出的布尔决定。曾按 statusText 是否等于几个字面量反推，
            // 结果是改一次文案就静默翻转配色，且 "未锁定 (可写入)" 那个分支早已无人产出（见 lockStatusCard）。
            var statusColor = isStatusEnabled ? Theme.Accent : Theme.SecondaryText;

            var headerContent = Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Auto],
                [GridSize.Auto],
                [
                    // 标题与说明
                    VStack(2,
                        Body(title).Foreground(Theme.PrimaryText),
                        Body(description)
                            .Foreground(Theme.SecondaryText)
                            .Margin(new Thickness(0, 3, 0, 0))
                    ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),

                    // 右侧状态摘要
                    Caption(statusText)
                        .Foreground(statusColor)
                        .VAlign(VerticalAlignment.Center)
                        .Margin(new Thickness(12, 0, 10, 0))
                        .Grid(row: 0, column: 1),

                    // 展开指示器（下箭头 / 右箭头）
                    Caption(isExpanded ? "\uE70D" : "\uE76C")
                        .FontFamily("Segoe Fluent Icons")
                        .Foreground(Theme.SecondaryText)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                ]
            ).HAlign(HorizontalAlignment.Stretch);

            var headerButton = Button(headerContent, () => ToggleExpanded(key))
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(0)
                .Padding(new Thickness(16, 12, 16, 12))
                .HAlign(HorizontalAlignment.Stretch)
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .CornerRadius(8)
                .IsEnabled(isLcuConnected)
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

        async void EnsureCatalogLoaded()
        {
            if (championSummaryList.Count == 0 && !championSummaryLoading && isLcuConnected)
            {
                setChampionSummaryLoading(true);
                try
                {
                    var champs = await toolsService.GetChampionSummaryAsync();
                    setChampionSummaryList(champs);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "拉取英雄列表失败");
                }
                finally
                {
                    setChampionSummaryLoading(false);
                }
            }

            if (summonerSpellsList.Count == 0 && !summonerSpellsLoading && isLcuConnected)
            {
                setSummonerSpellsLoading(true);
                try
                {
                    var spells = await toolsService.GetSummonerSpellsAsync();
                    setSummonerSpellsList(spells);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "拉取技能列表失败");
                }
                finally
                {
                    setSummonerSpellsLoading(false);
                }
            }
        }

        if ((expandedKeys.Contains("autoHover") || expandedKeys.Contains("autoBan") || expandedKeys.Contains("autoSpells"))
            && (championSummaryList.Count == 0 || summonerSpellsList.Count == 0))
        {
            EnsureCatalogLoaded();
        }

        if (expandedKeys.Contains("badges") && challengesList.Count == 0 && !challengesLoading && isLcuConnected)
        {
            LoadChallenges();
        }

        Element LaneTabs(int currentLane, Action<int> onSelectLane)
        {
            var buttons = new List<Element>();
            for (int i = 0; i < LaneLabels.Length; i++)
            {
                int idx = i;
                bool isCurrent = idx == currentLane;
                buttons.Add(
                    Button(LaneLabels[idx], () => onSelectLane(idx))
                        .Background(isCurrent ? Theme.Ref("LayerFillColorDefaultBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
                        .BorderBrush(isCurrent ? Theme.Accent : Theme.ControlStroke)
                        .BorderThickness(isCurrent ? 1.5 : 1)
                        .CornerRadius(6)
                        .Padding(8, 4)
                        .Margin(new Thickness(0, 0, 6, 0))
                        .AutomationName($"分路_{LaneLabels[idx]}")
                );
            }
            return HStack(0, buttons.ToArray()).Margin(new Thickness(0, 6, 0, 8));
        }

        void TogglePickChampion(int champId)
        {
            var list = GetPickListByLane(config.Functions, pickLaneIndex);
            if (list.Contains(champId))
            {
                list.Remove(champId);
            }
            else
            {
                if (list.Count >= 5)
                {
                    ShowToast("每个分路最多选用 5 位候选英雄", isError: true);
                    return;
                }
                list.Add(champId);
            }
            Commit(nameof(FunctionsConfig.AutoSelectChampion));
        }

        void ClearPickSelection()
        {
            var list = GetPickListByLane(config.Functions, pickLaneIndex);
            list.Clear();
            Commit(nameof(FunctionsConfig.AutoSelectChampion));
        }

        void ToggleBanChampion(int champId)
        {
            var list = GetBanListByLane(config.Functions, banLaneIndex);
            if (list.Contains(champId))
            {
                list.Remove(champId);
            }
            else
            {
                if (list.Count >= 5)
                {
                    ShowToast("每个分路最多禁用 5 位候选英雄", isError: true);
                    return;
                }
                list.Add(champId);
            }
            Commit(nameof(FunctionsConfig.AutoBanChampion));
        }

        void ClearBanSelection()
        {
            var list = GetBanListByLane(config.Functions, banLaneIndex);
            list.Clear();
            Commit(nameof(FunctionsConfig.AutoBanChampion));
        }

        void SetSpellForSlot(int slotIndex, int spellId)
        {
            var list = GetSpellListByLane(config.Functions, spellLaneIndex);
            while (list.Count < 2) list.Add(4);
            list[slotIndex] = spellId;
            Commit(nameof(FunctionsConfig.AutoSetSummonerSpell));
        }

        // 与设置页同一字阶：页面标题 Title(28) / 分组 Subtitle(20) / 卡片标题 Body(14) / 说明 Caption(12)。
        // 卡片标题与状态一律不加字重——雅黑没有 SemiBold，600 会被吸附到 Bold，小字号下笔画粘连、发糊。
        Element GroupHeader(string title) =>
            Subtitle(title)
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .Margin(new Thickness(4, 16, 0, 6));

        // 1. 自动接受对局卡片
        var autoAcceptStatus = config.Functions.EnableAutoAcceptMatching
            ? $"已启用，延迟: {config.Functions.AutoAcceptMatchingDelay} 秒"
            : "未启用";

        var autoAcceptCard = ToolCard(
            "autoAccept",
            "自动接受对局",
            "在你设置的秒数之后自动接受对局匹配",
            autoAcceptStatus,
            config.Functions.EnableAutoAcceptMatching,
            VStack(4,
                CardRow("在对局找到后接受对局前延迟的秒数:",
                    CompactStepperControl.Render(
                        config.Functions.AutoAcceptMatchingDelay,
                        0,
                        11,
                        1,
                        next =>
                        {
                            if (next == config.Functions.AutoAcceptMatchingDelay) return;
                            config.Functions.AutoAcceptMatchingDelay = next;
                            Commit(nameof(FunctionsConfig.AutoAcceptMatchingDelay));
                        },
                        palette,
                        "自动接受延迟")),
                CardRow("启用自动接受对局:",
                    CleanToggle(config.Functions.EnableAutoAcceptMatching, v =>
                    {
                        if (v == config.Functions.EnableAutoAcceptMatching) return;
                        config.Functions.EnableAutoAcceptMatching = v;
                        Commit(nameof(FunctionsConfig.EnableAutoAcceptMatching));
                    }, "启用自动接受对局"))
            ));

        // 2. 自动接受交换请求卡片
        var autoSwapEnabled = config.Functions.AutoAcceptCeilSwap || config.Functions.AutoAcceptChampTrade;
        var autoSwapStatus = autoSwapEnabled ? "已启用" : "未启用";

        var autoSwapCard = ToolCard(
            "autoSwap",
            "自动接受交换请求",
            "自动接受队友的交换楼层或英雄的请求",
            autoSwapStatus,
            autoSwapEnabled,
            VStack(4,
                CardRow("自动接受楼层交换请求:",
                    CleanToggle(config.Functions.AutoAcceptCeilSwap, v =>
                    {
                        if (v == config.Functions.AutoAcceptCeilSwap) return;
                        config.Functions.AutoAcceptCeilSwap = v;
                        Commit(nameof(FunctionsConfig.AutoAcceptCeilSwap));
                    }, "自动接受楼层交换")),
                CardRow("自动接受英雄交换请求:",
                    CleanToggle(config.Functions.AutoAcceptChampTrade, v =>
                    {
                        if (v == config.Functions.AutoAcceptChampTrade) return;
                        config.Functions.AutoAcceptChampTrade = v;
                        Commit(nameof(FunctionsConfig.AutoAcceptChampTrade));
                    }, "自动接受英雄交换"))
            ));

        // 3. 自动选用/预选英雄卡片
        var autoHoverEnabled = config.Functions.EnableAutoSelectChampion || config.Functions.EnableAutoHoverChampion;
        var autoHoverStatus = autoHoverEnabled ? "已启用" : "未启用";

        var autoHoverCard = ToolCard(
            "autoHover",
            "自动选用/预选英雄",
            "在你进入英雄选择时自动亮起/预选英雄",
            autoHoverStatus,
            autoHoverEnabled,
            VStack(4,
                CardRow("启用自动亮起/预选:",
                    CleanToggle(config.Functions.EnableAutoHoverChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoHoverChampion) return;
                        config.Functions.EnableAutoHoverChampion = v;
                        Commit(nameof(FunctionsConfig.EnableAutoHoverChampion));
                    }, "启用自动预选")),
                CardRow("在结束时确认选择 (防秒退):",
                    CleanToggle(config.Functions.AutoSelectConfirmOnTimeout, v =>
                    {
                        if (v == config.Functions.AutoSelectConfirmOnTimeout) return;
                        config.Functions.AutoSelectConfirmOnTimeout = v;
                        Commit(nameof(FunctionsConfig.AutoSelectConfirmOnTimeout));
                    }, "超时自动确认")),
                CardRow("选人阶段自动选择英雄:",
                    CleanToggle(config.Functions.EnableAutoSelectChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoSelectChampion) return;
                        config.Functions.EnableAutoSelectChampion = v;
                        Commit(nameof(FunctionsConfig.EnableAutoSelectChampion));
                    }, "自动选择英雄")),
                CardRow("在对局信息中显示秒退按钮:",
                    CleanToggle(config.Functions.EnableDodgeInGameInfo, v =>
                    {
                        if (v == config.Functions.EnableDodgeInGameInfo) return;
                        config.Functions.EnableDodgeInGameInfo = v;
                        Commit(nameof(FunctionsConfig.EnableDodgeInGameInfo));
                    }, "对局信息秒退按钮")),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 6, 0, 6)),
                Caption("分路预选候选英雄库:").Foreground(Theme.SecondaryText),
                LaneTabs(pickLaneIndex, idx => setPickLaneIndex(idx)),
                ChampionPickerView.Render(
                    championSummaryList,
                    GetPickListByLane(config.Functions, pickLaneIndex),
                    TogglePickChampion,
                    ClearPickSelection,
                    pickSearchQuery,
                    q => setPickSearchQuery(q),
                    championSummaryLoading,
                    palette)
            ));

        // 4. 自动禁用英雄卡片
        var autoBanStatus = config.Functions.EnableAutoBanChampion ? "已启用" : "未启用";

        var autoBanCard = ToolCard(
            "autoBan",
            "自动禁用英雄",
            "在你的禁用环节开始时自动禁用英雄",
            autoBanStatus,
            config.Functions.EnableAutoBanChampion,
            VStack(4,
                CardRow("禁用环节自动禁用设定英雄:",
                    CleanToggle(config.Functions.EnableAutoBanChampion, v =>
                    {
                        if (v == config.Functions.EnableAutoBanChampion) return;
                        config.Functions.EnableAutoBanChampion = v;
                        Commit(nameof(FunctionsConfig.EnableAutoBanChampion));
                    }, "自动禁用开关")),
                CardRow("避开队友已预选的英雄 (假禁用):",
                    CleanToggle(config.Functions.PretendBan, v =>
                    {
                        if (v == config.Functions.PretendBan) return;
                        config.Functions.PretendBan = v;
                        Commit(nameof(FunctionsConfig.PretendBan));
                    }, "避开队友预选")),
                CardRow("自动禁用前等待延迟 (秒):",
                    CompactStepperControl.RenderDouble(
                        config.Functions.AutoBanDelay,
                        0,
                        15,
                        1,
                        next =>
                        {
                            if (Math.Abs(next - config.Functions.AutoBanDelay) < 0.01) return;
                            config.Functions.AutoBanDelay = next;
                            Commit(nameof(FunctionsConfig.AutoBanDelay));
                        },
                        palette,
                        "自动禁用延迟")),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 6, 0, 6)),
                Caption("分路禁用候选英雄库:").Foreground(Theme.SecondaryText),
                LaneTabs(banLaneIndex, idx => setBanLaneIndex(idx)),
                ChampionPickerView.Render(
                    championSummaryList,
                    GetBanListByLane(config.Functions, banLaneIndex),
                    ToggleBanChampion,
                    ClearBanSelection,
                    banSearchQuery,
                    q => setBanSearchQuery(q),
                    championSummaryLoading,
                    palette)
            ));

        // 5. 自动设置召唤师技能卡片
        var autoSpellsStatus = config.Functions.EnableAutoSetSpells ? "已启用" : "未启用";

        var autoSpellsCard = ToolCard(
            "autoSpells",
            "自动设置召唤师技能",
            "当你的英雄选择开始时自动设置召唤师技能",
            autoSpellsStatus,
            config.Functions.EnableAutoSetSpells,
            VStack(4,
                CardRow("锁定英雄后自动写入配置好的技能组:",
                    CleanToggle(config.Functions.EnableAutoSetSpells, v =>
                    {
                        if (v == config.Functions.EnableAutoSetSpells) return;
                        config.Functions.EnableAutoSetSpells = v;
                        Commit(nameof(FunctionsConfig.EnableAutoSetSpells));
                    }, "自动召唤师技能")),
                Border(Empty()).Height(1).Background(Theme.CardStroke).Margin(new Thickness(0, 6, 0, 6)),
                Caption("分路召唤师技能组:").Foreground(Theme.SecondaryText),
                LaneTabs(spellLaneIndex, idx => setSpellLaneIndex(idx)),
                SpellPickerView.Render(
                    summonerSpellsList,
                    GetSpellListByLane(config.Functions, spellLaneIndex),
                    spellSlotIndex,
                    slot => setSpellSlotIndex(slot),
                    SetSpellForSlot,
                    summonerSpellsLoading,
                    palette)
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
            "自动游戏流程",
            "自动处理邀请、点赞、再来一局与 ARAM 报边",
            autoFlowStatus,
            autoFlowEnabled,
            VStack(4,
                CardRow("自动接受好友邀请:",
                    CleanToggle(config.Functions.EnableAutoHandleInvite, v =>
                    {
                        if (v == config.Functions.EnableAutoHandleInvite) return;
                        config.Functions.EnableAutoHandleInvite = v;
                        Commit(nameof(FunctionsConfig.EnableAutoHandleInvite));
                    }, "自动接受邀请")),
                CardRow("对局结束后自动点赞:",
                    CleanToggle(config.Functions.EnableAutoHonor, v =>
                    {
                        if (v == config.Functions.EnableAutoHonor) return;
                        config.Functions.EnableAutoHonor = v;
                        Commit(nameof(FunctionsConfig.EnableAutoHonor));
                    }, "自动点赞")),
                CardRow("对局结束后自动再来一局:",
                    CleanToggle(config.Functions.EnableAutoPlayAgain, v =>
                    {
                        if (v == config.Functions.EnableAutoPlayAgain) return;
                        config.Functions.EnableAutoPlayAgain = v;
                        Commit(nameof(FunctionsConfig.EnableAutoPlayAgain));
                    }, "自动再来一局")),
                CardRow("ARAM 选人时自动报边:",
                    CleanToggle(config.Functions.EnableAutoAramTeamSide, v =>
                    {
                        if (v == config.Functions.EnableAutoAramTeamSide) return;
                        config.Functions.EnableAutoAramTeamSide = v;
                        Commit(nameof(FunctionsConfig.EnableAutoAramTeamSide));
                    }, "ARAM报边")),
                CardRow("报边消息对队友可见:",
                    CleanToggle(config.Functions.AramTeamSideVisibleToTeam, v =>
                    {
                        if (v == config.Functions.AramTeamSideVisibleToTeam) return;
                        config.Functions.AramTeamSideVisibleToTeam = v;
                        Commit(nameof(FunctionsConfig.AramTeamSideVisibleToTeam));
                    }, "报边队友可见")),
                CardRow("选人阶段自动提醒标记玩家:",
                    CleanToggle(config.Functions.EnableAutoTagReminder, v =>
                    {
                        if (v == config.Functions.EnableAutoTagReminder) return;
                        config.Functions.EnableAutoTagReminder = v;
                        Commit(nameof(FunctionsConfig.EnableAutoTagReminder));
                    }, "标记玩家提醒"))
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
            "观战",
            "观战同大区玩家正在进行的实时游戏 (支持 CMD 与 LCU 双通道)",
            "点击展开",
            false,
            VStack(8,
                Body("输入目标召唤师的 Riot ID 或游戏名称，启动实时对局观战：")
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

                            // 清洗 LTR/RTL 特殊控制字符并去除首尾空白
                            var target = spectateName.Replace("\u2066", "").Replace("\u2069", "").Trim();
                            var method = spectateMethodIndex == 0 ? SpectateMethod.Lcu : SpectateMethod.Cmd;
                            ShowToast($"正在发起对「{target}」的观战请求...");
                            try
                            {
                                var res = await toolsService.LaunchSpectateAsync(target, method);
                                if (!res.Success && method == SpectateMethod.Lcu)
                                {
                                    ShowToast("LCU 内置观战通道未就绪，正在自动切换至 CMD 方式重试...");
                                    res = await toolsService.LaunchSpectateAsync(target, SpectateMethod.Cmd);
                                }
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
        var filteredFriendsCount = FriendCleanerView.FilterFriends(allFriends, friendSearchQuery, friendFilterIndex).Count;
        var friendCleanerStatus = allFriends.Count > 0 ? $"共 {allFriends.Count} 人 / 筛选出 {filteredFriendsCount} 人" : "点击展开";
        var friendCleanerCard = ToolCard(
            "friendCleaner",
            "好友管理与僵尸号清理",
            "扫描好友列表，筛选并批量清理长期未上线的离线好友 (支持安全保护)",
            friendCleanerStatus,
            allFriends.Count > 0,
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
            "大乱斗板凳席悬浮窗", "在大乱斗选人阶段显示半透明置顶悬浮窗，可无 CD 抢板凳席英雄",
            CleanToggle(config.Functions.EnableBenchOverlay, v =>
            {
                if (v == config.Functions.EnableBenchOverlay) return;
                config.Functions.EnableBenchOverlay = v;
                Commit(nameof(FunctionsConfig.EnableBenchOverlay));
            }, "大乱斗悬浮窗开关"));

        var autoReconnectCard = ActionCard(
            "自动重连", "当你意外掉线退出游戏时自动重新连接",
            CleanToggle(config.Functions.EnableAutoReconnect, v =>
            {
                if (v == config.Functions.EnableAutoReconnect) return;
                config.Functions.EnableAutoReconnect = v;
                Commit(nameof(FunctionsConfig.EnableAutoReconnect));
            }, "自动重连开关"));

        var autoLobbyCard = ToolCard(
            "autoLobby", "自动创建大厅", "启动 LOL 客户端后自动创建默认模式的大厅", config.Functions.EnableAutoCreateLobby ? "已启用" : "未启用",
            config.Functions.EnableAutoCreateLobby,
            VStack(4,
                CardRow("客户端就绪后自动拉入指定大厅房间:",
                    CleanToggle(config.Functions.EnableAutoCreateLobby, v =>
                    {
                        if (v == config.Functions.EnableAutoCreateLobby) return;
                        config.Functions.EnableAutoCreateLobby = v;
                        Commit(nameof(FunctionsConfig.EnableAutoCreateLobby));
                    }, "自动创建大厅开关")),
                CardRow("默认模式选择:",
                    ComboBox(LobbyModeLabels, IndexOfLobbyMode(config.Functions.DefaultGameMode), idx =>
                    {
                        var mode = LobbyModes[idx];
                        if (mode == config.Functions.DefaultGameMode) return;
                        config.Functions.DefaultGameMode = mode;
                        Commit(nameof(FunctionsConfig.DefaultGameMode));
                    }).Width(220).HAlign(HorizontalAlignment.Right).IsEnabled(config.Functions.EnableAutoCreateLobby).AutomationName("默认队列选择")),
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
            "游戏设置锁定", $"锁定游戏内配置 (PersistedSettings.json)，防止被意外篡改 [{lockStatusText}]",
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
            "修复客户端窗口", "修复客户端错误的窗口大小与黑边缩放（重设 LeagueClientUx 尺寸）",
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
            "快速重启客户端", "重新引导 LeagueClientUx，无需重新排队或重新输入账号密码",
            // 这一组是 HStack 而不是单个控件，ActionCard 的 IsEnabled 对 StackPanel 无效，
            // 因此三个按钮各自再禁一次（客户端没开时重启/取消都无从谈起）。
            isConfirmingRestartClient
                ? HStack(8,
                    Caption("⚡ 确定重启客户端？无需重登").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center),
                    Button(Caption("取消"), () => setIsConfirmingRestartClient(false)).Padding(8, 4).IsEnabled(isLcuConnected).AutomationName("取消重启客户端"),
                    Button(Caption("确认重启"), () =>
                    {
                        setIsConfirmingRestartClient(false);
                        SendLcu(async t =>
                        {
                            await t.SendAsync(LcuRequest.Post("/riotclient/kill-and-restart-ux", "{}"));
                        }, "已向 Riot Client 发送重启指令，客户端正在重新引导...");
                    })
                    .Background(Theme.Ref("SystemFillColorCriticalBrush"))
                    .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                    .Padding(8, 4)
                    .IsEnabled(isLcuConnected)
                    .AutomationName("确认重启客户端"))
                : Button("重启客户端", () => setIsConfirmingRestartClient(true)).IsEnabled(isLcuConnected).AutomationName("快速重启客户端"));

        // ══════════════════════════════════════════════════════
        // 4. 个人主页组
        // ══════════════════════════════════════════════════════
        var signatureCard = ToolCard(
            "signature", "个性签名", "自定义修改你在客户端好友列表中展示的个性状态签名", "点击展开",
            false,
            VStack(8,
                Body("输入你想展示的个性化签名并应用：")
                    .Foreground(Theme.SecondaryText),
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
                                setSignatureDraft(string.Empty);
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
            "profileBg", "生涯背景", "在个人生涯主页展示任意指定的英雄皮肤插画背景", "点击展开",
            false,
            VStack(10,
                Body("浏览英雄皮肤并预览应用，或直接输入皮肤 ID：")
                    .Foreground(Theme.SecondaryText),
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
                        var text = bgChampInput.Trim();
                        int champId = 0;
                        if (!int.TryParse(text, out champId))
                        {
                            var matched = championSummaryList.FirstOrDefault(c =>
                                c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                                || c.Alias.Contains(text, StringComparison.OrdinalIgnoreCase)
                                || c.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
                            if (matched is not null)
                            {
                                champId = matched.Id;
                            }
                        }

                        if (champId <= 0)
                        {
                            ShowToast("未匹配到英雄，请输入有效英雄名称（如「安妮」）或数字 ID", isError: true);
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

        bool isApexTier = SpoofTiers[spoofTierIndex] is "CHALLENGER" or "GRANDMASTER" or "MASTER" or "UNRANKED";

        var rankSpoofCard = ToolCard(
            "rankSpoof", "段位伪装", "在本地客户端及好友列表中伪造段位展示 (仅限视觉效果)", "点击展开",
            false,
            VStack(10,
                CardRow("排位队列类型:",
                    ComboBox(SpoofQueueLabels, spoofQueueIndex, index => setSpoofQueueIndex(index))
                        .Width(180).AutomationName("伪装队列选择")),
                CardRow("伪装段位级别:",
                    ComboBox(SpoofTierLabels, spoofTierIndex, index => setSpoofTierIndex(index))
                        .Width(220).AutomationName("伪装段位选择")),
                CardRow("伪装小段级别 (Division):",
                    ComboBox(SpoofDivisionLabels, spoofDivisionIndex, index => setSpoofDivisionIndex(index))
                        .Width(180).IsEnabled(!isApexTier).AutomationName("伪装小段选择")),
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        Body("应用后好友列表中的名片即时显示此段位").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
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
            "在线状态切换", "在好友列表中切换你的展示状态（在线 / 离开 / 隐身）",
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
                }).IsEnabled(isLcuConnected).AutomationName("设为在线"),
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
                }).IsEnabled(isLcuConnected).AutomationName("设为离开"),
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
                }).IsEnabled(isLcuConnected).AutomationName("设为隐身")));

        Element clearBadgesAction = isConfirmingClearBadges
            ? (Element)HStack(6,
                Caption("🏅 确定清除所有勋章？").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center),
                Button(Caption("取消"), () => setIsConfirmingClearBadges(false)).Padding(6, 2).AutomationName("取消卸下勋章"),
                Button(Caption("确认卸下"), async () =>
                {
                    setIsConfirmingClearBadges(false);
                    try
                    {
                        await toolsService.ClearChallengeBadgesAsync();
                        ShowToast("已卸下展示的全部挑战勋章");
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"卸下勋章失败: {ex.Message}", isError: true);
                    }
                }).Background(Theme.Ref("SystemFillColorCriticalBrush")).Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")).Padding(6, 2).AutomationName("确认卸下勋章"))
            : (Element)Button("卸下全部勋章", () => setIsConfirmingClearBadges(true)).AutomationName("卸下勋章");

        Element clearBorderAction = isConfirmingClearBorder
            ? (Element)HStack(6,
                Caption("🖼️ 确定清除头像框？").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center),
                Button(Caption("取消"), () => setIsConfirmingClearBorder(false)).Padding(6, 2).AutomationName("取消卸下头像框"),
                Button(Caption("确认卸下"), async () =>
                {
                    setIsConfirmingClearBorder(false);
                    try
                    {
                        await toolsService.ClearProfileTokensAsync();
                        ShowToast("已卸下生涯头像框");
                    }
                    catch (Exception ex)
                    {
                        ShowToast($"卸下头像框失败: {ex.Message}", isError: true);
                    }
                }).Background(Theme.Ref("SystemFillColorCriticalBrush")).Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")).Padding(6, 2).AutomationName("确认卸下头像框"))
            : (Element)Button("卸下头像框", () => setIsConfirmingClearBorder(true)).AutomationName("卸下头像框");

        var badgesCard = ToolCard(
            "badges", "挑战勋章与头像框", "装配/卸下展示的挑战勋章、称号与头像框", "点击展开",
            false,
            VStack(10,
                HStack(8,
                    clearBadgesAction,
                    clearBorderAction
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
                    Caption($"配置错误: {error}").Foreground(Theme.Ref("SystemFillColorCriticalBrush")).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }
        else if (actionFeedback is not null)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(Theme.Accent),
                    Caption(actionFeedback).Foreground(Theme.Accent).VAlign(VerticalAlignment.Center)))
                .Padding(10).CornerRadius(6).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1);
        }
        else if (savedAt is { } time)
        {
            feedbackBanner = Border(
                HStack(12,
                    Border(Empty()).Width(3).Height(26).CornerRadius(2).Background(Theme.Accent),
                    Caption($"配置已保存 · {time:HH:mm:ss}").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)))
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
                                Caption(iconGlyph)
                                    .FontFamily("Segoe Fluent Icons")
                                    .Foreground(iconColor)
                                    .HAlign(HorizontalAlignment.Center)
                                    .VAlign(VerticalAlignment.Center)
                            )
                            .Width(22)
                            .Height(22)
                            .CornerRadius(11)
                            .Background(Theme.Ref("LayerFillColorDefaultBrush")),

                            Body(item.Message)
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

        Element? offlineBanner = null;
        if (!isLcuConnected)
        {
            offlineBanner = Border(
                HStack(8,
                    Body("\uE7BA")
                        .FontFamily("Segoe Fluent Icons")
                        .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                        .VAlign(VerticalAlignment.Center),
                    Caption("LCU 客户端未连接，本页所有开关与操作已停用，配置不可修改；启动并登录英雄联盟后自动恢复。")
                        .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                        .VAlign(VerticalAlignment.Center)
                )
            )
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .BorderBrush(Theme.Ref("SystemFillColorCriticalBrush"))
            .BorderThickness(1)
            .CornerRadius(6)
            .Padding(12, 8)
            .Margin(new Thickness(0, 0, 0, 8));
        }

        // 【A/B 实验】用传统 ScrollViewer 替代 WinUI 2.5 的 ScrollView。
        // 依据：同一窗口内，ScrollView 之外的元素（侧边栏图标、RenderPlaceholder 里的按钮）
        // 归一化边缘梯度 0.41~0.58，而 ScrollView 内的标题/说明只有 0.26~0.31，
        // 且与滚动位置无关（offset=0 时同样偏软）。判定为 ScrollPresenter 内容层渲染所致。
        var mainScroll = ScrollViewer(
            VStack(8,
                Title("其他功能")
                    .HeadingLevel(AutomationHeadingLevel.Level1)
                    .Margin(new Thickness(0, 0, 0, 10)),

                offlineBanner ?? Empty(),

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
            )
            // 正文列：显式宽度 + 居中（不要写成 MaxWidth + Stretch，原因见 ContentColumn 注释）。
            .Width(columnWidth)
            .HAlign(HorizontalAlignment.Center)
            .Padding(24)
            // 离线时不再整页降透明度：0.6 叠在 Mica + 半透明卡片上会把正文压成一片灰雾，
            // 12~14px 中文几乎不可读。禁用提示改由顶部离线横幅承担，卡片保持正常对比度。
        );

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
            ]
        );
    }
}
