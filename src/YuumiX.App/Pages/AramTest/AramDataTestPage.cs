using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.App.Windowing;
using YuumiX.Core.Features.AramData;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.AramTest;

public sealed record AramDataTestPageProps(int? InitialChampionId = null);

internal enum AramSortField
{
    WinRate,
    Tier,
    PickRate
}

internal enum AramDetailTab
{
    Augments,
    Builds
}

internal enum AramAugmentRarityFilter
{
    All,
    Prismatic,
    Gold,
    Silver
}

/// <summary>
/// 独立右侧贴靠窗口内的「海克斯与出装推荐」页面：
/// 1) 英雄榜视图：默认按胜率由高到低排列，展示头像、名称、梯队（T1/T2...）、胜率、登场率，支持搜索与表头排序；
/// 2) 英雄详情视图：点击英雄（或选人自动跳转）展示头像、梯队、胜率/选取率双卡，以及「海克斯 / 出装」双页签（默认海克斯）。
/// </summary>
public sealed class AramDataTestPage : Component<AramDataTestPageProps>
{
    private static readonly string[] HotkeyModifiers =
    [
        "Alt",
        "Ctrl",
        "Shift",
        "Ctrl+Shift",
        "Alt+Shift",
        "无修饰键"
    ];

    private static readonly string[] HotkeyKeys =
    [
        "X", "H", "Z", "C", "V", "B", "G", "K", "L",
        "`", "Tab",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"
    ];

    private static readonly AramPageUi InitialUi = new(
        Url: string.Empty,
        DraftUrl: string.Empty,
        DraftModIndex: 0,
        DraftKeyIndex: 0,
        IsUrlDialogOpen: false,
        SearchQuery: string.Empty,
        SortField: AramSortField.WinRate,
        SortDescending: true,
        ActiveTab: AramDetailTab.Augments,
        RarityFilter: AramAugmentRarityFilter.All,
        SelectedBuildIndex: 0,
        ShowDetailView: false,
        IsLoadingDataset: false,
        IsLoadingDetail: false,
        StatusMessage: "正在初始化...",
        ErrorMessage: null,
        Dataset: null,
        SelectedChampionId: null,
        Detail: null);

    private sealed record AramPageUi(
        string Url,
        string DraftUrl,
        int DraftModIndex,
        int DraftKeyIndex,
        bool IsUrlDialogOpen,
        string SearchQuery,
        AramSortField SortField,
        bool SortDescending,
        AramDetailTab ActiveTab,
        AramAugmentRarityFilter RarityFilter,
        int SelectedBuildIndex,
        bool ShowDetailView,
        bool IsLoadingDataset,
        bool IsLoadingDetail,
        string StatusMessage,
        string? ErrorMessage,
        AramLoadedDataset? Dataset,
        int? SelectedChampionId,
        AramChampionDetail? Detail);

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var client = services.Services.GetRequiredService<AramDataClient>();
        var config = services.Config;
        var configStore = services.ConfigStore;

        var (ui, setUi) = UseState(InitialUi, threadSafe: true);
        var uiRef = UseRef(InitialUi);
        uiRef.Current = ui;

        void UpdateUi(Func<AramPageUi, AramPageUi> updater)
        {
            var next = updater(uiRef.Current);
            uiRef.Current = next;
            setUi(next);
        }

        async Task LoadChampionAsync(AramLoadedDataset dataset, int championId, bool openDetailView = true)
        {
            UpdateUi(prev => prev with
            {
                SelectedChampionId = championId,
                ShowDetailView = openDetailView,
                ActiveTab = AramDetailTab.Augments,
                RarityFilter = AramAugmentRarityFilter.All,
                SelectedBuildIndex = 0,
                IsLoadingDetail = true,
                ErrorMessage = null
            });

            try
            {
                var detail = await client.LoadChampionDetailAsync(dataset, championId);
                UpdateUi(prev => prev with
                {
                    IsLoadingDetail = false,
                    Detail = detail
                });
            }
            catch (Exception ex)
            {
                UpdateUi(prev => prev with
                {
                    IsLoadingDetail = false,
                    ErrorMessage = $"加载英雄 {championId} 详情失败：{ex.Message}"
                });
            }
        }

