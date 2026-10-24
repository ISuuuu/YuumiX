using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Navigation;
using YuumiX.App.Theming;
using YuumiX.Core;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.Career.Models;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using Windows.ApplicationModel.DataTransfer;

namespace YuumiX.App.Pages.Career;

/// <summary>生涯页的三个页签。顺序即界面顺序（生涯战绩 / 战利品 / 英雄熟练度）。</summary>
internal enum CareerTab
{
    Matches,
    Loot,
    Mastery,
}

/// <summary>
/// 生涯页：资料卡 + 三页签，对应原 <c>Career.vue</c> 与其三个子组件。
/// 严格对齐 docs/pic/生涯.png、生涯-战利品.png、生涯-熟练度.png。
/// </summary>
public sealed class CareerPage : Component
{
    /// <summary>
    /// 一页对局数。LCU 的 <c>matches</c> 接口一次登录只缓存最近 20 局，
    /// 与原生涯页 <c>careerGamesNumber = 20</c> 一致。
    /// </summary>
    private const int PageSize = 20;

    /// <summary>空列表共享实例（<c>UseState</c> 初值只读一次，集合表达式会每次新建，REACTOR_HOOKS_013）。</summary>
    private static readonly IReadOnlyList<MatchSummary> EmptyMatches = [];

    /// <summary>空战利品选中集合共享实例。</summary>
    private static readonly IReadOnlySet<string> EmptyLootSelection = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>队列筛选项：与原版同一组队列，索引 0 表示"全部"。</summary>
    private static readonly string[] QueueLabels =
    [
        "全部",
        "单双排位",
        "灵活排位",
        "匹配模式",
        "极地大乱斗",
        "海克斯大乱斗",
        "经典海斗",
    ];

    /// <summary>与 <see cref="QueueLabels"/> 一一对应的队列 ID；0 表示不过滤。</summary>
    private static readonly int[] QueueIds = [0, 420, 440, 430, 450, 2400, 2450];

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var summonerService = services.Services.GetRequiredService<ISummonerService>();
        var careerService = services.Services.GetRequiredService<ICareerService>();
        var matchService = services.Services.GetRequiredService<IMatchService>();
        var lifecycle = services.Services.GetService<GameLifecycleService>();

