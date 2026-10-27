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
    /// <summary>
    /// 按队伍模式左侧玩家卡片之间的缝隙（卡片下间距，末张靠外层内边距补齐）。
    /// 对齐旧版 <c>.player-list</c> 的 gap 8。
    /// </summary>
    private const double CardGap = 8;

    /// <summary>队伍页签高度：旧版 <c>.tab-btn</c> 为 padding 14 + 文案 + 2px 下划线 ≈ 46，取 44。</summary>
    private const double TeamTabHeight = 44;

    /// <summary>阵营胶囊高度：沿用网格改造前的 26（配 <see cref="TeamPillPadV"/> 的垂直内边距）。</summary>
    private const double TeamPillHeight = 26;

    /// <summary>阵营胶囊垂直内边距：胶囊保留垂直留白，不压成 0，高度由固定高 + 居中承载。</summary>
    private const double TeamPillPadV = 3;

    /// <summary>视图分段按钮高度：沿用网格改造前的 28（配 <see cref="SegmentButtonPadV"/>）。</summary>
    private const double SegmentButtonHeight = 28;

    /// <summary>视图分段按钮垂直内边距：同上，保留垂直留白。</summary>
    private const double SegmentButtonPadV = 4;

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

        // 窗口宽度（DIP）。旧版 `.game-layout` 在 `@media (max-width: 1100px)` 时把
        // 「250px + 1fr」两列改成上下堆叠（上 34vh / 下 1fr）；不还原这条规则的话，
        // 窗口缩窄后右侧 5 列会被压到每列 100 出头，卡片内文字全部挤压换行。
        //
        // 必须订阅**窗口**级 SizeChanged，而不是页面元素的 OnSizeChanged：
        // 5 列/10 列视图来回切换时页面元素会被池复用，元素级事件在复用路径上不再触发，
        // 断点会停留在「上一次非复用渲染」时的宽度，缩窗后仍按宽屏排版。
        // 顺带断点口径也回到旧版原值 1100（窗口宽度，不是内容区宽度）。
        var window = UseWindow();
        var (windowWidth, setWindowWidth) = UseState(0.0, threadSafe: true);
        UseEffect(
            () =>
            {
                if (window is null)
                {
                    return static () => { };
                }

                void OnWindowSizeChanged(object? sender, WindowDipSizeChangedEventArgs args) =>
                    setWindowWidth(args.Size.Item1);

                // 初值取客户区宽（物理像素 ÷ DIP 缩放），避免「首次进入页面时窗口已经是窄的」
                // 要等一次尺寸变化才切堆叠。
                setWindowWidth(window.AppWindow.ClientSize.Width / window.DipScale);
                window.SizeChanged += OnWindowSizeChanged;
                return () => window.SizeChanged -= OnWindowSizeChanged;
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
            return RenderPlaceholder("云顶之弈对局中", "云顶之弈模式不显示召唤师峡谷数据", palette);
        }

        // 3. 错误状态
        if (!string.IsNullOrEmpty(state.Error))
        {
            return RenderPlaceholder("数据读取异常", state.Error, palette);
        }

        // 4. 等待对局
        if (!state.ShouldShowContent)
        {
            return RenderPlaceholder(
                "等待对局开始",
                "进入选人阶段或对局中后将自动展示双方玩家数据",
                palette,
                DemoButton(() => gameInfoService.LoadDemoState()));
        }

        // 5. 正在读取队伍
        if (state.IsLoadingTeams)
        {
            return RenderPlaceholder("正在读取对局玩家", "正在连接 LCU 获取对局双方玩家信息...", palette);
        }

        // 6. 正常对局渲染
        //    演示态只影响数据来源，不再额外加顶部提示条：提示条会占掉 40 DIP 的页面高度，
        //    把右栏战绩区压短一截。演示数据本身已经在按钮文案与点击行为上可辨识。
        return state.ViewColumns == 10
            ? RenderTenColumnView(state, gameInfoService, palette, services, onNavigateSearch)
            : RenderFiveColumnView(state, gameInfoService, palette, services, onNavigateCareer, onNavigateSearch, windowWidth);
    }

    /// <summary>
    /// 「预览演示对局」：读的是内置假数据，不经过 LCU，因此离线态与等待对局态都能用。
    /// 抽成独立构件是因为离线态改由 <see cref="LcuOfflineState"/> 承载，两处需要同一个按钮。
    /// </summary>
    private static Element DemoButton(Action onDemoClick) =>
        Button(Caption("预览演示对局"), onDemoClick)
            .AutomationName("预览演示对局")
            .CornerRadius(YuumiTheme.ControlRadius)
            .Margin(0, 12, 0, 0)
            .HAlign(HorizontalAlignment.Center);

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
        Action<string, string?> onNavigateSearch,
        double windowWidth)
    {
        bool isMyTab = state.ActiveTab == "my";
        var currentTeam = isMyTab ? state.MyTeam : state.TheirTeam;
        var myGroups = PremadeAnalyzer.BuildPremadeGroups(state.MyTeam, state.PremadeColorsMy, state.PlayerData);
        var theirGroups = PremadeAnalyzer.BuildPremadeGroups(state.TheirTeam, state.PremadeColorsTheir, state.PlayerData);

        // 左侧队伍 Tab 切换器（带指示下划线与激活色彩）
        // 结构对齐旧版 `.tab-btn`：44 高的等宽格子 + 贴底 2px 指示线（旧版用 border-bottom 实现），
        // 而不是「文字下方紧跟一条线」——后者会把整条页签压到 30 高。
        var allyTabBtn = Button(
            TeamTabContent($"友方 ({state.MyTeam.Count})", isMyTab),
            () => service.SetActiveTab("my")
        )
        .AutomationName($"友方队伍 ({state.MyTeam.Count})")
        .Background(isMyTab ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
        .BorderThickness(new Thickness(0))
        .CornerRadius(YuumiTheme.SquareRadius)
        .Padding(0)
        .Height(TeamTabHeight)
        .HAlign(HorizontalAlignment.Stretch);

        var enemyTabBtn = Button(
            TeamTabContent($"敌方 ({state.TheirTeam.Count})", !isMyTab),
            () => service.SetActiveTab("their")
        )
        .AutomationName($"敌方队伍 ({state.TheirTeam.Count})")
        .Background(!isMyTab ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
        .BorderThickness(new Thickness(0))
        .CornerRadius(YuumiTheme.SquareRadius)
        .Padding(0)
        .Height(TeamTabHeight)
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

        // 旧版 `.player-list` 是 flex column + `gap: 8px`；本版要求 5 张卡**等高铺满**整列
        // （卡片高度 = 面板可用高 / 5），不能按内容高度堆叠——那样底部会留一大块空白。
        // 做法是 Grid 的 Star 行等分：每行权重相同，卡片随面板高度伸缩。
        //
        // 窄窗改上下堆叠后左面板只剩 34% 高度，5 等分会把卡片压到 30 出头、内容全部溢出，
        // 这种情形改回内容堆叠 + 滚动（与旧版 `.player-list { overflow-y: auto }` 一致）。
        const double StackBreakpoint = 1100;
        bool stacked = windowWidth > 0 && windowWidth < StackBreakpoint;

        Element cardsPanel;
        if (cards.Length == 0)
        {
            cardsPanel = Border(Empty());
        }
        else if (stacked)
        {
            // 只在堆叠态挂 ScrollViewer：宽屏交给外层 Grid 直接排版，卡片文本能按父宽触发省略号；
            // 一旦包住 ScrollViewer，子元素在测量阶段会拿到无限宽度，省略号失效、文字溢出后被裁成半截。
            cardsPanel = ScrollViewer(VStack(CardGap, cards).VAlign(VerticalAlignment.Top))
                .HorizontalScrollMode(Microsoft.UI.Xaml.Controls.ScrollMode.Disabled)
                // 旧版 `.player-list` padding 10×8：纵向 10 取 8（4px 网格），横向照抄 8。
                .Padding(CardGap, CardGap, CardGap, CardGap);
        }
        else
        {
            var cardRows = Enumerable.Range(0, cards.Length).Select(_ => GridSize.Star()).ToArray();
            cardsPanel = Grid(
                [GridSize.Star()],
                cardRows,
                cards.Select((c, i) => c
                    .Margin(0, 0, 0, CardGap)
                    .Grid(row: i, column: 0)
                    .WithKey(i.ToString())).ToArray())
                // 卡片自带下间距形成缝隙，所以外层底部内边距取 0，顶/中/底三处间距才一致。
                .Padding(CardGap, CardGap, CardGap, 0);
        }

        Element leftPanelBase = Border(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    tabHeader.Grid(row: 0),
                    cardsPanel.Grid(row: 1),
                ]
            )
        )
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(YuumiTheme.OverlayRadius);

        // 宽屏固定 250（旧版 `.game-layout { grid-template-columns: 250px 1fr }`）；
        // 堆叠态旧版是单列 1fr，面板撑满整行，不能再用 250 的固定宽度（否则会缩成窄条居中）。
        const double LeftPanelWidth = 250;

        // 右侧工具栏：• 友方 (N) + 组队芯片 + VS + 组队芯片 + • 敌方 (N) + 视图切换器
        bool mySideIsBlue = state.MapSide != "red";

        var allyPill = RenderTeamPill(
            $"友方 ({state.MyTeam.Count})",
            mySideIsBlue,
            isClickable: true,
            onClick: () => service.SetActiveTab("my"),
            isActive: isMyTab);

        var enemyPill = RenderTeamPill(
            $"敌方 ({state.TheirTeam.Count})",
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
        .Padding(12, 6, 12, 6)
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

        // 旧版 `.game-layout`：宽屏是「250 + 1fr」两列（列间距 16）；
        // 窗口窄于 StackBreakpoint 时改成上下堆叠，行高 34% / 66%（旧版 minmax(180px,34vh) 1fr）。
        Element layout = stacked
            ? Grid(
                [GridSize.Star()],
                [GridSize.Star(34), GridSize.Star(66)],
                [
                    leftPanelBase.Grid(row: 0),
                    rightPanel.Grid(row: 1).Margin(0, 12, 0, 0),
                ])
            : Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Star()],
                [
                    // 旧版 `.game-layout` 的列间距是 16（不是 12）
                    leftPanelBase.Width(LeftPanelWidth).Grid(row: 0, column: 0).Margin(0, 0, 16, 0),
                    rightPanel.Grid(row: 0, column: 1),
                ]);

        // 旧版 `.game-info` padding 16/24/16/9.6（上右下左）；9.6 取 8，
        // 旧的统一 8 会让两侧面板直接贴到标题栏与窗口边缘。
        return layout.Padding(8, 16, 24, 16);
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

        var allyPill = RenderTeamPill(
            $"友方 ({state.MyTeam.Count})",
            mySideIsBlue);

        var enemyPill = RenderTeamPill(
            $"敌方 ({state.TheirTeam.Count})",
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
        .Padding(12, 6, 12, 6)
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
        // 与 5 列视图同一套页面内边距（旧版 `.game-info`）
        .Padding(8, 16, 24, 16);
    }

    /// <summary>
    /// 队伍页签的内容：文案垂直居中 + 贴底 2px 指示线（对齐旧版 <c>.tab-btn</c> 的 border-bottom）。
    ///
    /// 指示线单独占一行、文字占整行剩余高度，而不是「文字 + 下划线」上下堆叠：
    /// 堆叠写法里指示线紧贴文字，页签实际高度只有 30，比旧版矮 16。
    /// </summary>
    private static Element TeamTabContent(string label, bool isActive) =>
        Grid(
            [GridSize.Star()],
            [GridSize.Star(), GridSize.Auto],
            [
                Caption(label)
                    .Foreground(isActive ? Theme.AccentText : Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 0),
                Border(Empty())
                    .Height(2)
                    .Background(isActive ? Theme.Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Grid(row: 1, column: 0),
            ]);

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
            .Margin(0, 0, 5, 0);

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
                .Padding(8, TeamPillPadV)
                .Height(TeamPillHeight);
        }

        return Border(content)
            .Background(bgColor)
            .BorderBrush(borderColor)
            .BorderThickness(new Thickness(1))
            .CornerRadius(YuumiTheme.PillRadius)
            .Padding(8, TeamPillPadV)
            .Height(TeamPillHeight);
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

        var teamContent = HStack(4,
            Caption(Icons.People)
                .FontFamily(Icons.FontFamily)
                .Foreground(isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
            Caption("按队伍")
                .Foreground(isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
        );

        var teamBtn = (isFive
            ? Button(teamContent, () => onSelect(5)).AccentButton()
            : Button(teamContent, () => onSelect(5)).SubtleButton())
            .AutomationName("按队伍模式")
            .BorderThickness(new Thickness(0))
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8, SegmentButtonPadV)
            .Height(SegmentButtonHeight);

        var allContent = HStack(4,
            Caption(Icons.AllMatches)
                .FontFamily(Icons.FontFamily)
                .Foreground(!isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center),
            Caption("全部战绩")
                .Foreground(!isFive ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                .VAlign(VerticalAlignment.Center)
        );

        var allBtn = (!isFive
            ? Button(allContent, () => onSelect(10)).AccentButton()
            : Button(allContent, () => onSelect(10)).SubtleButton())
            .AutomationName("全部战绩模式")
            .BorderThickness(new Thickness(0))
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8, SegmentButtonPadV)
            .Height(SegmentButtonHeight);

        return Border(
            HStack(2, teamBtn, allBtn)
        )
        .Background(Theme.SubtleFill)
        .BorderBrush(Theme.CardStroke)
        .BorderThickness(new Thickness(1))
        .CornerRadius(YuumiTheme.OverlayRadius)
        .Padding(2);
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

