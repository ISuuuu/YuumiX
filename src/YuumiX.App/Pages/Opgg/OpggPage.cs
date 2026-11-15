using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.Opgg;

public sealed record OpggPageProps(int? InitialChampionId = null);

internal sealed record OpggFilterState(
    string View,
    string Region,
    string Mode,
    string Tier,
    string Position,
    int? SelectedChampionId);

internal sealed record OpggTierResult(string Key, IReadOnlyList<TierListItem> Items);

internal sealed record OpggBuildResult(string Key, OpggBuildPayload? Payload);

internal sealed record ApplyItemSetInput(
    string QueryKey,
    int ChampionId,
    string ChampionName,
    string Position,
    OpggBuildData BuildData);

internal static class OpggEffects
{
    public static string TierKey(OpggFilterState f) =>
        $"tier|{f.Region}|{f.Mode}|{f.Tier}|{f.Position}";

    public static string BuildKey(OpggFilterState f) =>
        $"build|{f.Region}|{f.Mode}|{f.Tier}|{f.Position}|{f.SelectedChampionId ?? 0}";

    public static OpggFilterState ResolveInitialFilterState(int? initialChampionId, FunctionsConfig functions)
    {
        if (initialChampionId is > 0)
        {
            return new OpggFilterState("build", OpggRegion.Kr, OpggMode.Ranked, OpggTier.EmeraldPlus, OpggPosition.Mid, initialChampionId.Value);
        }

        if (functions.EnableAutoSelectChampion)
        {
            var candidate = functions.AutoSelectChampion
                .Concat(functions.AutoSelectChampionMid)
                .Concat(functions.AutoSelectChampionTop)
                .Concat(functions.AutoSelectChampionSup)
                .Concat(functions.AutoSelectChampionJug)
                .Concat(functions.AutoSelectChampionBot)
                .FirstOrDefault(id => id > 0);

            if (candidate > 0)
            {
                return new OpggFilterState("build", OpggRegion.Kr, OpggMode.Ranked, OpggTier.EmeraldPlus, OpggPosition.Mid, candidate);
            }
        }

        return new OpggFilterState("tier", OpggRegion.Kr, OpggMode.Ranked, OpggTier.EmeraldPlus, OpggPosition.Mid, null);
    }

    public static OpggFilterState ApplyChampSelectEvent(OpggFilterState current, OpggBuildEvent e) =>
        current with
        {
            View = "build",
            SelectedChampionId = e.ChampionId,
            Mode = string.IsNullOrWhiteSpace(e.Mode) ? current.Mode : e.Mode,
            Position = string.IsNullOrWhiteSpace(e.Position) ? current.Position : e.Position,
        };

    public static IReadOnlyList<TierListItem> CurrentTierItems(AsyncValue<OpggTierResult?> resource, string key)
    {
        var result = resource switch
        {
            AsyncValue<OpggTierResult?>.Data data => data.Value,
            AsyncValue<OpggTierResult?>.Reloading reloading => reloading.Previous,
            _ => null,
        };
        return result?.Key == key ? result.Items : Array.Empty<TierListItem>();
    }