        async Task LoadDatasetAsync(string? targetUrlOverride = null, int? preferredChampionId = null, bool forceRefresh = false)
        {
            var targetUrl = targetUrlOverride ?? uiRef.Current.Url;

            if (!forceRefresh && uiRef.Current.Dataset == null)
            {
                var localDataset = await client.TryLoadLocalDatasetAsync(targetUrl);
                if (localDataset != null)
                {
                    var initialId = preferredChampionId ?? uiRef.Current.SelectedChampionId;
                    UpdateUi(prev => prev with
                    {
                        Dataset = localDataset,
                        SelectedChampionId = initialId,
                        ShowDetailView = initialId is > 0,
                        StatusMessage = $"本地缓存 v{localDataset.DataVersion} · 游戏版本 {localDataset.GamePatch ?? "-"}"
                    });

                    if (initialId is > 0)
                    {
                        await LoadChampionAsync(localDataset, initialId.Value, openDetailView: true);
                    }
                }
            }

            UpdateUi(prev => prev with
            {
                IsLoadingDataset = true,
                ErrorMessage = null,
                StatusMessage = forceRefresh ? "正在刷新远端数据..." : (prev.Dataset != null ? prev.StatusMessage : "正在检测远端版本...")
            });

            try
            {
                var dataset = await client.CheckAndSyncAsync(targetUrl, force: forceRefresh);
                var targetChampId = preferredChampionId ?? uiRef.Current.SelectedChampionId;
                var shouldOpenDetail = targetChampId is > 0 && (preferredChampionId is > 0 || uiRef.Current.ShowDetailView);

                UpdateUi(prev => prev with
                {
                    IsLoadingDataset = false,
                    Dataset = dataset,
                    SelectedChampionId = targetChampId,
                    ShowDetailView = shouldOpenDetail,
                    StatusMessage = $"数据 {dataset.DataVersion} · 游戏版本 {dataset.GamePatch ?? "-"} · {(dataset.FromLocalCache ? "已载入缓存" : "已更新")}"
                });

                if (shouldOpenDetail && targetChampId is > 0)
                {
                    await LoadChampionAsync(dataset, targetChampId.Value, openDetailView: true);
                }
            }
            catch (Exception ex)
            {
                UpdateUi(prev => prev with
                {
                    IsLoadingDataset = false,
                    ErrorMessage = prev.Dataset == null ? ex.Message : $"检查更新失败：{ex.Message}",
                    StatusMessage = prev.Dataset != null
                        ? $"数据 {prev.Dataset.DataVersion} · 离线缓存"
                        : "拉取失败，请检查服务端地址。"
                });
            }
        }

        UseEffect(
            () =>
            {
                var defaultUrl = !string.IsNullOrWhiteSpace(config.General.AramDataApiUrl)
                    ? config.General.AramDataApiUrl.Trim()
                    : "http://192.168.6.133:3456";
                UpdateUi(prev => prev with
                {
                    Url = string.IsNullOrEmpty(prev.Url) ? defaultUrl : prev.Url,
                    DraftUrl = string.IsNullOrEmpty(prev.DraftUrl) ? defaultUrl : prev.DraftUrl,
                    SelectedChampionId = Props.InitialChampionId ?? prev.SelectedChampionId,
                    ShowDetailView = Props.InitialChampionId is > 0
                });

                _ = LoadDatasetAsync(defaultUrl, Props.InitialChampionId);

                void OnChampionRequested(int champId)
                {
                    var ds = uiRef.Current.Dataset;
                    if (ds != null)
                    {
                        _ = LoadChampionAsync(ds, champId, openDetailView: true);
                    }
                    else
                    {
                        UpdateUi(prev => prev with { SelectedChampionId = champId, ShowDetailView = true });
                        _ = LoadDatasetAsync(uiRef.Current.Url, champId);
                    }
                }

                AramDataWindowHost.SelectedChampionRequested += OnChampionRequested;
                return () => AramDataWindowHost.SelectedChampionRequested -= OnChampionRequested;
            },
            0);

        var blocks = new List<Element>
        {
            BuildConnectionHeaderCard(
                ui,
                onOpenUrlSettings: () =>
                {
                    var (modIdx, keyIdx) = ParseHotkeyIndices(config.General.AramDataHotkey);
                    UpdateUi(prev => prev with
                    {
                        DraftUrl = prev.Url,
                        DraftModIndex = modIdx,
                        DraftKeyIndex = keyIdx,
                        IsUrlDialogOpen = true
                    });
                },
                onRefresh: () => LoadDatasetAsync(forceRefresh: true))
        };

        if (ui.Dataset != null)
        {
            if (ui.ShowDetailView && ui.SelectedChampionId is > 0)
            {
                blocks.Add(BuildDetailView(
                    ui,
                    onBackToList: () => UpdateUi(prev => prev with { ShowDetailView = false }),
                    onTabChanged: tab => UpdateUi(prev => prev with { ActiveTab = tab }),
                    onRarityFilterChanged: filter => UpdateUi(prev => prev with { RarityFilter = filter }),
                    onBuildSelected: idx => UpdateUi(prev => prev with { SelectedBuildIndex = idx })));
            }
            else
            {
                blocks.Add(BuildChampionRankListCard(
                    ui,
                    query => UpdateUi(prev => prev with { SearchQuery = query }),
                    field => UpdateUi(prev => prev with
                    {
                        SortField = field,
                        SortDescending = prev.SortField == field ? !prev.SortDescending : field != AramSortField.Tier
                    }),
                    champId => _ = LoadChampionAsync(ui.Dataset, champId, openDetailView: true)));
            }
        }