        var nav = UseNavigation<AppRoute>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        var (ranked, setRanked) = UseState(RankedStats.Empty, threadSafe: true);
        var (matches, setMatches) = UseState(EmptyMatches, threadSafe: true);
        var (loot, setLoot) = UseState(PlayerLootOverview.Sample, threadSafe: true);
        var (mastery, setMastery) = UseState(MasteryOverview.Sample, threadSafe: true);
        var (catalog, setCatalog) = UseState(GameDataCatalog.Empty, threadSafe: true);
        var (tab, setTab) = UseState(CareerTab.Matches, threadSafe: true);
        var (queueFilter, setQueueFilter) = UseState(0, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (copied, setCopied) = UseState(false, threadSafe: true);
        var (refreshToken, setRefreshToken) = UseState(0, threadSafe: true);
        var (teammates, setTeammates) = UseState<RecentTeammatesResult?>(null, threadSafe: true);
        var (loadingTeammates, setLoadingTeammates) = UseState(false, threadSafe: true);
        var (targetPuuid, setTargetPuuid) = UseState<string?>(null, threadSafe: true);

        // 外部跳转（如对局信息玩家卡片点击跳转生涯）
        UseEffect(
            () =>
            {
                var (pendingPuuid, pendingProfile) = CareerNavigationPayload.Instance.Consume();
                if (!string.IsNullOrWhiteSpace(pendingPuuid))
                {
                    setTargetPuuid(pendingPuuid);
                    if (pendingProfile is not null)
                    {
                        setSummoner(pendingProfile);
                    }
                }
                return static () => { };
            },
            0);

        // 战利品交互与过滤状态
        var (selectedLootIds, setSelectedLootIds) = UseState(EmptyLootSelection, threadSafe: true);
        var (lootTypeFilter, setLootTypeFilter) = UseState(0, threadSafe: true);
        var (lootOwnedFilter, setLootOwnedFilter) = UseState(0, threadSafe: true);
        var (lootValueType, setLootValueType) = UseState(0, threadSafe: true);
        var (lootOperator, setLootOperator) = UseState(0, threadSafe: true);
        var (lootThreshold, setLootThreshold) = UseState(string.Empty, threadSafe: true);
        var (lootProgress, setLootProgress) = UseState<LootOperationProgress?>(null, threadSafe: true);
        var (lootNotice, setLootNotice) = UseState<string?>(null, threadSafe: true);

        // 英雄熟练度筛选与排序状态
        var (masterySearch, setMasterySearch) = UseState(string.Empty, threadSafe: true);
        var (masteryChest, setMasteryChest) = UseState(0, threadSafe: true);
        var (masteryLevel, setMasteryLevel) = UseState(0, threadSafe: true);
        var (masterySort, setMasterySort) = UseState(0, threadSafe: true);

        // 召唤师变更时清空队友缓存与战利品选中
        UseEffect(
            () =>
            {
                setTeammates(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                return static () => { };
            },
            summoner?.Puuid ?? string.Empty);

        // 对局结束与新对局开始自动刷新
        UseEffect(
            () =>
            {
                if (lifecycle is null) return static () => { };
                var sub1 = lifecycle.GameEnded.Subscribe(time =>
                {
                    Task.Run(async () =>
                    {
                        setRefreshToken(refreshToken + 1);
                        await Task.Delay(2000);
                        setRefreshToken(refreshToken + 2);
                    });
                });
                var sub2 = lifecycle.NewGameStarted.Subscribe(time =>
                {
                    setRefreshToken(refreshToken + 1);
                });
                return () =>
                {
                    sub1.Dispose();
                    sub2.Dispose();
                };
            },
            0);

        // 资源表：客户端未启动时保持空表，英雄图标退化为占位块而不是空白。
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
            0);

        // 首屏与刷新共用一条链路：refreshToken 变化即重新取数，token > 0 时强制绕过排位缓存。
        UseEffect(
            () =>
            {
                var cancellation = new CancellationTokenSource();
                _ = LoadAsync(
                    summonerService,
                    careerService,
                    matchService,
                    targetPuuid,
                    summoner,
                    refreshToken > 0,
                    setSummoner,
                    setRanked,
                    setMatches,
                    setLoot,
                    setMastery,
                    setLoading,
                    cancellation.Token);

                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            string.Concat(refreshToken.ToString(), ":", targetPuuid ?? string.Empty));

        // "已复制"提示 1.5 秒后自动消失。
        UseEffect(
            () =>
            {
                if (!copied)
                {
                    return static () => { };
                }

                var cancellation = new CancellationTokenSource();
                _ = ResetCopiedAsync(setCopied, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            copied ? 1 : 0);

        int filter = queueFilter >= 0 && queueFilter < QueueIds.Length ? queueFilter : 0;
        MatchSummary[] visibleMatches = filter == 0
            ? [.. matches]
            : [.. matches.Where(match => match.QueueId == QueueIds[filter])];

        RecentFormSummary summary = RecentFormSummary.Compute(matches);

        void LoadTeammates()
        {
            if (summoner is null || matches.Count == 0 || loadingTeammates)
            {
                return;
            }
            setLoadingTeammates(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    var res = await careerService.GetRecentTeammatesAsync(
                        summoner.Puuid,
                        matches.Select(m => m.GameId).ToArray(),
                        summoner.Puuid);
                    setTeammates(res);
                }
                finally
                {
                    setLoadingTeammates(false);
                }
            });
        }

        void OnClickTeammate(RecentTeammate tm)
        {
            SearchNavigationPayload.Instance.Set(tm.Name, tm.Puuid);
            nav.Replace(AppRoute.Search);
        }

        void HandleOpenLootItem(OpenableLootItem item)
        {
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var ingredients = item.NeedKey
                    ? new[] { item.LootId, item.KeyLootId ?? "MATERIAL_key" }
                    : new[] { item.LootId };
                var res = await careerService.OpenLootAsync(
                    item.RecipeName,
                    ingredients,
                    1,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setLootNotice(res.Success ? $"开启成功：{string.Join(", ", res.Rewards)}" : $"开启失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        void HandleSmartOpenAll()
        {
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.SmartOpenAllLootsAsync(
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setLootNotice(res.Success ? $"全部开启完成：成功 {res.SucceededCount} 个" : $"开启失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        void HandleForgeKey()
        {
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.ForgeKeyAsync();
                setLootNotice(res.Success ? "钥匙合成成功！" : $"钥匙合成失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        void HandleToggleSelect(string lootId)
        {
            var next = new HashSet<string>(selectedLootIds, StringComparer.Ordinal);
            if (!next.Add(lootId))
            {
                next.Remove(lootId);
            }
            setSelectedLootIds(next);
        }

        void HandleSelectAllFiltered()
        {
            var raw = loot.InventoryItems.Count > 0 ? loot.InventoryItems : PlayerLootOverview.Sample.InventoryItems;
            var filtered = LootManagerView.FilterInventory(
                raw,
                lootTypeFilter,
                lootOwnedFilter,
                lootValueType,
                lootOperator,
                lootThreshold);
            var next = new HashSet<string>(selectedLootIds, StringComparer.Ordinal);
            foreach (var item in filtered)
            {
                next.Add(item.LootId);
            }
            setSelectedLootIds(next);
        }

        void HandleClearSelection()
        {
            setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
        }

        void HandleDisenchant()
        {
            var raw = loot.InventoryItems.Count > 0 ? loot.InventoryItems : PlayerLootOverview.Sample.InventoryItems;
            var itemsToDisenchant = raw
                .Where(i => selectedLootIds.Contains(i.LootId))
                .Select(i => (i.LootId, i.Count))
                .ToList();

            if (itemsToDisenchant.Count == 0) return;

            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.DisenchantLootsAsync(
                    itemsToDisenchant,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                setLootNotice(res.Success ? $"批量分解完成：成功 {res.SucceededCount} 种" : $"分解失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        void HandleReroll()
        {
            var raw = loot.InventoryItems.Count > 0 ? loot.InventoryItems : PlayerLootOverview.Sample.InventoryItems;
            var flatLootIds = new List<string>();
            foreach (var item in raw)
            {
                if (selectedLootIds.Contains(item.LootId))
                {
                    for (int c = 0; c < item.Count; c++)
                    {
                        flatLootIds.Add(item.LootId);
                    }
                }
            }

            if (flatLootIds.Count < 3 || flatLootIds.Count % 3 != 0) return;

            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.RerollLootsAsync(
                    flatLootIds,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                setLootNotice(res.Success ? $"重铸完成：获得 {string.Join(", ", res.Rewards)}" : $"重铸失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        void HandleUpgrade()
        {
            var raw = loot.InventoryItems.Count > 0 ? loot.InventoryItems : PlayerLootOverview.Sample.InventoryItems;
            var itemsToUpgrade = raw
                .Where(i => selectedLootIds.Contains(i.LootId))
                .Select(i => (i.LootId, i.Count))
                .ToList();

            if (itemsToUpgrade.Count == 0) return;

            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.UpgradeLootsAsync(
                    itemsToUpgrade,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                setLootNotice(res.Success ? $"升级/解锁完成：成功 {res.SucceededCount} 种" : $"升级失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        // 「生涯战绩」页签：排位表 + 近期对局看板（含下拉） + 战绩卡片列表
        Element MatchesTab() => VStack(12,
            RankedTableView.Render(ranked, palette),
            RecentSummaryView.Render(
                summary,
                catalog,
                palette,
                QueueLabels,
                filter,
                index =>
                {
                    if (index != queueFilter)
                    {
                        setQueueFilter(index);
                    }
                },
                onRecentTeammates: LoadTeammates,
                teammates: teammates,
                loadingTeammates: loadingTeammates,
                onClickTeammate: OnClickTeammate),
            visibleMatches.Length > 0
                ? VStack(6, visibleMatches
                    .Select(match => CareerMatchRow
                        .Render(match, catalog, palette, () => nav.Replace(AppRoute.Search))
                        .WithKey(match.GameId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToArray())
                : CareerVisuals.Card(
                    Caption(loading
                        ? "正在读取最近对局…"
                        : filter == 0
                            ? "没有可显示的对局（客户端未连接，或该账号近期没有对局）"
                            : "该队列下没有近期对局"),
                    padding: 16));

        Element header = summoner is null
            ? EmptyHeader(loading)
            : CareerProfileHeader.Render(
                summoner,
                palette,
                copied,
                onCopy: () => CopyRiotId(summoner, setCopied),
                onRefresh: () => setRefreshToken(refreshToken + 1),
                onHistory: () => nav.Replace(AppRoute.Search));

        // 页签栏：对齐原版设计，带底部分割线
        Element tabBar = summoner is null
            ? Empty()
            : VStack(8,
                HStack(8,
                    TabButton("生涯战绩", CareerTab.Matches, tab, setTab, palette),
                    TabButton("战利品", CareerTab.Loot, tab, setTab, palette),
                    TabButton("英雄熟练度", CareerTab.Mastery, tab, setTab, palette)),
                Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch));

        Element content = tab switch
        {
            CareerTab.Loot => LootManagerView.Render(
                loot,
                palette,
                onRefresh: () => setRefreshToken(refreshToken + 1),
                onOpenAll: HandleSmartOpenAll,
                onOpenItem: HandleOpenLootItem,
                onForgeKey: HandleForgeKey,
                typeFilterIndex: lootTypeFilter,
                onTypeFilterChanged: setLootTypeFilter,
                ownedFilterIndex: lootOwnedFilter,
                onOwnedFilterChanged: setLootOwnedFilter,
                valueTypeIndex: lootValueType,
                onValueTypeChanged: setLootValueType,
                operatorIndex: lootOperator,
                onOperatorChanged: setLootOperator,
                valueThreshold: lootThreshold,
                onValueThresholdChanged: setLootThreshold,
                selectedLootIds: selectedLootIds,
                onToggleSelect: HandleToggleSelect,
                onSelectAllFiltered: HandleSelectAllFiltered,
                onClearSelection: HandleClearSelection,
                onDisenchant: HandleDisenchant,
                onReroll: HandleReroll,
                onUpgrade: HandleUpgrade,
                progress: lootProgress,
                operationNotice: lootNotice),
            CareerTab.Mastery => ChampionMasteryView.Render(
                mastery,
                catalog,
                palette,
                onRefresh: () => setRefreshToken(refreshToken + 1),
                searchQuery: masterySearch,
                onSearchChanged: setMasterySearch,
                chestFilterIndex: masteryChest,
                onChestFilterChanged: setMasteryChest,
                levelFilterIndex: masteryLevel,
                onLevelFilterChanged: setMasteryLevel,
                sortIndex: masterySort,
                onSortChanged: setMasterySort),
            _ => MatchesTab(),
        };

        const double MaxContentWidth = 1000;
        Element body = VStack(16, header, tabBar, content)
            .MaxWidth(MaxContentWidth)
            .HAlign(HorizontalAlignment.Center);

        return ScrollView(
            Border(body)
                .Padding(24)
                .HAlign(HorizontalAlignment.Stretch));
    }

    /// <summary>未登录时的资料卡占位：说明原因而不是留白。</summary>
    private static Element EmptyHeader(bool loading) =>
        CareerVisuals.Card(
            VStack(6,
                TextBlock(loading ? "正在读取生涯数据…" : "未读取到当前召唤师（客户端未登录）")
                    .FontSize(14)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold),
                Caption("启动并登录英雄联盟客户端后，这里会显示头像、等级与排位成绩。")),
            padding: 20);

    /// <summary>页签按钮：选中时用强调色文字 + 浅色底色与边框，未选中走次要文字色。</summary>
    private static Element TabButton(
        string label,
        CareerTab target,
        CareerTab current,
        Action<CareerTab> setTab,
        YuumiPalette palette)
    {
        bool isSelected = target == current;
        Element button = Button(label, () =>
        {
            if (target == current)
            {
                return;
            }

            setTab(target);
        }).Height(34).Padding(16, 0).FontSize(13).AutomationName(label);

        return isSelected
            ? button
                .Background(CareerVisuals.WithAlpha(palette.Accent, 0x1F))
                .Foreground(palette.Accent)
                .FontWeight(Microsoft.UI.Text.FontWeights.Bold)
                .WithBorder(CareerVisuals.WithAlpha(palette.Accent, 0x33), 1)
            : button
                .Foreground(Theme.SecondaryText)
                .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold);
    }

    /// <summary>
    /// 复制 Riot ID 到剪贴板。
    /// </summary>
    private static void CopyRiotId(SummonerProfile summoner, Action<bool> setCopied)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(summoner.RiotId);
            Clipboard.SetContent(package);
            setCopied(true);
        }
        catch (Exception)
        {
            setCopied(false);
        }
    }

    private static async Task ResetCopiedAsync(Action<bool> setCopied, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            setCopied(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 召唤师 → 排位总览 + 最近 20 局 + 战利品 + 英雄熟练度。
    /// 并行拉取，失败只显示空态或降级数据，不弹框打扰。
    /// </summary>
    private static async Task LoadAsync(
        ISummonerService summonerService,
        ICareerService careerService,
        IMatchService matchService,
        string? targetPuuid,
        SummonerProfile? preloadedProfile,
        bool forceRefresh,
        Action<SummonerProfile?> applySummoner,
        Action<RankedStats> applyRanked,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<PlayerLootOverview> applyLoot,
        Action<MasteryOverview> applyMastery,
        Action<bool> applyLoading,
        CancellationToken cancellationToken)
    {
        try
        {
            applyLoading(true);

            SummonerProfile? profile;
            if (!string.IsNullOrWhiteSpace(targetPuuid))
            {
                if (preloadedProfile is not null && preloadedProfile.Puuid == targetPuuid)
                {
                    profile = preloadedProfile;
                }
                else
                {
                    SummonerProfile? current = await summonerService
                        .GetCurrentSummonerAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (current?.Puuid == targetPuuid)
                    {
                        profile = current;
                    }
                    else
                    {
                        profile = preloadedProfile ?? new SummonerProfile
                        {
                            AccountId = 0,
                            SummonerId = 0,
                            Puuid = targetPuuid,
                            DisplayName = "召唤师",
                            GameName = "召唤师",
                            TagLine = string.Empty,
                            SummonerLevel = 0,
                            ProfileIconId = 29,
                            PercentCompleteForNextLevel = 0,
                            XpSinceLastLevel = 0,
                            XpUntilNextLevel = 0,
                        };
                    }
                }
            }
            else
            {
                profile = await summonerService
                    .GetCurrentSummonerAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            applySummoner(profile);
            if (profile is null)
            {
                applyRanked(RankedStats.Empty);
                applyMatches(EmptyMatches);
                applyLoot(PlayerLootOverview.Sample);
                applyMastery(MasteryOverview.Sample);
                return;
            }

            Task<RankedStats> rankedTask = careerService.GetRankedStatsAsync(
                profile.Puuid, forceRefresh, cancellationToken);

            Task<MatchHistoryPage> historyTask = matchService.GetMatchHistoryAsync(
                profile.Puuid, 0, PageSize - 1, cancellationToken);

            bool isMe = string.IsNullOrWhiteSpace(targetPuuid);
            Task<PlayerLootOverview> lootTask = isMe
                ? careerService.GetLootOverviewAsync(forceRefresh, cancellationToken)
                : Task.FromResult(PlayerLootOverview.Sample);

            Task<MasteryOverview> masteryTask = isMe
                ? careerService.GetMasteryOverviewAsync(forceRefresh, cancellationToken)
                : Task.FromResult(MasteryOverview.Sample);

            await Task.WhenAll(rankedTask, historyTask, lootTask, masteryTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            applyRanked(await rankedTask.ConfigureAwait(false));
            applyMatches((await historyTask.ConfigureAwait(false)).Games);
            applyLoot(await lootTask.ConfigureAwait(false));
            applyMastery(await masteryTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            applyRanked(RankedStats.Empty);
            applyMatches(EmptyMatches);
            applyLoot(PlayerLootOverview.Sample);
            applyMastery(MasteryOverview.Sample);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                applyLoading(false);
            }
        }
    }

    /// <summary>加载游戏资源映射表。</summary>
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
        }
        catch (Exception)
        {
        }
    }
}