    public static OpggBuildPayload? CurrentBuildPayload(AsyncValue<OpggBuildResult?> resource, string key)
    {
        var result = resource switch
        {
            AsyncValue<OpggBuildResult?>.Data data => data.Value,
            AsyncValue<OpggBuildResult?>.Reloading reloading => reloading.Previous,
            _ => null,
        };
        return result?.Key == key ? result.Payload : null;
    }
}

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
        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(() =>
        {
            var subscription = theme.Subscribe(setPalette);
            return () => subscription.Dispose();
        }, 0);
        bool isDark = palette.IsDark;

        var (filter, setFilter) = UseState(
            OpggEffects.ResolveInitialFilterState(Props.InitialChampionId, services.Config.Functions),
            threadSafe: true);
        var (retryVersion, setRetryVersion) = UseState(0);
        var (lastMetaVersion, setLastMetaVersion) = UseState<string?>(null);

        var (lang, setLang) = UseState(services.I18n.CurrentLanguage, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = services.I18n.CurrentLanguageChanged.Subscribe(setLang);
                return () => subscription.Dispose();
            },
            0);

        // 游戏资源映射表必须主动触发加载：只同步读 Current 时，本窗口从未有页面
        // 调用过 GetAsync 的话 catalog 永远是空表，装备图标会全部落到错误兜底路径上。
        var (catalog, setCatalog) = UseState(GameDataCatalog.Empty, threadSafe: true);

        var reducedMotion = UseReducedMotion();
        var highContrast = UseHighContrast();
        // 全局 Toast 服务（R5）：由 OpggWindowRoot Provide，队列渲染在其挂载的 ToastHost。
        var toast = UseContext(AppHost.ToastContext)!;

        string tierKey = OpggEffects.TierKey(filter);
        string buildKey = OpggEffects.BuildKey(filter);
        var filterRef = UseRef(filter);
        filterRef.Current = filter;

        var tierResource = UseResource(
            async ct =>
            {
                if (filter.View != "tier")
                {
                    return null;
                }

                var list = await opggService
                    .GetTierListAsync(filter.Region, filter.Mode, filter.Tier, filter.Position, ct)
                    .ConfigureAwait(false);
                return new OpggTierResult(tierKey, list);
            },
            deps: new object[]
            {
                filter.View == "tier",
                filter.Region,
                filter.Mode,
                filter.Tier,
                filter.Position,
                retryVersion,
            });

        var buildResource = UseResource(
            async ct =>
            {
                if (filter.View != "build" || !filter.SelectedChampionId.HasValue)
                {
                    return null;
                }

                var payload = await opggService
                    .GetBuildAsync(filter.Region, filter.Mode, filter.Tier, filter.SelectedChampionId.Value, filter.Position, ct)
                    .ConfigureAwait(false);
                return new OpggBuildResult(buildKey, payload);
            },
            deps: new object[]
            {
                filter.View == "build",
                filter.Region,
                filter.Mode,
                filter.Tier,
                filter.Position,
                filter.SelectedChampionId ?? 0,
                retryVersion,
            });

        var tierItems = OpggEffects.CurrentTierItems(tierResource, tierKey);
        var buildPayload = OpggEffects.CurrentBuildPayload(buildResource, buildKey);
        var buildData = buildPayload?.Data;
        var activeVersion = !string.IsNullOrWhiteSpace(buildPayload?.Meta?.Version)
            ? buildPayload!.Meta!.Version
            : lastMetaVersion;

        if (!string.IsNullOrWhiteSpace(buildPayload?.Meta?.Version) && buildPayload.Meta.Version != lastMetaVersion)
        {
            setLastMetaVersion(buildPayload.Meta.Version);
        }

        bool loading = filter.View == "tier"
            ? tierResource is AsyncValue<OpggTierResult?>.Loading or AsyncValue<OpggTierResult?>.Reloading
            : buildResource is AsyncValue<OpggBuildResult?>.Loading or AsyncValue<OpggBuildResult?>.Reloading;

        string? errorMessage = filter.View == "tier"
            ? tierResource switch
            {
                AsyncValue<OpggTierResult?>.Error err => $"加载梯队排行榜失败: {err.Exception.Message}",
                _ => null,
            }
            : buildResource switch
            {
                AsyncValue<OpggBuildResult?>.Error err => $"加载英雄出装失败: {err.Exception.Message}",
                AsyncValue<OpggBuildResult?>.Data data when data.Value?.Key == buildKey && data.Value.Payload is null => "未获取到该英雄的出装数据",
                _ => null,
            };

        var applyRuneMutation = UseMutation<(string QueryKey, int ChampionId, string ChampionName, OpggRunePreset Rune), bool>(
            mutator: async (input, ct) =>
            {
                await opggService.ApplyRunePageAsync(
                    $"Yuumi: {input.ChampionName}",
                    input.Rune.PrimaryPageId,
                    input.Rune.SecondaryPageId,
                    (int[])[.. input.Rune.PrimaryRuneIds, .. input.Rune.SecondaryRuneIds, .. input.Rune.StatModIds],
                    ct).ConfigureAwait(false);
                return true;
            },
            options: new MutationOptions<(string QueryKey, int ChampionId, string ChampionName, OpggRunePreset Rune), bool>(
                OnSuccess: (_, _) => toast.Success(services.I18n.T("opgg.runes.appliedSuccess", "符文页已应用至客户端")),
                OnError: (ex, _) => toast.Error(ex.Message.Contains("未连接")
                    ? "请先连接客户端"
                    : services.I18n.T("opgg.runes.appliedFailed", "应用符文页失败: ") + ex.Message)));

        var applyItemSetMutation = UseMutation<ApplyItemSetInput, string>(
            mutator: (input, ct) => opggService.ApplyItemSetAsync(
                input.ChampionId,
                input.ChampionName,
                input.Position,
                input.BuildData,
                ct),
            options: new MutationOptions<ApplyItemSetInput, string>(
                OnSuccess: (res, _) => toast.Success(res),
                OnError: (ex, _) => toast.Error(ex.Message.Contains("未连接")
                    ? "请先连接客户端"
                    : services.I18n.T("opgg.items.appliedFailed", "应用出装失败: ") + ex.Message)));

        // 选人联动事件订阅：在 UI 线程内一次更新整组筛选，统一由 UseResource 触发请求。
        UseEffect(() =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            var sub = opggService.ChampSelectBuildEvents.Subscribe(new AnonymousObserver<OpggBuildEvent>(e =>
            {
                void Apply() => setFilter(OpggEffects.ApplyChampSelectEvent(filterRef.Current, e));
                if (dispatcher is not null && !dispatcher.HasThreadAccess)
                {
                    dispatcher.TryEnqueue(Apply);
                }
                else
                {
                    Apply();
                }
            }));

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
                RenderHeader(filter.View, filter.SelectedChampionId, activeVersion, view =>
                {
                    setFilter(filterRef.Current with { View = view });
                }).Grid(0, 0),

                // 2. 四维筛选栏
                Component<OpggFilterBarView, OpggFilterBarProps>(new OpggFilterBarProps
                {
                    Region = filter.Region,
                    Mode = filter.Mode,
                    Tier = filter.Tier,
                    Position = filter.Position,
                    OnRegionChanged = r => setFilter(filterRef.Current with { Region = r }),
                    OnModeChanged = m => setFilter(filterRef.Current with { Mode = m }),
                    OnTierChanged = t => setFilter(filterRef.Current with { Tier = t }),
                    OnPositionChanged = p => setFilter(filterRef.Current with { Position = p }),
                }).Grid(1, 0),

                // 3. 视图主体区域
                OpggMotion.View(
                    RenderBody(
                        filter.View,
                        loading,
                        errorMessage,
                        tierItems,
                        buildData,
                        filter.SelectedChampionId,
                        filter.Position,
                        catalog,
                        isDark,
                        applyItemSetMutation.IsPending,
                        onSelectChamp: champId => setFilter(filterRef.Current with { SelectedChampionId = champId, View = "build" }),
                        onRetry: () => setRetryVersion(retryVersion + 1),
                        onApplyRune: rune =>
                        {
                            var current = filterRef.Current;
                            if (current.View != "build" || !current.SelectedChampionId.HasValue || OpggEffects.BuildKey(current) != buildKey)
                            {
                                return;
                            }

                            var champName = catalog.Champions.TryGetValue(current.SelectedChampionId.Value, out var cn) ? cn : "Champion";
                            _ = applyRuneMutation.RunAsync((buildKey, current.SelectedChampionId.Value, champName, rune));
                        },
                        onApplyItemSet: () =>
                        {
                            var current = filterRef.Current;
                            if (buildData is null || current.View != "build" || !current.SelectedChampionId.HasValue || OpggEffects.BuildKey(current) != buildKey)
                            {
                                return;
                            }

                            var champName = catalog.Champions.TryGetValue(current.SelectedChampionId.Value, out var cn) ? cn : "Champion";
                            _ = applyItemSetMutation.RunAsync(new ApplyItemSetInput(
                                buildKey,
                                current.SelectedChampionId.Value,
                                champName,
                                current.Position,
                                buildData));
                        }
                    ),
                    OpggMotion.BuildTrigger(
                        filter.View,
                        filter.SelectedChampionId,
                        isReady: (filter.View == "build" && buildData is not null && filter.SelectedChampionId.HasValue) ||
                                 (filter.View == "tier" && tierItems.Count > 0 && errorMessage is null)),
                    reducedMotion || highContrast).Grid(2, 0)
            ]
            // Toast 宿主不在这里挂：OpggWindowRoot 已统一挂载（同主窗口 AppShell 的模式），
            // 页面再挂一个会对同一队列重复渲染出两条 toast。
        );
    }

    private static Element RenderHeader(string currentView, int? selectedChampId, string? metaVersion, Action<string> onSelectView)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        var tierTabText = i18n?.T("opgg.tabs.tier", "梯队") ?? "梯队";
        var buildTabText = i18n?.T("opgg.tabs.build", "出装") ?? "出装";

        return Border(
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    // 左侧：OP.GG Logo + 品牌文字 + Tab 切换
                    HStack(12,
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
                        RenderTab(tierTabText, "OPGG梯队Tab", currentView == "tier", true, () => onSelectView("tier")),

                        // 「出装」Tab
                        RenderTab(buildTabText, "OPGG出装Tab", currentView == "build", selectedChampId.HasValue,
                            () => { if (selectedChampId.HasValue) onSelectView("build"); })
                    )
                    .VAlign(VerticalAlignment.Center)
                    .Grid(0, 0),

                    // 右侧：游戏版本号
                    string.IsNullOrWhiteSpace(metaVersion)
                        ? Empty().Grid(0, 2)
                        : Caption($"{i18n?.T("opgg.gameVersion", "游戏版本: ") ?? "游戏版本: "}{metaVersion}")
                            .Foreground(Theme.SecondaryText)
                            .VAlign(VerticalAlignment.Center)
                            .Grid(0, 2)
                ]
            )
        )
        .Padding(new Thickness(16, 8, 16, 8))
        .BorderThickness(new Thickness(0, 0, 0, 1))
        .WithBorder(Theme.CardStroke, 1);
    }

    internal static ButtonElement RenderTab(string label, string automationName, bool isSelected, bool isEnabled, Action onSelect) =>
        Button(label, onSelect)
            .IsEnabled(isEnabled)
            .Height(30)
            .Padding(new Thickness(16, 0, 16, 0))
            .CornerRadius(YuumiTheme.ControlRadius)
            .Background(isSelected ? Theme.Accent : Theme.Ref("SubtleFillColorTransparentBrush"))
            .Foreground(!isEnabled ? Theme.DisabledText : isSelected ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.PrimaryText)
            .TogglePointerOverStates(isSelected)
            .AutomationName(automationName);

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
        var i18n = Composition.AppHost.Services?.I18n;

        // 若当前出装或梯队已有内容，优先保留视图呈现，避免切换分路或取数时整个卡片坍塌为居中菊花
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

        if (currentView == "tier" && tierItems.Count > 0)
        {
            return Component<TierListPanelView, TierListPanelProps>(new TierListPanelProps
            {
                Items = tierItems,
                ChampionsMap = catalog.Champions,
                IsDark = isDark,
                OnSelectChampion = onSelectChamp
            });
        }

        if (loading)
        {
            return VStack(12,
                ProgressRing().Width(36).Height(36),
                Body(i18n?.T("opgg.loadingData", "正在拉取 OP.GG 数据...") ?? "正在拉取 OP.GG 数据...")
                    .Foreground(Theme.SecondaryText)
            )
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return VStack(12,
                TitleLarge(Icons.Error)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                    .HAlign(HorizontalAlignment.Center),
                Body(errorMessage)
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center),
                Button(i18n?.T("common.retry", "重试") ?? "重试", onRetry)
                    .Width(100)
                    .HAlign(HorizontalAlignment.Center)
                    .AutomationName("重试")
            )
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);
        }

        return Empty();
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
