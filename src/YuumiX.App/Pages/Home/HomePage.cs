using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Navigation;
using YuumiX.App.Pages.Career;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core;
using YuumiX.Core.Automation;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Localization;

namespace YuumiX.App.Pages.Home;

/// <summary>
/// 召唤师与单双排位简略数据。
/// </summary>
internal sealed record HomeSummary(
    SummonerProfile? Profile,
    RankedQueueEntry? SoloDuo);

/// <summary>
/// 主页视图：根据 LCU 连接状态呈现。
///
/// 连接成功时呈现：
///   - 统一页头 PageHeader
///   - 召唤师概览卡（头像、等级、昵称、单双段位）
///   - 游戏阶段与 LCU 运行参数卡
///   - 常用功能快捷入口网格
///   - 右下角装饰 Logo（无障碍隐藏）
///
/// 未连接时：
///   - 居中品牌 Logo
///   - 客户端未连接提示与描述
///   - 启动客户端按钮与等待客户端启动状态环
/// </summary>
public sealed class HomePage : Component
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var lcu = services.Services.GetRequiredService<LcuConnection>();
        var automationHost = services.Services.GetRequiredService<AutomationHost>();
        var nav = UseNavigation<AppRoute>();
        var i18n = services.I18n;

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        var (lang, setLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setLang);
                return () => subscription.Dispose();
            },
            0);

        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        var (gamePhase, setGamePhase) = UseState(automationHost.CurrentPhase, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = automationHost.Phases.Subscribe(setGamePhase);
                return () => subscription.Dispose();
            },
            0);

        var isConnected = connState.Status == LcuConnectionStatus.Connected;
        if (!isConnected)
        {
            return LcuOfflineState.RenderHome(() => LaunchClient(services));
        }

        var homeResource = UseResource(
            async ct =>
            {
                var summonerService = services.Services.GetRequiredService<ISummonerService>();
                var careerService = services.Services.GetRequiredService<ICareerService>();
                var logger = services.LoggerFactory.CreateLogger<HomePage>();

                SummonerProfile? profile = null;
                try
                {
                    profile = await summonerService.GetCurrentSummonerAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "获取主页召唤师信息失败");
                }

                RankedQueueEntry? soloDuo = null;
                if (profile is not null && !string.IsNullOrEmpty(profile.Puuid))
                {
                    try
                    {
                        var rankedStats = await careerService.GetRankedStatsAsync(profile.Puuid, cancellationToken: ct).ConfigureAwait(false);
                        soloDuo = rankedStats.SoloDuo;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "获取主页单双排位数据失败");
                    }
                }

                return new HomeSummary(profile, soloDuo);
            },
            deps: [connState.Status]);

        return RenderConnectedView(connState, gamePhase, homeResource, nav, i18n, palette);
    }

    /// <summary>
    /// 连接成功态视图：统一页头 + 概览卡 + 游戏阶段卡 + 快捷入口。
    /// </summary>
    private static Element RenderConnectedView(
        LcuConnectionState connState,
        GamePhase gamePhase,
        AsyncValue<HomeSummary> homeResource,
        NavigationHandle<AppRoute> nav,
        I18nService i18n,
        YuumiPalette palette)
    {
        var title = i18n.T("home.title", "主页");
        var description = i18n.T("home.description", "查看当前召唤师状态、游戏阶段与常用功能快捷直达");

        var pageHeader = PageHeaders.PageHeader(
            title,
            description,
            badge: Badge.Create(
                Caption(i18n.T("home.connected", "客户端连接成功"))
                    .Foreground(Theme.SystemSuccess),
                Theme.SubtleFill,
                Theme.ControlStroke,
                padH: 8,
                padV: 2));

        var mainContent = VStack(
            20,
            pageHeader,
            RenderSummonerCard(homeResource, nav, i18n),
            RenderGamePhaseCard(gamePhase, connState, i18n),
            RenderQuickAccess(nav, i18n)
        );

        var scrollContent = ScrollViewer(
            mainContent.Padding(0, 0, 16, 32)
        )
        .IsTabStop(true);

        // 右下角猫咪 Logo（无障碍合规：隐藏且不可点）
        var cornerLogo = Image(AppAssets.LogoUri)
            .Width(130)
            .Height(130)
            .Opacity(0.85)
            .AccessibilityHidden()
            .IsHitTestVisible(false)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .VerticalAlignment(VerticalAlignment.Bottom)
            .Margin(0, 0, 36, 36);

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            scrollContent,
            cornerLogo
        )
        .Padding(32, 24);
    }

    /// <summary>
    /// 召唤师概览卡片。
    /// </summary>
    private static Element RenderSummonerCard(
        AsyncValue<HomeSummary> homeResource,
        NavigationHandle<AppRoute> nav,
        I18nService i18n)
    {
        if (homeResource is AsyncValue<HomeSummary>.Loading)
        {
            return Card(
                HStack(
                    16,
                    ProgressRing().Width(32).Height(32).VAlign(VerticalAlignment.Center),
                    Body(i18n.T("career.loading", "正在获取召唤师信息...")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                ).Padding(8)
            );
        }

        var summary = homeResource switch
        {
            AsyncValue<HomeSummary>.Data data => data.Value,
            _ => null,
        };

        var profile = summary?.Profile;
        if (profile is null)
        {
            return Card(
                HStack(
                    12,
                    Body(Icons.Warning).FontFamily(Icons.FontFamily).Foreground(Theme.SystemCaution).VAlign(VerticalAlignment.Center),
                    Body("未能读取到当前登录召唤师数据").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
                ).Padding(8)
            );
        }

        var soloDuo = summary?.SoloDuo;
        string mainName = !string.IsNullOrWhiteSpace(profile.GameName)
            ? profile.GameName
            : (!string.IsNullOrWhiteSpace(profile.DisplayName)
                ? profile.DisplayName
                : profile.RiotId);

        // 正圆头像
        const double avatarSize = 64;
        Element avatar = Component<LcuImage, LcuImageProps>(
            new LcuImageProps(
                CareerVisuals.ProfileIcon(profile.ProfileIconId),
                avatarSize,
                avatarSize / 2));

        // 等级徽章
        Element levelBadge = Badge.Create(
            Caption($"Lv.{profile.SummonerLevel}")
                .SemiBold()
                .Foreground(Theme.PrimaryText)
                .HAlign(HorizontalAlignment.Center),
            Theme.CardBackground,
            Theme.CardStroke,
            padH: 8,
            padV: 2);

        var avatarStack = VStack(
            4,
            avatar.HAlign(HorizontalAlignment.Center),
            levelBadge.HAlign(HorizontalAlignment.Center)
        ).VAlign(VerticalAlignment.Center);

        // 昵称与段位信息
        var nameRow = HStack(
            8,
            Subtitle(mainName)
                .HeadingLevel(AutomationHeadingLevel.Level2)
                .VAlign(VerticalAlignment.Center),
            !string.IsNullOrEmpty(profile.TagLine)
                ? Body($"#{profile.TagLine}")
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                : Empty()
        );

        Element rankInfo;
        if (soloDuo is not null && soloDuo.HasRank)
        {
            rankInfo = HStack(
                8,
                CareerVisuals.RankedMedal(soloDuo.Tier, 22).VAlign(VerticalAlignment.Center),
                BodyStrong(soloDuo.TierText).VAlign(VerticalAlignment.Center),
                Caption($"{soloDuo.LeaguePoints} LP").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                Caption($"胜率 {soloDuo.WinRateText} ({soloDuo.Wins}胜 {soloDuo.Losses}负)").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
            );
        }
        else
        {
            rankInfo = HStack(
                8,
                CareerVisuals.RankedMedal(null, 22).VAlign(VerticalAlignment.Center),
                Body(i18n.T("home.unranked", "单双排位：未定级")).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center)
            );
        }

        var infoColumn = VStack(
            6,
            nameRow,
            rankInfo
        ).VAlign(VerticalAlignment.Center);

        // 查看生涯入口
        var viewCareerBtn = Button(
            HStack(
                4,
                Body(i18n.T("home.viewCareer", "查看生涯")),
                Caption(Icons.ChevronRight).FontFamily(Icons.FontFamily).VAlign(VerticalAlignment.Center)
            ),
            () => nav.Replace(new AppRoute.CareerRoute())
        )
        .VAlign(VerticalAlignment.Center)
        .AutomationName("查看完整生涯");

        return Card(
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                avatarStack.Grid(column: 0).Margin(0, 0, 20, 0),
                infoColumn.Grid(column: 1),
                viewCareerBtn.Grid(column: 2)
            )
        );
    }

    /// <summary>
    /// 游戏阶段与 LCU 运行卡片。
    /// </summary>
    private static Element RenderGamePhaseCard(
        GamePhase gamePhase,
        LcuConnectionState connState,
        I18nService i18n)
    {
        var phaseTitle = i18n.T("phase." + gamePhase, null) ?? (gamePhase switch
        {
            GamePhase.None => "空闲",
            GamePhase.Lobby => "房间组队中",
            GamePhase.Matchmaking => "匹配中",
            GamePhase.ReadyCheck => "确认对局",
            GamePhase.ChampSelect => "选择英雄",
            GamePhase.GameStart => "游戏加载",
            GamePhase.InProgress => "游戏中",
            GamePhase.Reconnect => "重新连接",
            GamePhase.WaitingForStats => "等待数据",
            GamePhase.PreEndOfGame => "结算中",
            GamePhase.EndOfGame => "对局结束",
            GamePhase.WatchInProgress => "观战中",
            _ => gamePhase.ToString(),
        });

        var phaseDescription = gamePhase switch
        {
            GamePhase.None or GamePhase.Unknown => i18n.T("home.phaseIdle", "当前客户端处于空闲等待状态"),
            GamePhase.Lobby => "已进入游戏房间，可准备对局或组队匹配",
            GamePhase.Matchmaking => "正在寻找对局，找到后将自动弹出确认",
            GamePhase.ReadyCheck => "对局已就绪，请在规定时间内完成确认",
            GamePhase.ChampSelect => "英雄选择阶段，请留意预选阵容与出装符文",
            GamePhase.GameStart or GamePhase.InProgress => "游戏对局正在进行中",
            GamePhase.Reconnect => "游戏意外中断，请尝试重新连接游戏服务器",
            GamePhase.PreEndOfGame or GamePhase.EndOfGame or GamePhase.WaitingForStats => "本场对局已结束，正在同步结算统计数据",
            _ => "客户端运行中",
        };

        var creds = connState.Credentials;
        var pid = creds?.ProcessId ?? 0;
        var port = creds?.Port ?? 0;

        var phaseIcon = gamePhase switch
        {
            GamePhase.InProgress or GamePhase.GameStart => Icons.CheckMark,
            GamePhase.ChampSelect => Icons.GameInfo,
            GamePhase.Reconnect => Icons.Warning,
            _ => Icons.Home,
        };

        var leftContent = HStack(
            16,
            Border(
                Body(phaseIcon)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.Accent)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(44)
            .Height(44)
            .Background(Theme.SubtleFill)
            .CornerRadius(YuumiTheme.ControlRadius)
            .VAlign(VerticalAlignment.Center),

            VStack(
                4,
                HStack(
                    8,
                    Subtitle(i18n.T("home.gamePhase", "游戏阶段") + "：" + phaseTitle)
                        .HeadingLevel(AutomationHeadingLevel.Level2)
                        .VAlign(VerticalAlignment.Center),
                    Badge.Create(
                        Caption("进行中").Foreground(Theme.Accent),
                        Theme.SubtleFill,
                        Theme.ControlStroke,
                        padH: 6,
                        padV: 2)
                ),
                Body(phaseDescription).Foreground(Theme.SecondaryText)
            ).VAlign(VerticalAlignment.Center)
        );

        var rightParams = VStack(
            4,
            HStack(
                12,
                Caption($"PID: {pid}")
                    .FontFamily(Icons.MonoFontFamily)
                    .Foreground(Theme.SecondaryText),
                Caption($"Port: {port}")
                    .FontFamily(Icons.MonoFontFamily)
                    .Foreground(Theme.SecondaryText)
            ).HorizontalAlignment(HorizontalAlignment.Right),
            Badge.Create(
                HStack(
                    4,
                    Caption("●").Foreground(Theme.SystemSuccess).VAlign(VerticalAlignment.Center),
                    Caption("LCU 在线").Foreground(Theme.PrimaryText).VAlign(VerticalAlignment.Center)
                ),
                Theme.SubtleFill,
                Theme.ControlStroke,
                padH: 8,
                padV: 2
            ).HorizontalAlignment(HorizontalAlignment.Right)
        ).VAlign(VerticalAlignment.Center);

        return Card(
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                leftContent.Grid(column: 0),
                rightParams.Grid(column: 1)
            )
        );
    }

    /// <summary>
    /// 快捷入口网格。
    /// </summary>
    private static Element RenderQuickAccess(
        NavigationHandle<AppRoute> nav,
        I18nService i18n)
    {
        var sectionTitle = Subtitle(i18n.T("home.quickAccess", "快捷入口"))
            .HeadingLevel(AutomationHeadingLevel.Level2);

        var tileCareer = QuickAccessTile(
            Icons.Career,
            i18n.T("nav.career", "我的生涯"),
            i18n.T("home.careerDesc", "查看历史战绩、战利品与熟练度"),
            () => nav.Replace(new AppRoute.CareerRoute()));

        var tileSearch = QuickAccessTile(
            Icons.Search,
            i18n.T("nav.search", "战绩查询"),
            i18n.T("home.searchDesc", "搜索召唤师并分析对局详情"),
            () => nav.Replace(new AppRoute.SearchRoute()));

        var tileGameInfo = QuickAccessTile(
            Icons.GameInfo,
            i18n.T("nav.gameinfo", "对局信息"),
            i18n.T("home.gameInfoDesc", "选人与对局中实时看板"),
            () => nav.Replace(new AppRoute.GameInfoRoute()));

        var tileOpgg = QuickAccessTile(
            Icons.Opgg,
            i18n.T("nav.opgg", "OP.GG"),
            i18n.T("home.opggDesc", "英雄出装、符文与克制排行"),
            () => OpggWindowHost.ShowOrActivate());

        var tileTools = QuickAccessTile(
            Icons.Tools,
            i18n.T("nav.tools", "实用工具"),
            i18n.T("home.toolsDesc", "自动接受、自动选人与实用工具"),
            () => nav.Replace(new AppRoute.ToolsRoute()));

        var grid = (Grid(
            [GridSize.Star(), GridSize.Star(), GridSize.Star()],
            [GridSize.Auto, GridSize.Auto],
            tileCareer.Grid(row: 0, column: 0),
            tileSearch.Grid(row: 0, column: 1),
            tileGameInfo.Grid(row: 0, column: 2),
            tileOpgg.Grid(row: 1, column: 0),
            tileTools.Grid(row: 1, column: 1)
        ) with
        {
            RowSpacing = 12,
            ColumnSpacing = 12,
        });

        return VStack(
            12,
            sectionTitle,
            grid
        );
    }

    /// <summary>
    /// 快捷入口磁贴按钮。
    /// </summary>
    private static Element QuickAccessTile(
        string icon,
        string title,
        string description,
        Action onClick)
    {
        return Button(
            VStack(
                6,
                Body(icon)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.Accent)
                    .HAlign(HorizontalAlignment.Left),
                BodyStrong(title)
                    .HAlign(HorizontalAlignment.Left),
                Caption(description)
                    .Foreground(Theme.SecondaryText)
                    .TextWrapping(TextWrapping.Wrap)
                    .HAlign(HorizontalAlignment.Left)
            ).Padding(16, 12),
            onClick
        )
        .HAlign(HorizontalAlignment.Stretch)
        .HorizontalContentAlignment(HorizontalAlignment.Stretch)
        .CornerRadius(YuumiTheme.OverlayRadius)
        .AutomationName(title);
    }

    /// <summary>
    /// 启动客户端：走 Core 的 <see cref="LolClientLauncher"/>。
    /// </summary>
    private static void LaunchClient(AppServices services)
    {
        var launcher = services.Services.GetRequiredService<YuumiX.Core.Features.Tools.LolClientLauncher>();
        var logger = services.LoggerFactory.CreateLogger<YuumiX.Core.Features.Tools.LolClientLauncher>();
        var config = services.Config;
        _ = Task.Run(() =>
        {
            string? error = launcher.Launch(config.General.LolPath, config.General.WegamePath);
            if (error is not null)
            {
                logger.LogWarning("手动启动客户端失败: {Error}", error);
            }
        });
    }
}
