using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 对局信息主页面（对应旧版 <c>GameInfo.vue</c>）。
/// 支持 5 列模式（左侧卡片 + 右侧战绩）与 10 列模式（全员 10 人并排透视）。
/// </summary>
public sealed class GameInfoPage : Component
{
    /// <summary>按队伍模式左侧玩家卡片之间的缝隙（卡片下间距，末张靠外层内边距补齐）。</summary>
    private const double CardGap = 4;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var gameInfoService = services.Services.GetRequiredService<IGameInfoService>();
        var lcu = services.Services.GetRequiredService<LcuConnection>();

        var nav = UseNavigation<AppRoute>();
        Action<string, SummonerProfile?> onNavigateCareer = (puuid, profile) =>
        {
            // 演示数据的 puuid 是占位值，跳过去只会查到一个不存在的召唤师
            if (gameInfoService.CurrentState.IsDemoData) return;
            nav.Replace(new AppRoute.CareerRoute(puuid, profile));
        };
        Action<string, string?> onNavigateSearch = (query, puuid) =>
        {
            if (gameInfoService.CurrentState.IsDemoData) return;
            nav.Replace(new AppRoute.SearchRoute(query, puuid));
        };

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

        var (state, setState) = UseState(gameInfoService.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = gameInfoService.StateStream.Subscribe(setState);
                return () => subscription.Dispose();
            },
            0);

        // 连接态必须用订阅值而不是 lcu.CurrentState：后者是快照，客户端启动或退出时
        // 本页不会重渲染，离线占位会一直卡在「进入本页那一刻」的状态。
        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        // 初次加载拉取一次状态
        UseEffect(
            () =>
            {
                _ = gameInfoService.RefreshStateAsync();
                return static () => { };
            },
            0);

        // 1. LCU 未连接：与生涯、战绩查询共用同一张离线占位，
        //    「预览演示对局」不依赖客户端，作为附加动作保留（旧系统此处没有该按钮）。
        //
        //    演示态必须放行：演示数据由 LoadDemoState 直接注入、不经过 LCU，
        //    若在这里一并拦下，离线时点完按钮，下一轮渲染又被这张占位顶回去，按钮形同虚设。
        bool isConnected = connState.Status == LcuConnectionStatus.Connected;
        if (!isConnected && !state.IsDemo)
        {
            return LcuOfflineState.Render(extra: DemoButton(() => gameInfoService.LoadDemoState()));
        }

        // 2. 云顶之弈模式
        if (state.IsTftMode)
        {
            return RenderPlaceholder(
                services.I18n.T("gameInfo.tftInGame", "云顶之弈对局中"),
                services.I18n.T("gameInfo.tftDesc", "云顶之弈模式不显示召唤师峡谷数据"),
                palette);
        }

        // 3. 错误状态
        if (!string.IsNullOrEmpty(state.Error))
        {
            return RenderPlaceholder(services.I18n.T("gameInfo.errorTitle", "数据读取异常"), state.Error, palette);
        }

        // 4. 等待对局
        if (!state.ShouldShowContent)
        {
            return RenderPlaceholder(
                services.I18n.T("gameInfo.awaitingLoadTitle", "等待对局开始"),
                services.I18n.T("gameInfo.awaitingLoadDesc", "进入选人阶段或对局中后将自动展示双方玩家数据"),
                palette,
                DemoButton(() => gameInfoService.LoadDemoState()));
        }

        // 5. 正在读取队伍
        if (state.IsLoadingTeams)
        {
            return RenderPlaceholder(
                services.I18n.T("gameInfo.loadingTeamsTitle", "正在读取对局玩家"),
                services.I18n.T("gameInfo.loadingTeamsDesc", "正在连接 LCU 获取对局双方玩家信息..."),
                palette);
        }

        // 6. 正常对局渲染
        Element content = state.ViewColumns == 10
            ? RenderTenColumnView(state, gameInfoService, palette, services, onNavigateSearch)
            : RenderFiveColumnView(state, gameInfoService, palette, services, onNavigateCareer, onNavigateSearch);

        if (!state.IsDemoData)
        {
            return content;
        }

        // 演示数据：顶部明示，避免用户以为示例召唤师可以查到真实战绩
        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Star()],
            [
                InfoBar(
                    services.I18n.T("gameInfo.demoTitle", "演示数据"),
                    services.I18n.T("gameInfo.demoDesc", "以上十位召唤师为示例，点击不会跳转生涯与战绩查询"))
                    .Informational()
                    .Margin(8, 8, 8, 0)
                    .Grid(row: 0),
                content.Grid(row: 1),
            ]);
    }

    /// <summary>
    /// 「预览演示对局」：读的是内置假数据，不经过 LCU，因此离线态与等待对局态都能用。
    /// 抽成独立构件是因为离线态改由 <see cref="LcuOfflineState"/> 承载，两处需要同一个按钮。
    /// </summary>
    private static Element DemoButton(Action onDemoClick)
    {
        var i18n = AppHost.Services?.I18n;
        var isEn = i18n?.CurrentLanguage == YuumiX.Core.Localization.SupportedLanguages.EnUs;
        var text = isEn ? "Demo" : (i18n?.T("gameInfo.previewDemo", "预览演示对局") ?? "预览演示对局");
        return Button(Caption(text), onDemoClick)
            .AutomationName(text)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Margin(0, 12, 0, 0)
            .HAlign(HorizontalAlignment.Center);
    }

    private static Element RenderPlaceholder(string title, string subtitle, YuumiPalette palette, Element? extra = null)
    {
        var items = new List<Element>
        {
            BodyLarge(title)
                .SemiBold()
                .Foreground(Theme.PrimaryText)
                .HAlign(HorizontalAlignment.Center),
            Caption(subtitle)
                .Foreground(Theme.SecondaryText)
                .HAlign(HorizontalAlignment.Center)
        };

        if (extra is not null)
        {
            items.Add(extra);
        }

        return Border(
            VStack(8, items.ToArray())
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        );
    }

    private Element RenderFiveColumnView(
        GameInfoState state,
        IGameInfoService service,
        YuumiPalette palette,
        AppServices services,
        Action<string, SummonerProfile?> onNavigateCareer,
        Action<string, string?> onNavigateSearch)
    {
        bool isMyTab = state.ActiveTab == "my";
        var currentTeam = isMyTab ? state.MyTeam : state.TheirTeam;
        var myGroups = PremadeAnalyzer.BuildPremadeGroups(state.MyTeam, state.PremadeColorsMy, state.PlayerData);
        var theirGroups = PremadeAnalyzer.BuildPremadeGroups(state.TheirTeam, state.PremadeColorsTheir, state.PlayerData);

        var allyLabelText = services.I18n.T("gameInfo.myTeam", "友方 ({count})").Replace("{count}", state.MyTeam.Count.ToString());
        var enemyLabelText = services.I18n.T("gameInfo.theirTeam", "敌方 ({count})").Replace("{count}", state.TheirTeam.Count.ToString());

        // 左侧队伍 Tab 切换器（带指示下划线与激活色彩）
        var allyTabBtn = Button(
            VStack(0,
                Caption(allyLabelText)
                    .Foreground(isMyTab ? Theme.AccentText : Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center),
                Border(Empty())
                    .Height(2)
                    .Background(isMyTab ? Theme.Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Margin(0, 4, 0, 0)
            ),
            () => service.SetActiveTab("my")
        )
        .AutomationName(allyLabelText)
        .Background(isMyTab ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
        .BorderThickness(new Thickness(0))
        .CornerRadius(YuumiTheme.SquareRadius)
        .Padding(0, 8, 0, 0)
        .HAlign(HorizontalAlignment.Stretch);

        var enemyTabBtn = Button(
            VStack(0,
                Caption(enemyLabelText)
                    .Foreground(!isMyTab ? Theme.AccentText : Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center),
                Border(Empty())
                    .Height(2)
                    .Background(!isMyTab ? Theme.Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Margin(0, 4, 0, 0)
            ),
            () => service.SetActiveTab("their")
        )
        .AutomationName(enemyLabelText)
        .Background(!isMyTab ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
        .BorderThickness(new Thickness(0))
        .CornerRadius(YuumiTheme.SquareRadius)
        .Padding(0, 8, 0, 0)
        .HAlign(HorizontalAlignment.Stretch);

        var tabHeader = Border(
            Grid(
                [GridSize.Star(), GridSize.Star()],
                [GridSize.Auto],
                [
                    allyTabBtn.Grid(row: 0, column: 0),
                    enemyTabBtn.Grid(row: 0, column: 1),
                ]
            )
        )
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .BorderBrush(Theme.CardStroke);

        // 左侧玩家卡片列表
        var cards = currentTeam.Select((p, idx) =>
        {
            var pData = FindPlayerData(p, state.PlayerData);
            int premadeIdx = PremadeAnalyzer.GetPlayerColorIdx(p, isMyTab ? state.PremadeColorsMy : state.PremadeColorsTheir);

            return Component<PlayerCardView, PlayerCardProps>(
                new PlayerCardProps(p, pData, premadeIdx, idx, palette, state.CurrentSummonerPuuid, state.SavedPlayerMap, onNavigateCareer, onNavigateSearch, state.IsDemoData))
                .WithKey(p.Puuid ?? idx.ToString());
        }).ToArray();

        // 5 张卡片垂直均分面板高度：原先按内容高度堆叠，卡片总高小于面板时下方留白；
        // 改为 Grid 等分行后每张卡片铺满一格（空队时占位，不建 0 行 Grid）。
        int cardCount = Math.Max(1, cards.Length);
        var cardRowDefs = Enumerable.Range(0, cardCount).Select(_ => GridSize.Star()).ToArray();

        Element cardsPanel = cards.Length == 0
            ? Border(Empty())
            : Grid([GridSize.Star()], cardRowDefs, cards.Select((c, i) => c.Margin(0, 0, 0, CardGap).Grid(row: i, column: 0).WithKey(i.ToString())).ToArray());
        // 卡片下间距形成缝隙；外层底部内边距取 0，与卡片下间距相加后顶/中/底间距一致
        cardsPanel = cardsPanel.Padding(CardGap, CardGap, CardGap, 0);

        var leftPanel = Border(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    tabHeader.Grid(row: 0),
                    cardsPanel.Grid(row: 1),
                ]
            )
        )
        .Width(250)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(YuumiTheme.OverlayRadius);

        // 右侧工具栏：• 友方 (N) + 组队芯片 + VS + 组队芯片 + • 敌方 (N) + 视图切换器
        bool mySideIsBlue = state.MapSide != "red";

        var allyPill = RenderTeamPill(
            allyLabelText,
            mySideIsBlue,
            isClickable: true,
            onClick: () => service.SetActiveTab("my"),
            isActive: isMyTab);

        var enemyPill = RenderTeamPill(
            enemyLabelText,
            !mySideIsBlue,
            isClickable: true,
            onClick: () => service.SetActiveTab("their"),
            isActive: !isMyTab);

        var myChips = Component<PremadeGroupChipsView, PremadeGroupChipsProps>(
            new PremadeGroupChipsProps(myGroups));

        var theirChips = Component<PremadeGroupChipsView, PremadeGroupChipsProps>(
            new PremadeGroupChipsProps(theirGroups));

        var vsBadge = RenderVsBadge(palette);

        var segmentedControl = RenderSegmentedControl(5, cols => service.SetViewColumns(cols), palette);

        var toolbarLeft = HStack(8,
            allyPill,
            myChips,
            vsBadge,
            theirChips,
            enemyPill
        )
        .VAlign(VerticalAlignment.Center);

        var toolbarRight = HStack(0,
            segmentedControl
        ).VAlign(VerticalAlignment.Center);

        var toolbar = Border(
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    toolbarLeft.Grid(row: 0, column: 0),
                    toolbarRight.Grid(row: 0, column: 1),
                ]
            )
        )
        .Height(44)
        .Padding(12, 4, 12, 4)
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .BorderBrush(Theme.CardStroke);

        // 右侧 5 列战绩（均分宽度）
        var matchColumns = currentTeam.Select((p, idx) =>
        {
            var pData = FindPlayerData(p, state.PlayerData);
            int premadeIdx = PremadeAnalyzer.GetPlayerColorIdx(p, isMyTab ? state.PremadeColorsMy : state.PremadeColorsTheir);

            return Component<PlayerMatchColumnView, PlayerMatchColumnProps>(
                new PlayerMatchColumnProps(
                    p,
                    pData,
                    premadeIdx,
                    5,
                    palette,
                    state.CurrentSummonerPuuid,
                    isMyTab ? "ally" : "enemy",
                    state.MapSide,
                    false,
                    idx == currentTeam.Count - 1,
                    state.SavedPlayerMap,
                    onNavigateSearch,
                    state.IsDemoData))
                .Grid(row: 0, column: idx);
        }).ToArray();

        var colDefs = Enumerable.Range(0, Math.Max(5, currentTeam.Count))
            .Select(_ => GridSize.Star())
            .ToArray();

        var columnsPanel = Grid(colDefs, [GridSize.Star()], matchColumns);

        var rightPanel = Border(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    toolbar.Grid(row: 0),
                    columnsPanel.Grid(row: 1),
                ]
            )
        )
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(YuumiTheme.OverlayRadius);

        return Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Star()],
            [
                leftPanel.Grid(row: 0, column: 0).Margin(0, 0, 12, 0),
                rightPanel.Grid(row: 0, column: 1),
            ]
        )
        .Padding(8);
    }

    private Element RenderTenColumnView(
        GameInfoState state,
        IGameInfoService service,
        YuumiPalette palette,
        AppServices services,
        Action<string, string?> onNavigateSearch)
    {
        var myGroups = PremadeAnalyzer.BuildPremadeGroups(state.MyTeam, state.PremadeColorsMy, state.PlayerData);
        var theirGroups = PremadeAnalyzer.BuildPremadeGroups(state.TheirTeam, state.PremadeColorsTheir, state.PlayerData);

        bool mySideIsBlue = state.MapSide != "red";

        var allyLabelText = services.I18n.T("gameInfo.myTeam", "友方 ({count})").Replace("{count}", state.MyTeam.Count.ToString());
        var enemyLabelText = services.I18n.T("gameInfo.theirTeam", "敌方 ({count})").Replace("{count}", state.TheirTeam.Count.ToString());

        var allyPill = RenderTeamPill(
            allyLabelText,
            mySideIsBlue);

        var enemyPill = RenderTeamPill(
            enemyLabelText,
            !mySideIsBlue);

        var myChips = Component<PremadeGroupChipsView, PremadeGroupChipsProps>(
            new PremadeGroupChipsProps(myGroups));

        var theirChips = Component<PremadeGroupChipsView, PremadeGroupChipsProps>(
            new PremadeGroupChipsProps(theirGroups));

        var vsBadge = RenderVsBadge(palette);

        var segmentedControl = RenderSegmentedControl(10, cols => service.SetViewColumns(cols), palette);

        var toolbar = Border(
            Grid(
                [GridSize.Star(), GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                [
                    HStack(8, allyPill, myChips)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),

                    vsBadge.Grid(row: 0, column: 1).VAlign(VerticalAlignment.Center),

                    HStack(8,
                        theirChips,
                        enemyPill,
                        segmentedControl
                    )
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 2),
                ]
            )
        )
        .Height(44)
        .Padding(12, 4, 12, 4)
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .BorderBrush(Theme.CardStroke);

        // 10 列战绩（我方 5 列 + 敌方 5 列）
        var colElements = new List<Element>();

        for (int i = 0; i < state.MyTeam.Count; i++)
        {
            var p = state.MyTeam[i];
            var pData = FindPlayerData(p, state.PlayerData);
            int premadeIdx = PremadeAnalyzer.GetPlayerColorIdx(p, state.PremadeColorsMy);
            bool isTeamSeparator = i == state.MyTeam.Count - 1;

            var col = Component<PlayerMatchColumnView, PlayerMatchColumnProps>(
                new PlayerMatchColumnProps(
                    p,
                    pData,
                    premadeIdx,
                    10,
                    palette,
                    state.CurrentSummonerPuuid,
                    "ally",
                    state.MapSide,
                    isTeamSeparator,
                    false,
                    state.SavedPlayerMap,
                    onNavigateSearch,
                    state.IsDemoData))
                .Grid(row: 0, column: i);

            colElements.Add(col);
        }

        int offset = state.MyTeam.Count;
        for (int i = 0; i < state.TheirTeam.Count; i++)
        {
            var p = state.TheirTeam[i];
            var pData = FindPlayerData(p, state.PlayerData);
            int premadeIdx = PremadeAnalyzer.GetPlayerColorIdx(p, state.PremadeColorsTheir);
            bool isLast = i == state.TheirTeam.Count - 1;

            var col = Component<PlayerMatchColumnView, PlayerMatchColumnProps>(
                new PlayerMatchColumnProps(
                    p,
                    pData,
                    premadeIdx,
                    10,
                    palette,
                    state.CurrentSummonerPuuid,
                    "enemy",
                    state.MapSide,
                    false,
                    isLast,
                    state.SavedPlayerMap,
                    onNavigateSearch,
                    state.IsDemoData))
                .Grid(row: 0, column: offset + i);

            colElements.Add(col);
        }

        int totalCount = Math.Max(10, state.MyTeam.Count + state.TheirTeam.Count);
        var colDefs = Enumerable.Range(0, totalCount)
            .Select(_ => GridSize.Star())
            .ToArray();

        var columnsPanel = Grid(colDefs, [GridSize.Star()], colElements.ToArray());

        var fullPanel = Border(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    toolbar.Grid(row: 0),
                    columnsPanel.Grid(row: 1),
                ]
            )
        )
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(YuumiTheme.OverlayRadius);

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                fullPanel.Grid(row: 0, column: 0),
            ]
        )
        .Padding(8);
    }

    private static Element RenderTeamPill(
        string text,
        bool isBlueSide,
        bool isClickable = false,
        Action? onClick = null,
        bool isActive = false)
    {
        string dotColor = isBlueSide ? AssetColors.TeamBlue : AssetColors.TeamRed;
        string bgColor = isBlueSide ? YuumiPalette.WithAlpha(AssetColors.TeamBlue, 0x20) : YuumiPalette.WithAlpha(AssetColors.TeamRed, 0x20);
        string borderColor = isBlueSide ? YuumiPalette.WithAlpha(AssetColors.TeamBlue, 0x40) : YuumiPalette.WithAlpha(AssetColors.TeamRed, 0x40);
        string fgColor = isBlueSide ? AssetColors.TeamBlue : AssetColors.TeamRed;

        var dot = Border(Empty())
            .Width(5)
            .Height(5)
            .CornerRadius(YuumiTheme.PillRadius)
            .Background(dotColor)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 4, 0);

        var label = Caption(text)
            .Foreground(fgColor)
            .VAlign(VerticalAlignment.Center);

        var content = HStack(0, dot, label);

        if (isClickable && onClick != null)
        {
            return Button(content, onClick)
                .AutomationName(text)
                .Background(bgColor)
                .BorderBrush(borderColor)
                .BorderThickness(new Thickness(1))
                .CornerRadius(YuumiTheme.PillRadius)
                .Padding(8, 4)
                .Height(26);
        }

        return Border(content)
            .Background(bgColor)
            .BorderBrush(borderColor)
            .BorderThickness(new Thickness(1))
            .CornerRadius(YuumiTheme.PillRadius)
            .Padding(8, 4);
    }

    private static Element RenderVsBadge(YuumiPalette palette)
    {
        return Badge.Create(
                Caption("VS")
                    .SemiBold()
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
                Theme.SubtleFill,
                Theme.CardStroke,
                padH: 7,
                padV: 2);
    }

    private static Element RenderSegmentedControl(int currentColumns, Action<int> onSelect, YuumiPalette palette)
    {
        bool isFive = currentColumns == 5;
        var i18n = AppHost.Services?.I18n;
        var showTeamText = i18n?.T("gameInfo.showTeam", "按队伍") ?? "按队伍";
        var showAllText = i18n?.T("gameInfo.showAll", "全部战绩") ?? "全部战绩";

        var teamContent = HStack(4,
            Caption(Icons.People)
                .FontFamily(Icons.FontFamily)
                .Foreground(isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
            Caption(showTeamText)
                .Foreground(isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
        );

        var teamBtn = (isFive
            ? Button(teamContent, () => onSelect(5)).AccentButton()
            : Button(teamContent, () => onSelect(5)).SubtleButton())
            .AutomationName("按队伍模式")
            .BorderThickness(new Thickness(0))
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8, 4)
            .Height(28);

        var allContent = HStack(4,
            Caption(Icons.AllMatches)
                .FontFamily(Icons.FontFamily)
                .Foreground(!isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
            Caption(showAllText)
                .Foreground(!isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
        );

        var allBtn = (!isFive
            ? Button(allContent, () => onSelect(10)).AccentButton()
            : Button(allContent, () => onSelect(10)).SubtleButton())
            .AutomationName("全部战绩模式")
            .BorderThickness(new Thickness(0))
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8, 4)
            .Height(28);

        return Border(
            HStack(2, teamBtn, allBtn)
        )
        .Background(Theme.SubtleFill)
        .BorderBrush(Theme.CardStroke)
        .BorderThickness(new Thickness(1))
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Padding(0);
    }

    private static GameInfoPlayer? FindPlayerData(
        PremadePlayerLike p,
        IReadOnlyDictionary<string, GameInfoPlayer> playerData)
    {
        if (!string.IsNullOrEmpty(p.Puuid) && playerData.TryGetValue(p.Puuid, out var byPuuid))
        {
            return byPuuid;
        }

        if (p.SummonerId != 0 && playerData.TryGetValue(p.SummonerId.ToString(), out var bySid))
        {
            return bySid;
        }

        if (playerData.TryGetValue(p.CellId.ToString(), out var byCell))
        {
            return byCell;
        }

        return null;
    }
}

