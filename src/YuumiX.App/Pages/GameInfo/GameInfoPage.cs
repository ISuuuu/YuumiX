using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
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
    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var gameInfoService = services.Services.GetRequiredService<IGameInfoService>();
        var lcu = services.Services.GetRequiredService<LcuConnection>();

        var nav = UseNavigation<AppRoute>();
        Action<string, SummonerProfile?> onNavigateCareer = (puuid, profile) =>
        {
            CareerNavigationPayload.Instance.Set(puuid, profile);
            nav.Replace(AppRoute.Career);
        };
        Action<string, string?> onNavigateSearch = (query, puuid) =>
        {
            SearchNavigationPayload.Instance.Set(query, puuid);
            nav.Replace(AppRoute.Search);
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

        // 初次加载拉取一次状态
        UseEffect(
            () =>
            {
                _ = gameInfoService.RefreshStateAsync();
                return static () => { };
            },
            0);

        // 1. LCU 未连接
        if (lcu.CurrentState.Status != LcuConnectionStatus.Connected)
        {
            return RenderPlaceholder(
                "LCU 客户端未连接",
                "请先启动英雄联盟客户端并登录",
                () => gameInfoService.LoadDemoState());
        }

        // 2. 云顶之弈模式
        if (state.IsTftMode)
        {
            return RenderPlaceholder("云顶之弈对局中", "云顶之弈模式不显示召唤师峡谷数据");
        }

        // 3. 错误状态
        if (!string.IsNullOrEmpty(state.Error))
        {
            return RenderPlaceholder("数据读取异常", state.Error);
        }

        // 4. 等待对局
        if (!state.ShouldShowContent)
        {
            return RenderPlaceholder(
                "等待对局开始",
                "进入选人阶段或对局中后将自动展示双方玩家数据",
                () => gameInfoService.LoadDemoState());
        }

        // 5. 正在读取队伍
        if (state.IsLoadingTeams)
        {
            return RenderPlaceholder("正在读取对局玩家", "正在连接 LCU 获取对局双方玩家信息...");
        }

        // 6. 正常对局渲染
        return state.ViewColumns == 10
            ? RenderTenColumnView(state, gameInfoService, palette, services, onNavigateSearch)
            : RenderFiveColumnView(state, gameInfoService, palette, services, onNavigateCareer, onNavigateSearch);
    }

    private static Element? RenderDodgeButton(AppServices services)
    {
        if (!services.Config.Functions.EnableDodgeInGameInfo)
        {
            return null;
        }

        var toolsService = services.Services.GetService<YuumiX.Core.Features.Tools.IToolsService>();
        if (toolsService is null)
        {
            return null;
        }

        return Button("秒退", async () =>
        {
            try
            {
                await toolsService.DodgeChampSelectAsync();
            }
            catch
            {
                // 静默隔离
            }
        })
        .Background("#FFE53935")
        .Foreground("#FFFFFFFF")
        .FontSize(11)
        .Padding(12, 4)
        .CornerRadius(6)
        .Margin(new Thickness(0, 0, 8, 0))
        .AutomationName("秒退对局");
    }

    private static Element RenderPlaceholder(string title, string subtitle, Action? onDemoClick = null)
    {
        var items = new List<Element>
        {
            TextBlock(title)
                .FontSize(18)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                .HAlign(HorizontalAlignment.Center),
            TextBlock(subtitle)
                .FontSize(12)
                .Foreground("#FFAAAAAA")
                .HAlign(HorizontalAlignment.Center)
        };

        if (onDemoClick != null)
        {
            items.Add(
                Button("预览演示对局", onDemoClick)
                    .FontSize(11.5)
                    .CornerRadius(4)
                    .Margin(0, 12, 0, 0)
                    .HAlign(HorizontalAlignment.Center)
            );
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

        // 左侧队伍 Tab 切换器（带指示下划线与激活色彩）
        var allyTabBtn = Button(
            VStack(0,
                TextBlock($"友方 ({state.MyTeam.Count})")
                    .FontSize(12)
                    .FontWeight(isMyTab ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal)
                    .Foreground(isMyTab ? "#FF3B82F6" : "#FFAAAAAA")
                    .HAlign(HorizontalAlignment.Center),
                Border(Empty())
                    .Height(2)
                    .Background(isMyTab ? "#FF3B82F6" : "#00000000")
                    .Margin(0, 6, 0, 0)
            ),
            () => service.SetActiveTab("my")
        )
        .AutomationName($"友方队伍 ({state.MyTeam.Count})")
        .Background(isMyTab ? "#08FFFFFF" : "#00000000")
        .BorderThickness(new Thickness(0))
        .CornerRadius(0)
        .Padding(0, 8, 0, 0)
        .HAlign(HorizontalAlignment.Stretch);

        var enemyTabBtn = Button(
            VStack(0,
                TextBlock($"敌方 ({state.TheirTeam.Count})")
                    .FontSize(12)
                    .FontWeight(!isMyTab ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal)
                    .Foreground(!isMyTab ? "#FF3B82F6" : "#FFAAAAAA")
                    .HAlign(HorizontalAlignment.Center),
                Border(Empty())
                    .Height(2)
                    .Background(!isMyTab ? "#FF3B82F6" : "#00000000")
                    .Margin(0, 6, 0, 0)
            ),
            () => service.SetActiveTab("their")
        )
        .AutomationName($"敌方队伍 ({state.TheirTeam.Count})")
        .Background(!isMyTab ? "#08FFFFFF" : "#00000000")
        .BorderThickness(new Thickness(0))
        .CornerRadius(0)
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
        .BorderBrush("#15FFFFFF");

        // 左侧玩家卡片列表
        var cards = currentTeam.Select((p, idx) =>
        {
            var pData = FindPlayerData(p, state.PlayerData);
            int premadeIdx = PremadeAnalyzer.GetPlayerColorIdx(p, isMyTab ? state.PremadeColorsMy : state.PremadeColorsTheir);

            return Component<PlayerCardView, PlayerCardProps>(
                new PlayerCardProps(p, pData, premadeIdx, idx, state.CurrentSummonerPuuid, state.SavedPlayerMap, onNavigateCareer, onNavigateSearch));
        }).ToArray();

        var leftPanel = Border(
            Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    tabHeader.Grid(row: 0),
                    ScrollView(
                        VStack(0, cards).Padding(8)
                    ).Grid(row: 1),
                ]
            )
        )
        .Width(250)
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(8);

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

        var vsBadge = RenderVsBadge();

        var segmentedControl = RenderSegmentedControl(5, cols => service.SetViewColumns(cols));
        var dodgeBtn = RenderDodgeButton(services);

        var toolbarLeft = HStack(8,
            allyPill,
            myChips,
            vsBadge,
            theirChips,
            enemyPill
        )
        .VAlign(VerticalAlignment.Center);

        var toolbarRight = HStack(0,
            dodgeBtn ?? Empty(),
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
        .BorderBrush("#15FFFFFF");

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
                    onNavigateSearch))
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
        .CornerRadius(8);

        return Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Star()],
            [
                leftPanel.Grid(row: 0, column: 0).Margin(0, 0, 12, 0),
                rightPanel.Grid(row: 0, column: 1),
            ]
        )
        .Padding(10);
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

        var vsBadge = RenderVsBadge();

        var segmentedControl = RenderSegmentedControl(10, cols => service.SetViewColumns(cols));
        var dodgeBtn = RenderDodgeButton(services);

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
                        dodgeBtn ?? Empty(),
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
        .BorderBrush("#15FFFFFF");

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
                    onNavigateSearch))
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
                    onNavigateSearch))
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
        .CornerRadius(8);

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                fullPanel.Grid(row: 0, column: 0),
            ]
        )
        .Padding(10);
    }

    private static Element RenderTeamPill(
        string text,
        bool isBlueSide,
        bool isClickable = false,
        Action? onClick = null,
        bool isActive = false)
    {
        string dotColor = isBlueSide ? "#FF3B82F6" : "#FFF43F5E";
        string bgColor = isBlueSide ? "#203B82F6" : "#20F43F5E";
        string borderColor = isBlueSide ? "#403B82F6" : "#40F43F5E";
        string fgColor = isBlueSide ? "#FF3B82F6" : "#FFF43F5E";

        var dot = Border(Empty())
            .Width(5)
            .Height(5)
            .CornerRadius(2.5)
            .Background(dotColor)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 5, 0);

        var label = TextBlock(text)
            .FontSize(11)
            .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
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
                .CornerRadius(12)
                .Padding(8, 3)
                .Height(26);
        }

        return Border(content)
            .Background(bgColor)
            .BorderBrush(borderColor)
            .BorderThickness(new Thickness(1))
            .CornerRadius(12)
            .Padding(8, 3);
    }

    private static Element RenderVsBadge()
    {
        return Border(
            TextBlock("VS")
                .FontSize(10)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .Foreground("#FFAAAAAA")
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
        )
        .Background("#15FFFFFF")
        .WithBorder("#20FFFFFF", 1)
        .CornerRadius(10)
        .Padding(7, 2);
    }

    private static Element RenderSegmentedControl(int currentColumns, Action<int> onSelect)
    {
        bool isFive = currentColumns == 5;

        var teamContent = HStack(4,
            TextBlock("\uE716")
                .FontFamily("Segoe Fluent Icons")
                .FontSize(11)
                .Foreground(isFive ? "#FFFFFFFF" : "#FFAAAAAA")
                .VAlign(VerticalAlignment.Center),
            TextBlock("按队伍")
                .FontSize(11)
                .FontWeight(isFive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal)
                .Foreground(isFive ? "#FFFFFFFF" : "#FFAAAAAA")
                .VAlign(VerticalAlignment.Center)
        );

        var teamBtn = Button(teamContent, () => onSelect(5))
            .AutomationName("按队伍模式")
            .Background(isFive ? "#253B82F6" : "#00000000")
            .BorderThickness(new Thickness(0))
            .CornerRadius(4)
            .Padding(8, 4)
            .Height(28);

        var allContent = HStack(4,
            TextBlock("\uE80A")
                .FontFamily("Segoe Fluent Icons")
                .FontSize(11)
                .Foreground(!isFive ? "#FFFFFFFF" : "#FFAAAAAA")
                .VAlign(VerticalAlignment.Center),
            TextBlock("全部战绩")
                .FontSize(11)
                .FontWeight(!isFive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal)
                .Foreground(!isFive ? "#FFFFFFFF" : "#FFAAAAAA")
                .VAlign(VerticalAlignment.Center)
        );

        var allBtn = Button(allContent, () => onSelect(10))
            .AutomationName("全部战绩模式")
            .Background(!isFive ? "#253B82F6" : "#00000000")
            .BorderThickness(new Thickness(0))
            .CornerRadius(4)
            .Padding(8, 4)
            .Height(28);

        return Border(
            HStack(2, teamBtn, allBtn)
        )
        .Background("#10FFFFFF")
        .BorderBrush("#20FFFFFF")
        .BorderThickness(new Thickness(1))
        .CornerRadius(6)
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