        var urlDialog = ConfirmationDialog.Render(
            title: "数据源与热键设置",
            content: VStack(12,
                VStack(4,
                    Caption("服务端 API 地址（保存后点击刷新按钮拉取数据）：")
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(),
                    TextBox(
                        ui.DraftUrl,
                        v => UpdateUi(prev => prev with { DraftUrl = v }),
                        "http://192.168.6.133:3456")
                        .HAlign(HorizontalAlignment.Stretch)
                        .AutomationName("服务端 API 地址")),
                VStack(4,
                    Caption("全局呼出热键（按一次显示，再按一次隐藏）：")
                        .Foreground(Theme.SecondaryText)
                        .TextWrapping(),
                    Grid(
                        [GridSize.Star(), GridSize.Star()],
                        [GridSize.Auto],
                        [
                            ComboBox(HotkeyModifiers, ui.DraftModIndex, idx => UpdateUi(prev => prev with { DraftModIndex = idx }))
                                .HAlign(HorizontalAlignment.Stretch)
                                .AutomationName("热键修饰键")
                                .Margin(0, 0, 8, 0)
                                .Grid(row: 0, column: 0),
                            ComboBox(HotkeyKeys, ui.DraftKeyIndex, idx => UpdateUi(prev => prev with { DraftKeyIndex = idx }))
                                .HAlign(HorizontalAlignment.Stretch)
                                .AutomationName("热键主键")
                                .Grid(row: 0, column: 1),
                        ]))),
            primaryText: "保存",
            closeText: "取消",
            isOpen: ui.IsUrlDialogOpen,
            onConfirm: () =>
            {
                var cur = uiRef.Current;
                var nextUrl = cur.DraftUrl.Trim();
                var nextHotkey = BuildHotkeyString(cur.DraftModIndex, cur.DraftKeyIndex);

                if (!string.IsNullOrWhiteSpace(nextUrl))
                {
                    config.General.AramDataApiUrl = nextUrl;
                }
                config.General.AramDataHotkey = nextHotkey;
                configStore.Save(config);
                AramHotkeyMonitor.UpdateHotkey(nextHotkey);

                UpdateUi(prev => prev with
                {
                    Url = string.IsNullOrWhiteSpace(nextUrl) ? prev.Url : nextUrl,
                    IsUrlDialogOpen = false,
                    StatusMessage = $"已保存（热键 {nextHotkey}），点击刷新按钮可拉取数据"
                });
            },
            onDismiss: () => UpdateUi(prev => prev with { IsUrlDialogOpen = false }),
            palette: theme.Current,
            isPrimaryEnabled: !string.IsNullOrWhiteSpace(ui.DraftUrl));

