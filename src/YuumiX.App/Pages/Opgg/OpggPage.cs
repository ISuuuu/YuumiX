using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.Opgg;

public sealed record OpggPageProps(int? InitialChampionId = null);

/// <summary>
/// OP.GG 独立窗口主页面组件（头部 Tab 切换、四维筛选栏、梯队/出装视图联动、Toast 反馈）。
/// </summary>
public sealed class OpggPage : Component<OpggPageProps>
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var opggService = services.Services.GetRequiredService<IOpggService>();
        var theme = UseContext(AppHost.ThemeContext) ?? AppHost.Theme;
        bool isDark = string.Equals(theme.Current.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase);

        var (currentView, setCurrentView) = UseState("tier"); // "tier" | "build"
        var (region, setRegion) = UseState(OpggRegion.Kr);
        var (mode, setMode) = UseState(OpggMode.Ranked);
        var (tier, setTier) = UseState(OpggTier.EmeraldPlus);
        var (position, setPosition) = UseState(OpggPosition.Mid);

        var (selectedChampId, setSelectedChampId) = UseState<int?>(Props.InitialChampionId);
        var (tierItems, setTierItems) = UseState<IReadOnlyList<TierListItem>>(Array.Empty<TierListItem>());
        var (buildData, setBuildData) = UseState<OpggBuildData?>(null);
        var (metaVersion, setMetaVersion) = UseState<string?>(null);

        // 游戏资源映射表必须主动触发加载：只同步读 Current 时，本窗口从未有页面
        // 调用过 GetAsync 的话 catalog 永远是空表，装备图标会全部落到错误兜底路径上。
        var (catalog, setCatalog) = UseState(GameDataCatalog.Empty, threadSafe: true);

        var (loading, setLoading) = UseState(false);
        var (errorMessage, setErrorMessage) = UseState<string?>(null);
        var (isApplyingItemSet, setIsApplyingItemSet) = UseState(false);
        // 全局 Toast 服务（R5）：由 OpggWindowRoot Provide，队列渲染在其挂载的 ToastHost。
        var toast = UseContext(AppHost.ToastContext)!;

        async Task FetchTierList()
        {
            setLoading(true);
            setErrorMessage(null);
            try
            {
                var list = await opggService.GetTierListAsync(region, mode, tier, position).ConfigureAwait(false);
                setTierItems(list);
            }
            catch (Exception ex)
            {
                setErrorMessage($"加载梯队排行榜失败: {ex.Message}");
            }
            finally
            {
                setLoading(false);
            }
        }

        async Task FetchBuild(int champId)
        {
            setLoading(true);
            setErrorMessage(null);
            try
            {
                var payload = await opggService.GetBuildAsync(region, mode, tier, champId, position).ConfigureAwait(false);
                if (payload is not null)
                {
                    setBuildData(payload.Data);
                    if (!string.IsNullOrWhiteSpace(payload.Meta?.Version))
                    {
                        setMetaVersion(payload.Meta.Version);
                    }
                }
                else
                {
                    setErrorMessage("未获取到该英雄的出装数据");
                }
            }
            catch (Exception ex)
            {
                setErrorMessage($"加载英雄出装失败: {ex.Message}");
            }
            finally
            {
                setLoading(false);
            }
        }

        // 选人联动事件订阅与初始英雄出装自动定位
        UseEffect(() =>
        {
            var sub = opggService.ChampSelectBuildEvents.Subscribe(new AnonymousObserver<OpggBuildEvent>(e =>
            {
                setSelectedChampId(e.ChampionId);
                if (!string.IsNullOrWhiteSpace(e.Mode))
                {
                    setMode(e.Mode);
                }
                if (!string.IsNullOrWhiteSpace(e.Position))
                {
                    setPosition(e.Position);
                }
                setCurrentView("build");
                _ = FetchBuild(e.ChampionId);
            }));

            // 首次挂载初始化
            if (Props.InitialChampionId.HasValue && Props.InitialChampionId.Value > 0)
            {
                setSelectedChampId(Props.InitialChampionId.Value);
                setCurrentView("build");
                _ = FetchBuild(Props.InitialChampionId.Value);
            }
            else
            {
                var cfg = services.Config.Functions;
                if (cfg.EnableAutoSelectChampion)
                {
                    // 顺序取第一个 > 0 的英雄 ID
                    var candidate = cfg.AutoSelectChampion
                        .Concat(cfg.AutoSelectChampionMid)
                        .Concat(cfg.AutoSelectChampionTop)
                        .Concat(cfg.AutoSelectChampionSup)
                        .Concat(cfg.AutoSelectChampionJug)
                        .Concat(cfg.AutoSelectChampionBot)
                        .FirstOrDefault(id => id > 0);

                    if (candidate > 0)
                    {
                        setSelectedChampId(candidate);
                        setCurrentView("build");
                        _ = FetchBuild(candidate);
                        return () => sub.Dispose();
                    }
                }

                _ = FetchTierList();
            }

            return () => sub.Dispose();
        }, []);

        // 游戏资源映射表加载：失败不提示，客户端未启动是常态，图标占位已足够表达。
        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                _ = LoadCatalogAsync(services, setCatalog, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            []);

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            [
                // 1. 顶部 Header Tab 栏
                RenderHeader(currentView, selectedChampId, metaVersion, view =>
                {
                    setCurrentView(view);
                    if (view == "tier")
                    {
                        _ = FetchTierList();
                    }
                    else if (view == "build" && selectedChampId.HasValue)
                    {
                        _ = FetchBuild(selectedChampId.Value);
                    }
                }).Grid(0, 0),

                // 2. 四维筛选栏
                Component<OpggFilterBarView, OpggFilterBarProps>(new OpggFilterBarProps
                {
                    Region = region,
                    Mode = mode,
                    Tier = tier,
                    Position = position,
                    OnRegionChanged = r =>
                    {
                        setRegion(r);
                        if (currentView == "tier") _ = FetchTierList();
                        else if (selectedChampId.HasValue) _ = FetchBuild(selectedChampId.Value);
                    },
                    OnModeChanged = m =>
                    {
                        setMode(m);
                        if (currentView == "tier") _ = FetchTierList();
                        else if (selectedChampId.HasValue) _ = FetchBuild(selectedChampId.Value);
                    },
                    OnTierChanged = t =>
                    {
                        setTier(t);
                        if (currentView == "tier") _ = FetchTierList();
                        else if (selectedChampId.HasValue) _ = FetchBuild(selectedChampId.Value);
                    },
                    OnPositionChanged = p =>
                    {
                        setPosition(p);
                        if (currentView == "tier") _ = FetchTierList();
                        else if (selectedChampId.HasValue) _ = FetchBuild(selectedChampId.Value);
                    }
                }).Grid(1, 0),

                // 3. 视图主体区域
                RenderBody(
                    currentView,
                    loading,
                    errorMessage,
                    tierItems,
                    buildData,
                    selectedChampId,
                    position,
                    catalog,
                    isDark,
                    isApplyingItemSet,
                    onSelectChamp: champId =>
                    {
                        setSelectedChampId(champId);
                        setCurrentView("build");
                        _ = FetchBuild(champId);
                    },
                    onRetry: () =>
                    {
                        if (currentView == "tier") _ = FetchTierList();
                        else if (selectedChampId.HasValue) _ = FetchBuild(selectedChampId.Value);
                    },
                    onApplyRune: async rune =>
                    {
                        try
                        {
                            var champName = catalog.Champions.TryGetValue(selectedChampId ?? 0, out var cn) ? cn : "Champion";
                            await opggService.ApplyRunePageAsync($"Yuumi: {champName}", rune.PrimaryPageId, rune.SecondaryPageId,
                                [.. rune.PrimaryRuneIds, .. rune.SecondaryRuneIds, .. rune.StatModIds]).ConfigureAwait(false);
                            toast.Success("符文页已应用至客户端");
                        }
                        catch (Exception ex)
                        {
                            toast.Error($"应用符文页失败: {ex.Message}");
                        }
                    },
                    onApplyItemSet: async () =>
                    {
                        if (buildData is null || !selectedChampId.HasValue) return;
                        setIsApplyingItemSet(true);
                        try
                        {
                            var champName = catalog.Champions.TryGetValue(selectedChampId.Value, out var cn) ? cn : "Champion";
                            var res = await opggService.ApplyItemSetAsync(selectedChampId.Value, champName, position, buildData).ConfigureAwait(false);
                            toast.Success(res);
                        }
                        catch (Exception ex)
                        {
                            toast.Error($"应用出装失败: {ex.Message}");
                        }
                        finally
                        {
                            setIsApplyingItemSet(false);
                        }
                    }
                ).Grid(2, 0),

                // 全局 Toast 宿主（R5）：本窗口队列由 OpggWindowRoot 提供
                Component<ToastHost>().Grid(2, 0)
            ]
        );
    }

    private static Element RenderHeader(string currentView, int? selectedChampId, string? metaVersion, Action<string> onSelectView)
    {
        return Border(
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    // 左侧：OP.GG Logo + 品牌文字 + Tab 切换
                    HStack(10,
                        // OP.GG Logo
                        Image(AppAssets.OpggLogoUri)
                            .AutomationName("OPGG品牌Logo")
                            .Width(24)
                            .Height(24)
                            .VAlign(VerticalAlignment.Center),

                        BodyStrong("OP.GG")
                            .Foreground(OpggVisuals.OpggBrandColor)
                            .VAlign(VerticalAlignment.Center),

                        Border(Empty()).Width(8),

                        // 「梯队」Tab
                        Button(
                            currentView == "tier"
                                ? BodyStrong("梯队").Foreground("#FFFFFF")
                                : BodyStrong("梯队"),
                            () => onSelectView("tier"))
                            .Background(currentView == "tier" ? OpggVisuals.ActiveTabGreen : "transparent")
                            .Height(30)
                            .Padding(new Thickness(16, 0, 16, 0))
                            .AutomationName("OPGG梯队Tab"),

                        // 「出装」Tab
                        Button(
                            currentView == "build"
                                ? BodyStrong("出装").Foreground("#FFFFFF")
                                : BodyStrong("出装"),
                            () =>
                            {
                                if (selectedChampId.HasValue) onSelectView("build");
                            })
                            .IsEnabled(selectedChampId.HasValue)
                            .Opacity(selectedChampId.HasValue ? 1.0 : 0.4)
                            .Background(currentView == "build" ? OpggVisuals.ActiveTabGreen : "transparent")
                            .Height(30)
                            .Padding(new Thickness(16, 0, 16, 0))
                            .AutomationName("OPGG出装Tab")
                    )
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 0),

                    // 右侧：游戏版本号
                    string.IsNullOrWhiteSpace(metaVersion)
                        ? Empty().Grid(0, 2)
                        : Caption($"游戏版本: {metaVersion}")
                            .Foreground(Theme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(0, 2)
                ]
            )
        )
        .Padding(new Thickness(16, 10, 16, 10))
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .WithBorder(Theme.CardStroke, 1);
    }

    private static Element RenderBody(
        string currentView,
        bool loading,
        string? errorMessage,
        IReadOnlyList<TierListItem> tierItems,
        OpggBuildData? buildData,
        int? selectedChampId,
        string? position,
        GameDataCatalog catalog,
        bool isDark,
        bool isApplyingItemSet,
        Action<int> onSelectChamp,
        Action onRetry,
        Action<OpggRunePreset> onApplyRune,
        Action onApplyItemSet)
    {
        if (loading)
        {
            return VStack(12,
                ProgressRing().Width(36).Height(36),
                Body("正在拉取 OP.GG 数据...")
                    .Foreground(Theme.SecondaryText)
            )
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return VStack(12,
                TitleLarge("\uE783")
                    .FontFamily("Segoe Fluent Icons")
                    .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                    .HAlign(HorizontalAlignment.Center),
                Body(errorMessage)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center),
                Button("重试", onRetry)
                    .Width(100)
                    .HAlign(HorizontalAlignment.Center)
            )
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
        }

        if (currentView == "build" && buildData is not null && selectedChampId.HasValue)
        {
            return Component<BuildDetailPanelView, BuildDetailPanelProps>(new BuildDetailPanelProps
            {
                Build = buildData,
                ChampionId = selectedChampId.Value,
                Position = position,
                ChampionsMap = catalog.Champions,
                ItemsMap = catalog.Items,
                SpellsMap = catalog.Spells,
                RunesMap = catalog.Runes,
                PerkStyles = catalog.PerkStyles,
                PerkDetails = catalog.PerkDetails,
                IsDark = isDark,
                OnApplyRune = onApplyRune,
                OnApplyItemSet = onApplyItemSet,
                IsApplyingItemSet = isApplyingItemSet
            });
        }

        return Component<TierListPanelView, TierListPanelProps>(new TierListPanelProps
        {
            Items = tierItems,
            ChampionsMap = catalog.Champions,
            IsDark = isDark,
            OnSelectChampion = onSelectChamp
        });
    }

    /// <summary>
    /// 加载游戏资源映射表。失败不提示：客户端未启动是常态，图标占位已经足够表达。
    /// </summary>
    private static async Task LoadCatalogAsync(
        AppServices services,
        Action<GameDataCatalog> apply,
        CancellationToken cancellationToken)
    {
        try
        {
            GameDataCatalog loaded = await services.Services
                .GetRequiredService<GameDataService>()
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!cancellationToken.IsCancellationRequested)
            {
                apply(loaded);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面已卸载
        }
        catch (Exception)
        {
            // 资源表缺失只影响图标，不影响布局
        }
    }
}

file sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => onNext(value);
}