        return Grid(
            [GridSize.Star()],
            [GridSize.Star()],
            [
                ScrollViewer(
                    VStack(12, blocks.ToArray())
                        .Padding(16)
                        .HAlign(HorizontalAlignment.Stretch)),
                urlDialog
            ]);
    }

    private static Element BuildConnectionHeaderCard(
        AramPageUi ui,
        Action onOpenUrlSettings,
        Func<Task> onRefresh)
    {
        var items = new List<Element>
        {
            Grid(
                [GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    VStack(4,
                        HStack(8,
                            Subtitle("海克斯英雄榜").SemiBold().HeadingLevel(AutomationHeadingLevel.Level1),
                            ui.Dataset != null
                                ? Border(Caption($"v{ui.Dataset.GamePatch ?? ui.Dataset.DataVersion}").Foreground(Theme.AccentText))
                                    .Padding(8, 2)
                                    .CornerRadius(YuumiTheme.ControlRadius)
                                    .Background(Theme.SubtleFill)
                                    .VAlign(VerticalAlignment.Center)
                                : Empty()),
                        Caption(ui.StatusMessage).Foreground(Theme.SecondaryText).TextWrapping())
                        .Grid(row: 0, column: 0),
                    HStack(4,
                        Button(
                            Caption(Icons.Settings).FontFamily(Icons.FontFamily),
                            onOpenUrlSettings)
                            .GhostButton()
                            .AutomationName("数据源设置")
                            .WithToolTip(TextBlock("数据源设置")),
                        Button(
                            Caption(Icons.Refresh).FontFamily(Icons.FontFamily),
                            () => _ = onRefresh())
                            .GhostButton()
                            .IsEnabled(!ui.IsLoadingDataset)
                            .AutomationName("拉取远端数据")
                            .WithToolTip(TextBlock("拉取远端数据")))
                        .Grid(row: 0, column: 1)
                        .VAlign(VerticalAlignment.Center)
                ])
        };

        if (!string.IsNullOrWhiteSpace(ui.ErrorMessage))
        {
            items.Add(Caption(ui.ErrorMessage).Foreground(Theme.SystemCritical).TextWrapping());
        }

        return Border(VStack(8, items.ToArray()))
            .Padding(12)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element BuildChampionRankListCard(
        AramPageUi ui,
        Action<string> onSearchChanged,
        Action<AramSortField> onSortClicked,
        Action<int> onChampionClicked)
    {
        var all = ui.Dataset!.Champions;
        var q = ui.SearchQuery.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? all
            : all.Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = SortChampions(filtered, ui.SortField, ui.SortDescending);

        string SortArrow(AramSortField field) =>
            ui.SortField == field ? (ui.SortDescending ? " ↓" : " ↑") : string.Empty;

        var headerGrid = Grid(
            [GridSize.Star(), GridSize.Px(56), GridSize.Px(68), GridSize.Px(68)],
            [GridSize.Auto],
            [
                Caption("英雄").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 0),
                Button(Caption($"梯队{SortArrow(AramSortField.Tier)}").Foreground(Theme.SecondaryText), () => onSortClicked(AramSortField.Tier))
                    .GhostButton()
                    .Padding(4, 2)
                    .HAlign(HorizontalAlignment.Center)
                    .AutomationName("按梯队排序")
                    .Grid(row: 0, column: 1),
                Button(Caption($"胜率{SortArrow(AramSortField.WinRate)}").Foreground(Theme.SecondaryText), () => onSortClicked(AramSortField.WinRate))
                    .GhostButton()
                    .Padding(4, 2)
                    .HAlign(HorizontalAlignment.Right)
                    .AutomationName("按胜率排序")
                    .Grid(row: 0, column: 2),
                Button(Caption($"登场{SortArrow(AramSortField.PickRate)}").Foreground(Theme.SecondaryText), () => onSortClicked(AramSortField.PickRate))
                    .GhostButton()
                    .Padding(4, 2)
                    .HAlign(HorizontalAlignment.Right)
                    .AutomationName("按登场率排序")
                    .Grid(row: 0, column: 3),
            ])
            .HAlign(HorizontalAlignment.Stretch);

        var headerRow = Border(headerGrid)
            .Padding(12, 8)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(Theme.CardStroke);

        var rows = sorted.Select((c, idx) =>
        {
            var tierLabel = c.Tier is > 0 ? $"T{c.Tier}" : "-";
            var tierBadge = Border(
                    Caption(tierLabel)
                        .SemiBold()
                        .Foreground(c.Tier == 1 ? Theme.AccentText : (c.Tier == 2 ? Theme.PrimaryText : Theme.SecondaryText))
                        .HAlign(HorizontalAlignment.Center))
                .Padding(8, 2)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(c.Tier == 1 ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
                .HAlign(HorizontalAlignment.Center)
                .VAlign(VerticalAlignment.Center)
                .Grid(row: 0, column: 1);

            var nameCell = (Grid(
                [GridSize.Auto, GridSize.Star()],
                [GridSize.Auto],
                [
                    Component<LcuImage, LcuImageProps>(
                        new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{c.Id}.png", 32, CornerRadius: 16))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 0),
                    VStack(0,
                        BodyStrong(c.Name)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1),
                        Caption(c.Title)
                            .Foreground(Theme.SecondaryText)
                            .TextTrimming(TextTrimming.CharacterEllipsis)
                            .MaxLines(1))
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 1),
                ]) with
            { ColumnSpacing = 8 })
                .HAlign(HorizontalAlignment.Stretch)
                .Grid(row: 0, column: 0);

            var rowContent = Grid(
                [GridSize.Star(), GridSize.Px(56), GridSize.Px(68), GridSize.Px(68)],
                [GridSize.Auto],
                [
                    nameCell,
                    tierBadge,
                    Body(FormatPct(c.WinRate))
                        .SemiBold()
                        .Foreground(c.Tier == 1 ? Theme.AccentText : Theme.PrimaryText)
                        .HAlign(HorizontalAlignment.Right)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 2),
                    Caption(FormatPct(c.PickRate))
                        .Foreground(Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Right)
                        .VAlign(VerticalAlignment.Center)
                        .Grid(row: 0, column: 3),
                ])
                .HAlign(HorizontalAlignment.Stretch);

            return (Element)Button(rowContent, () => onChampionClicked(c.Id))
                .GhostButton()
                .Background(idx % 2 == 0 ? Theme.SubtleFill : Theme.Ref("SubtleFillColorTransparentBrush"))
                .HorizontalContentAlignment(HorizontalAlignment.Stretch)
                .HAlign(HorizontalAlignment.Stretch)
                .Padding(12, 8)
                .CornerRadius(YuumiTheme.ControlRadius)
                .AutomationName($"{c.Name} {tierLabel} 胜率 {FormatPct(c.WinRate)}");
        }).ToArray();

        return Border(
                VStack(8,
                [
                    TextBox(ui.SearchQuery, onSearchChanged, "搜索英雄名称、称号或 ID...")
                        .HAlign(HorizontalAlignment.Stretch)
                        .AutomationName("搜索英雄"),
                    Caption($"显示 {sorted.Count} / {all.Count} 位英雄（默认按胜率由高到低）")
                        .Foreground(Theme.SecondaryText),
                    headerRow,
                    rows.Length > 0
                        ? (Element)VStack(4, rows)
                        : Border(Caption("没有匹配的英雄").Foreground(Theme.SecondaryText).HAlign(HorizontalAlignment.Center))
                            .Padding(20)
                ]))
            .Padding(12)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element BuildDetailView(
        AramPageUi ui,
        Action onBackToList,
        Action<AramDetailTab> onTabChanged,
        Action<AramAugmentRarityFilter> onRarityFilterChanged,
        Action<int> onBuildSelected)
    {
        var items = new List<Element>
        {
            HStack(8,
                Button("← 返回英雄榜", onBackToList)
                    .CornerRadius(YuumiTheme.ControlRadius),
                Caption("点击切换其他英雄或返回列表").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center))
        };

        if (ui.IsLoadingDetail)
        {
            items.Add(Border(
                    HStack(12,
                        ProgressRing().IsActive(true).Width(20).Height(20),
                        Body("正在加载英雄详情...")))
                .Padding(16)
                .CornerRadius(YuumiTheme.OverlayRadius)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1));
            return VStack(12, items.ToArray());
        }

        if (ui.Detail == null || ui.Dataset == null)
        {
            return VStack(12, items.ToArray());
        }

        var champ = ui.Detail.Champion;

        // 英雄头部信息卡片 + 梯队/胜率徽章
        items.Add(Border(
                Grid(
                    [GridSize.Star(), GridSize.Auto],
                    [GridSize.Auto],
                    [
                        HStack(12,
                            Border(
                                Component<LcuImage, LcuImageProps>(
                                    new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{champ.Id}.png", 56, CornerRadius: 12)))
                                .CornerRadius(YuumiTheme.OverlayRadius)
                                .WithBorder(Theme.CardStroke, 1)
                                .VAlign(VerticalAlignment.Center),
                            VStack(4,
                                BodyLarge(champ.Name).SemiBold(),
                                Caption(champ.Title).Foreground(Theme.SecondaryText))
                                .VAlign(VerticalAlignment.Center))
                            .Grid(row: 0, column: 0),

                        VStack(4,
                            Border(
                                Caption($"梯队 T{champ.Tier?.ToString() ?? "-"}")
                                    .SemiBold()
                                    .Foreground(champ.Tier == 1 ? Theme.AccentText : Theme.PrimaryText))
                                .Padding(8, 4)
                                .CornerRadius(YuumiTheme.ControlRadius)
                                .Background(Theme.SubtleFill)
                                .HAlign(HorizontalAlignment.Right),
                            Border(
                                Caption($"胜率 {FormatPct(champ.WinRate)}")
                                    .SemiBold()
                                    .Foreground(GetWinRateBrush(champ.WinRate)))
                                .Padding(8, 4)
                                .CornerRadius(YuumiTheme.ControlRadius)
                                .Background(Theme.SubtleFill)
                                .HAlign(HorizontalAlignment.Right))
                            .VAlign(VerticalAlignment.Center)
                            .Grid(row: 0, column: 1)
                    ]))
            .Padding(16)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1));

        // 胜率 & 选取率双指标小卡
        items.Add(Grid(
            [GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                Border(
                        VStack(4,
                            Caption("当前胜率").Foreground(Theme.SecondaryText),
                            Subtitle(FormatPct(champ.WinRate)).Foreground(GetWinRateBrush(champ.WinRate)).SemiBold()))
                    .Padding(12)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .Margin(0, 0, 8, 0)
                    .Grid(row: 0, column: 0),

                Border(
                        VStack(4,
                            Caption("登场选取率").Foreground(Theme.SecondaryText),
                            Subtitle(FormatPct(champ.PickRate)).Foreground(Theme.PrimaryText).SemiBold()))
                    .Padding(12)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1)
                    .Grid(row: 0, column: 1)
            ]));

        // 「海克斯」/「出装」分段式切换条（默认选中海克斯）
        var isAugmentsTab = ui.ActiveTab == AramDetailTab.Augments;
        items.Add(Border(
                Grid(
                    [GridSize.Star(), GridSize.Star()],
                    [GridSize.Auto],
                    [
                        BuildTabButton("海克斯强化", isAugmentsTab, () => onTabChanged(AramDetailTab.Augments))
                            .Margin(0, 0, 4, 0)
                            .Grid(row: 0, column: 0),
                        BuildTabButton("推荐出装", !isAugmentsTab, () => onTabChanged(AramDetailTab.Builds))
                            .Grid(row: 0, column: 1)
                    ]))
            .Padding(4)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(Theme.SubtleFill)
            .WithBorder(Theme.CardStroke, 1));

        if (isAugmentsTab)
        {
            items.Add(ui.Detail.Augments.Count > 0
                ? BuildAugmentsCard(ui.Detail.Augments, ui.RarityFilter, onRarityFilterChanged)
                : Border(Caption("该英雄暂无海克斯强化数据。").Foreground(Theme.SecondaryText))
                    .Padding(16)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1));
        }
        else
        {
            if (ui.Detail.Builds.Count == 0)
            {
                items.Add(Border(Caption("该英雄暂无出装方案数据。").Foreground(Theme.SecondaryText))
                    .Padding(16)
                    .CornerRadius(YuumiTheme.OverlayRadius)
                    .Background(Theme.CardBackground)
                    .WithBorder(Theme.CardStroke, 1));
            }
            else
            {
                var safeIndex = Math.Clamp(ui.SelectedBuildIndex, 0, ui.Detail.Builds.Count - 1);
                if (ui.Detail.Builds.Count > 1)
                {
                    var indexedBuilds = ui.Detail.Builds.Select((b, i) => (Build: b, Index: i)).ToList();
                    static bool IsAdOrAp(AramChampionBuild b) =>
                        !string.IsNullOrWhiteSpace(b.BuildLabel) &&
                        (b.BuildLabel.Contains("AD", StringComparison.OrdinalIgnoreCase) ||
                         b.BuildLabel.Contains("AP", StringComparison.OrdinalIgnoreCase));

                    var firstRow = indexedBuilds.Where(x => IsAdOrAp(x.Build)).ToList();
                    var secondRow = indexedBuilds.Where(x => !IsAdOrAp(x.Build)).ToList();

                    // 若全部分在同一组且超过 2 个，自动按每行 2 个拆成两行
                    if (firstRow.Count == 0 || secondRow.Count == 0)
                    {
                        var split = (indexedBuilds.Count + 1) / 2;
                        firstRow = indexedBuilds.Take(split).ToList();
                        secondRow = indexedBuilds.Skip(split).ToList();
                    }

                    Element BuildRouteRow(List<(AramChampionBuild Build, int Index)> rowItems)
                    {
                        var cols = Enumerable.Range(0, rowItems.Count).Select(_ => GridSize.Star()).ToArray();
                        var btns = rowItems.Select((item, colIdx) =>
                        {
                            var labelText = !string.IsNullOrWhiteSpace(item.Build.BuildLabel) ? item.Build.BuildLabel : $"路线 {item.Index + 1}";
                            var btn = BuildTwoLineRouteChip(
                                    labelText,
                                    $"胜率 {FormatPct(item.Build.WinRate)}",
                                    item.Index == safeIndex,
                                    () => onBuildSelected(item.Index))
                                .Grid(row: 0, column: colIdx);
                            return colIdx < rowItems.Count - 1 ? btn.Margin(0, 0, 4, 0) : btn;
                        }).ToArray();
                        return Grid(cols, [GridSize.Auto], btns);
                    }

                    var routeRows = new List<Element> { BuildRouteRow(firstRow) };
                    if (secondRow.Count > 0)
                    {
                        routeRows.Add(BuildRouteRow(secondRow));
                    }

                    items.Add(Border(
                            VStack(8,
                            [
                                Caption($"共 {ui.Detail.Builds.Count} 条出装路线，点击切换").Foreground(Theme.SecondaryText),
                                VStack(4, routeRows.ToArray())
                            ]))
                        .Padding(12)
                        .CornerRadius(YuumiTheme.OverlayRadius)
                        .Background(Theme.CardBackground)
                        .WithBorder(Theme.CardStroke, 1));
                }

                items.Add(BuildSingleBuildCard(ui.Detail.Builds[safeIndex], safeIndex + 1, ui.Dataset.Items));
            }
        }

        return VStack(12, items.ToArray());
    }

    private static Element BuildTabButton(string label, bool isActive, Action onClick) =>
        Button(
                BodyStrong(label)
                    .Foreground(isActive ? Theme.AccentText : Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center),
                onClick)
            .Background(isActive ? Theme.CardBackground : Theme.Ref("SubtleFillColorTransparentBrush"))
            .WithBorder(isActive ? Theme.CardStroke : Theme.Ref("SubtleFillColorTransparentBrush"), 1)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(12, 8)
            .AutomationName(label)
            .HAlign(HorizontalAlignment.Stretch);

    private static ThemeRef GetWinRateBrush(double rawWinRate)
    {
        var pct = rawWinRate <= 1.0 && rawWinRate > 0 ? rawWinRate * 100.0 : rawWinRate;
        if (pct >= 53.0) return Theme.SystemSuccess;
        if (pct >= 50.0) return Theme.AccentText;
        if (pct > 0 && pct < 47.0) return Theme.SystemCritical;
        return Theme.PrimaryText;
    }

    private static ThemeRef GetRarityBrush(string rarity) => rarity switch
    {
        "棱彩" => Theme.AccentText,
        "黄金" => Theme.SystemCaution,
        _ => Theme.SecondaryText
    };

    private static List<AramChampionSummary> SortChampions(
        IReadOnlyList<AramChampionSummary> list,
        AramSortField field,
        bool descending)
    {
        return (field, descending) switch
        {
            (AramSortField.WinRate, true) => list.OrderByDescending(c => c.WinRate).ThenBy(c => c.Tier ?? int.MaxValue).ToList(),
            (AramSortField.WinRate, false) => list.OrderBy(c => c.WinRate).ThenBy(c => c.Tier ?? int.MaxValue).ToList(),
            (AramSortField.Tier, true) => list.OrderByDescending(c => c.Tier ?? 0).ThenByDescending(c => c.WinRate).ToList(),
            (AramSortField.Tier, false) => list.OrderBy(c => c.Tier ?? int.MaxValue).ThenByDescending(c => c.WinRate).ToList(),
            (AramSortField.PickRate, true) => list.OrderByDescending(c => c.PickRate).ThenByDescending(c => c.WinRate).ToList(),
            (AramSortField.PickRate, false) => list.OrderBy(c => c.PickRate).ThenByDescending(c => c.WinRate).ToList(),
            _ => list.OrderByDescending(c => c.WinRate).ToList()
        };
    }

    private static Element BuildSingleBuildCard(
        AramChampionBuild build,
        int index,
        IReadOnlyDictionary<int, AramItemInfo> itemDict)
    {
        var rows = new List<Element>();

        AddItemGroupSection(rows, "出门装", build.StartingItems, itemDict, showArrows: false);
        AddItemGroupSection(rows, "核心出装", build.CoreItems, itemDict, showArrows: true);
        AddItemGroupSection(rows, "六神装", build.FullItems, itemDict, showArrows: true);

        if (build.SituationalItemIds.Count > 0)
        {
            var iconChips = build.SituationalItemIds.Take(9)
                .Select(id => RenderItemWithLabel(id, itemDict))
                .ToArray();

            rows.Add(VStack(4,
                Caption("备选装备").Foreground(Theme.SecondaryText),
                WrapItemsRow(iconChips)));
        }

        return Border(VStack(12, rows.ToArray()))
            .Padding(12)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static void AddItemGroupSection(
        List<Element> rows,
        string title,
        IReadOnlyList<AramItemGroup> groups,
        IReadOnlyDictionary<int, AramItemInfo> itemDict,
        bool showArrows)
    {
        if (groups.Count == 0) return;

        var groupElements = groups.Take(3).Select(g =>
        {
            var iconChain = new List<Element>();
            for (var i = 0; i < g.ItemIds.Count; i++)
            {
                iconChain.Add(RenderItemIcon(g.ItemIds[i], itemDict, 28));
                if (showArrows && i < g.ItemIds.Count - 1)
                {
                    iconChain.Add(Caption("›").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center));
                }
            }

            var namesText = string.Join(" / ", g.ItemIds.Select(id => ResolveItemName(id, itemDict)));
            return (Element)Border(
                    VStack(4,
                        HStack(8,
                            HStack(4, iconChain.ToArray()).VAlign(VerticalAlignment.Center),
                            Caption($"{FormatPct(g.WinRate)} · {g.Games} 场")
                                .Foreground(Theme.SecondaryText)
                                .VAlign(VerticalAlignment.Center)),
                        Caption(namesText).Foreground(Theme.SecondaryText).TextWrapping()))
                .Padding(8)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(Theme.SubtleFill);
        }).ToArray();

        rows.Add(VStack(4, [Caption(title).Foreground(Theme.SecondaryText), .. groupElements]));
    }

    private static Element WrapItemsRow(Element[] items)
    {
        var rowElements = items
            .Chunk(3)
            .Select(chunk =>
            {
                var cols = Enumerable.Range(0, 3).Select(_ => GridSize.Star()).ToArray();
                var cells = chunk.Select((el, idx) => el.Grid(row: 0, column: idx)).ToArray();
                return (Element)Grid(cols, [GridSize.Auto], cells).HAlign(HorizontalAlignment.Stretch);
            })
            .ToArray();

        return VStack(8, rowElements);
    }

    private static Element RenderItemWithLabel(int itemId, IReadOnlyDictionary<int, AramItemInfo> itemDict) =>
        (Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                RenderItemIcon(itemId, itemDict, 24).Grid(row: 0, column: 0),
                Caption(ResolveItemName(itemId, itemDict))
                    .Foreground(Theme.SecondaryText)
                    .TextTrimming(TextTrimming.CharacterEllipsis)
                    .MaxLines(1)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 1)
            ]) with
        { ColumnSpacing = 4 })
        .HAlign(HorizontalAlignment.Stretch);

    private static Element RenderItemIcon(int itemId, IReadOnlyDictionary<int, AramItemInfo> itemDict, double size)
    {
        var info = itemDict.GetValueOrDefault(itemId);
        var name = info?.Name ?? $"#{itemId}";
        var path = !string.IsNullOrWhiteSpace(info?.IconUrl)
            ? info.IconUrl
            : $"https://cdn.dtodo.cn/hextech/item-icons/{itemId}.png";
        return Component<LcuImage, LcuImageProps>(new LcuImageProps(path, size, CornerRadius: 4))
            .AutomationName(name)
            .VAlign(VerticalAlignment.Center);
    }

    private static Element BuildAugmentsCard(
        IReadOnlyList<AramChampionAugmentStat> augments,
        AramAugmentRarityFilter activeFilter,
        Action<AramAugmentRarityFilter> onFilterChanged)
    {
        var filtered = activeFilter switch
        {
            AramAugmentRarityFilter.Prismatic => augments.Where(a => a.Rarity == "棱彩").ToList(),
            AramAugmentRarityFilter.Gold => augments.Where(a => a.Rarity == "黄金").ToList(),
            AramAugmentRarityFilter.Silver => augments.Where(a => a.Rarity == "白银").ToList(),
            _ => augments.ToList()
        };

        var filterBar = Grid(
            [GridSize.Star(), GridSize.Star(), GridSize.Star(), GridSize.Star()],
            [GridSize.Auto],
            [
                BuildFilterChip("全部", activeFilter == AramAugmentRarityFilter.All, () => onFilterChanged(AramAugmentRarityFilter.All))
                    .Margin(0, 0, 4, 0)
                    .Grid(row: 0, column: 0),
                BuildFilterChip("棱彩", activeFilter == AramAugmentRarityFilter.Prismatic, () => onFilterChanged(AramAugmentRarityFilter.Prismatic))
                    .Margin(0, 0, 4, 0)
                    .Grid(row: 0, column: 1),
                BuildFilterChip("黄金", activeFilter == AramAugmentRarityFilter.Gold, () => onFilterChanged(AramAugmentRarityFilter.Gold))
                    .Margin(0, 0, 4, 0)
                    .Grid(row: 0, column: 2),
                BuildFilterChip("白银", activeFilter == AramAugmentRarityFilter.Silver, () => onFilterChanged(AramAugmentRarityFilter.Silver))
                    .Grid(row: 0, column: 3),
            ]);

        var topRows = filtered.Take(20).Select(a =>
        {
            Element icon = !string.IsNullOrWhiteSpace(a.IconUrl)
                ? (a.IconUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? Image(a.IconUrl).Width(28).Height(28).AutomationName(a.Name).VAlign(VerticalAlignment.Center)
                    : Component<LcuImage, LcuImageProps>(new LcuImageProps(a.IconUrl, 28, CornerRadius: 4)).VAlign(VerticalAlignment.Center))
                : Border(Caption(a.Rarity.Length > 0 ? a.Rarity[..1] : "强").Foreground(Theme.AccentText))
                    .Width(28)
                    .Height(28)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Background(Theme.SubtleFill)
                    .VAlign(VerticalAlignment.Center);

            return (Element)Border(
                    HStack(8,
                        icon,
                        VStack(4,
                            HStack(8,
                                BodyStrong(a.Name),
                                Border(Caption(a.Rarity).Foreground(GetRarityBrush(a.Rarity)))
                                    .Padding(8, 2)
                                    .CornerRadius(YuumiTheme.ControlRadius)
                                    .Background(Theme.CardBackground)
                                    .VAlign(VerticalAlignment.Center)),
                            HStack(8,
                                Caption($"T{a.Tier?.ToString() ?? "-"}").Foreground(Theme.SecondaryText),
                                Caption($"胜率 {FormatPct(a.WinRate)}").SemiBold().Foreground(GetWinRateBrush(a.WinRate)),
                                Caption($"选取 {FormatPct(a.PickRate)} · {a.Games} 场").Foreground(Theme.SecondaryText)))
                        .VAlign(VerticalAlignment.Center)))
                .Padding(12, 8)
                .CornerRadius(YuumiTheme.ControlRadius)
                .Background(Theme.SubtleFill);
        }).ToArray();

        var bodyContent = topRows.Length > 0
            ? (Element)VStack(8, topRows)
            : Caption("该稀有度分类下暂无推荐海克斯。").Foreground(Theme.SecondaryText).Padding(8);

        return Border(
                VStack(8,
                    Body($"海克斯强化推荐（共 {filtered.Count} 条）").SemiBold(),
                    filterBar,
                    bodyContent))
            .Padding(12)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.CardBackground)
            .WithBorder(Theme.CardStroke, 1);
    }

    private static Element BuildTwoLineRouteChip(string topText, string bottomText, bool isActive, Action onClick) =>
        Button(
                VStack(2,
                    Caption(topText)
                        .SemiBold()
                        .Foreground(isActive ? Theme.AccentText : Theme.PrimaryText)
                        .TextTrimming(TextTrimming.CharacterEllipsis)
                        .MaxLines(1)
                        .HAlign(HorizontalAlignment.Center),
                    Caption(bottomText)
                        .Foreground(isActive ? Theme.AccentText : Theme.SecondaryText)
                        .HAlign(HorizontalAlignment.Center)),
                onClick)
            .Background(isActive ? Theme.CardBackground : Theme.SubtleFill)
            .WithBorder(isActive ? Theme.AccentText : Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8, 8)
            .AutomationName($"{topText} {bottomText}")
            .HAlign(HorizontalAlignment.Stretch);

    private static Element BuildFilterChip(string label, bool isActive, Action onClick) =>
        Button(
                Caption(label)
                    .Foreground(isActive ? Theme.AccentText : Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center),
                onClick)
            .Background(isActive ? Theme.CardBackground : Theme.SubtleFill)
            .WithBorder(isActive ? Theme.AccentText : Theme.CardStroke, 1)
            .CornerRadius(YuumiTheme.ControlRadius)
            .Padding(8, 4)
            .AutomationName($"筛选海克斯：{label}")
            .HAlign(HorizontalAlignment.Stretch);

    private static (int ModIndex, int KeyIndex) ParseHotkeyIndices(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return (0, 0);
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (0, 0);

        var keyToken = parts[^1];
        var keyIdx = Array.FindIndex(HotkeyKeys, k => string.Equals(k, keyToken, StringComparison.OrdinalIgnoreCase));
        if (keyIdx < 0) keyIdx = 0;

        var modToken = parts.Length > 1 ? string.Join("+", parts[..^1]) : "无修饰键";
        var modIdx = Array.FindIndex(HotkeyModifiers, m => string.Equals(m, modToken, StringComparison.OrdinalIgnoreCase));
        if (modIdx < 0) modIdx = 0;

        return (modIdx, keyIdx);
    }

    private static string BuildHotkeyString(int modIndex, int keyIndex)
    {
        var mod = modIndex >= 0 && modIndex < HotkeyModifiers.Length ? HotkeyModifiers[modIndex] : "Alt";
        var key = keyIndex >= 0 && keyIndex < HotkeyKeys.Length ? HotkeyKeys[keyIndex] : "X";
        return mod == "无修饰键" ? key : $"{mod}+{key}";
    }

    private static string ResolveItemName(int itemId, IReadOnlyDictionary<int, AramItemInfo> itemDict) =>
        itemDict.TryGetValue(itemId, out var info) ? info.Name : $"#{itemId}";

    private static string FormatPct(double value)
    {
        var pct = value <= 1.0 && value > 0 ? value * 100.0 : value;
        return $"{pct:F1}%";
    }
}
