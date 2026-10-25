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

/// <summary>生涯页的页签。顺序即界面顺序（生涯战绩 / 战利品 / 英雄熟练度）；查看他人时隐藏页签栏。</summary>
internal enum CareerTab
{
    Matches,
    Loot,
    Mastery,
}

/// <summary>
/// 当前生涯的查看目标：<c>null</c> 表示自己；非 <c>null</c> 表示查看他人（需求 §2.8）。
/// puuid 与预载资料合成一个状态，避免"目标换了、资料还是上一个人"的中间帧。
/// </summary>
public sealed record CareerViewTarget(string Puuid, SummonerProfile? Preloaded);

/// <summary>
/// 生涯页：资料卡 + 页签，对应原 <c>Career.vue</c> 与其三个子组件。
///
/// 查看他人生涯（§2.8）：其他页面通过 <see cref="CareerNavigationPayload"/> 传入
/// puuid（可带预载资料），本页进入"查看他人"模式——隐藏战利品/熟练度页签、
/// 显示「返回我的生涯」、刷新按钮改为刷新他人；解析失败用占位对象兜底，不阻断排位/战绩加载。
/// 请求代际防串号：<see cref="_requestSeq"/> 递增计数，回写前校验（先点 A 再点 B，慢响应不回写）。
/// </summary>
public sealed class CareerPage : Component
{
    /// <summary>空列表共享实例（<c>UseState</c> 初值只读一次，集合表达式会每次新建，REACTOR_HOOKS_013）。</summary>
    private static readonly IReadOnlyList<MatchSummary> EmptyMatches = [];

    /// <summary>空战利品选中集合共享实例。</summary>
    private static readonly IReadOnlySet<string> EmptyLootSelection = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>请求代际计数：每次加载递增，回写前校验（对应旧版 careerRequestSeq）。</summary>
    private int _requestSeq;

    private string[]? _filterLabels;
    private string[]? _filterModeNames;
    private IReadOnlyList<MatchSummary>? _filterSource;

    /// <summary>
    /// 解析队列筛选项：第一项为「全部 (N)」，随后仅保留当前对局列表中实际存在的模式（按场数倒序，带场数）。
    /// </summary>
    private (string[] Labels, string[] ModeNames) ResolveFilterLabels(IReadOnlyList<MatchSummary> matches)
    {
        if (_filterLabels is not null && ReferenceEquals(_filterSource, matches))
        {
            return (_filterLabels, _filterModeNames!);
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (MatchSummary m in matches)
        {
            if (!string.IsNullOrEmpty(m.QueueName))
            {
                counts[m.QueueName] = counts.GetValueOrDefault(m.QueueName) + 1;
            }
        }

        var labels = new List<string> { $"全部 ({matches.Count})" };
        var modes = new List<string> { string.Empty };

        // 实际有战绩的模式按场数倒序
        foreach (var kvp in counts.OrderByDescending(p => p.Value))
        {
            labels.Add($"{kvp.Key} ({kvp.Value})");
            modes.Add(kvp.Key);
        }

        _filterLabels = labels.ToArray();
        _filterModeNames = modes.ToArray();
        _filterSource = matches;
        return (_filterLabels, _filterModeNames);
    }

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var summonerService = services.Services.GetRequiredService<ISummonerService>();
        var careerService = services.Services.GetRequiredService<ICareerService>();
        var matchService = services.Services.GetRequiredService<IMatchService>();
        var lifecycle = services.Services.GetService<GameLifecycleService>();
        var lcu = services.Services.GetRequiredService<LcuConnection>();

        var nav = UseNavigation<AppRoute>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        // 连接态走订阅值而非 lcu.CurrentState：客户端启动/退出必须让本页重渲染，
        // 否则离线占位会停在进页那一刻的判断上（客户端启动后仍是一张空页）。
        var (connState, setConnState) = UseState(lcu.CurrentState, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = lcu.States.Subscribe(setConnState);
                return () => subscription.Dispose();
            },
            0);

        var (summoner, setSummoner) = UseState<SummonerProfile?>(null, threadSafe: true);
        var (ranked, setRanked) = UseState(RankedStats.Empty, threadSafe: true);
        var (matches, setMatches) = UseState(EmptyMatches, threadSafe: true);
        var (loot, setLoot) = UseState<PlayerLootOverview?>(null, threadSafe: true);
        var (mastery, setMastery) = UseState<MasteryOverview?>(null, threadSafe: true);
        var (catalog, setCatalog) = UseState(GameDataCatalog.Empty, threadSafe: true);
        var (tab, setTab) = UseState(CareerTab.Matches, threadSafe: true);
        var (queueFilter, setQueueFilter) = UseState(0, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (copied, setCopied) = UseState(false, threadSafe: true);
        var (refreshToken, setRefreshToken) = UseState(0, threadSafe: true);
        var (teammates, setTeammates) = UseState<RecentTeammatesResult?>(null, threadSafe: true);
        var (loadingTeammates, setLoadingTeammates) = UseState(false, threadSafe: true);
        var (viewTarget, setViewTarget) = UseState<CareerViewTarget?>(null, threadSafe: true);

        // 战利品交互与过滤状态
        var (selectedLootIds, setSelectedLootIds) = UseState(EmptyLootSelection, threadSafe: true);
        var (lootTypeFilter, setLootTypeFilter) = UseState(0, threadSafe: true);
        var (lootOwnedFilter, setLootOwnedFilter) = UseState(0, threadSafe: true);
        var (lootValueType, setLootValueType) = UseState(0, threadSafe: true);
        var (lootOperator, setLootOperator) = UseState(0, threadSafe: true);
        var (lootThreshold, setLootThreshold) = UseState(string.Empty, threadSafe: true);
        var (lootProgress, setLootProgress) = UseState<LootOperationProgress?>(null, threadSafe: true);
        var (lootNotice, setLootNotice) = UseState<string?>(null, threadSafe: true);
        // 单件数量弹窗与批量确认弹窗（裁决：对齐旧版 openLootModal / confirmModal）
        var (openDialogItem, setOpenDialogItem) = UseState<OpenableLootItem?>(null, threadSafe: true);
        var (openQuantity, setOpenQuantity) = UseState(1, threadSafe: true);
        var (confirmDialog, setConfirmDialog) = UseState<LootConfirmDialog?>(null, threadSafe: true);

        // 英雄熟练度筛选与排序状态
        var (masterySearch, setMasterySearch) = UseState(string.Empty, threadSafe: true);
        var (masteryChest, setMasteryChest) = UseState(0, threadSafe: true);
        var (masteryLevel, setMasteryLevel) = UseState(0, threadSafe: true);
        var (masterySort, setMasterySort) = UseState(0, threadSafe: true);

        // 外部跳转（最近队友卡片、战绩详情玩家名等入口）：挂载时消费 payload 进入"查看他人"。
        UseEffect(
            () =>
            {
                var (puuid, preloaded) = CareerNavigationPayload.Instance.Consume();
                if (!string.IsNullOrWhiteSpace(puuid))
                {
                    setQueueFilter(0);
                    setViewTarget(new CareerViewTarget(puuid, preloaded));
                    setTab(CareerTab.Matches);
                }

                return static () => { };
            },
            0);

        // 召唤师变更时清空队友缓存、重置队列筛选与战利品选中
        UseEffect(
            () =>
            {
                setQueueFilter(0);
                setTeammates(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                return static () => { };
            },
            summoner?.Puuid ?? string.Empty);

        // 召唤师状态流变化（如客户端从未登录变为登录）时自动拉取刷新
        UseEffect(
            () =>
            {
                var sub = summonerService.CurrentChanged.Subscribe(profile =>
                {
                    if (viewTarget is null && profile is not null)
                    {
                        setRefreshToken(Environment.TickCount);
                    }
                });
                return () => sub.Dispose();
            },
            viewTarget?.Puuid ?? string.Empty);

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

        // 首屏与刷新共用一条链路：查看目标、refreshToken 或对局加载数量变化即重新取数，
        // token > 0 时强制绕过排位与战绩缓存；代际计数在加载入口递增、回写前校验。
        string loadKey = $"{viewTarget?.Puuid ?? string.Empty}|{refreshToken}|{services.Config.Functions.CareerGamesNumber}";
        UseEffect(
            () =>
            {
                int seq = ++_requestSeq;
                Func<bool> isStale = () => seq != _requestSeq;
                var cancellation = new CancellationTokenSource();
                var gamesNumber = Math.Clamp(services.Config.Functions.CareerGamesNumber, 1, 50);
                _ = viewTarget is { } target
                    ? LoadOtherCareerAsync(
                        summonerService,
                        careerService,
                        matchService,
                        target,
                        gamesNumber,
                        refreshToken > 0,
                        isStale,
                        setSummoner,
                        setRanked,
                        setMatches,
                        setLoading,
                        cancellation.Token)
                    : LoadSelfAsync(
                        summonerService,
                        careerService,
                        matchService,
                        gamesNumber,
                        refreshToken > 0,
                        isStale,
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
            loadKey);

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

        bool isViewingOther = viewTarget is not null;
        var (filterLabels, filterModes) = ResolveFilterLabels(matches);
        int filter = queueFilter >= 0 && queueFilter < filterModes.Length ? queueFilter : 0;
        string targetMode = filterModes[filter];
        MatchSummary[] visibleMatches = string.IsNullOrEmpty(targetMode)
            ? [.. matches]
            : [.. matches.Where(match => string.Equals(match.QueueName, targetMode, StringComparison.Ordinal))];

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

        // 最近队友点击（裁决 D9）：有 puuid 留在生涯页打开他人生涯（预载占位资料）；
        // 无 puuid 维持旧行为跳战绩查询。
        void OnClickTeammate(RecentTeammate tm)
        {
            if (!string.IsNullOrEmpty(tm.Puuid))
            {
                setViewTarget(new CareerViewTarget(tm.Puuid, PlaceholderSummoner(tm.Puuid, tm.Name, tm.ProfileIconId)));
                setTab(CareerTab.Matches);
                return;
            }

            SearchNavigationPayload.Instance.Set(tm.Name, tm.Puuid);
            nav.Replace(AppRoute.Search);
        }

        // 返回我的生涯：清空查看目标走现有加载链路（召唤师走 5 分钟缓存）。
        void BackToMyCareer()
        {
            setQueueFilter(0);
            setViewTarget(null);
            setTab(CareerTab.Matches);
        }

        // Career → Search 跳转载荷（§4）：历史战绩按钮 = 只搜索（gameId: -1）
        void GoToHistory()
        {
            if (summoner is null)
            {
                return;
            }

            SearchNavigationPayload.Instance.Set(summoner.RiotId, summoner.Puuid, gameId: -1);
            nav.Replace(AppRoute.Search);
        }

        // Career → Search 跳转载荷（§4）：对局卡片点击 = 搜索本人并选中该局（gameId > 0）
        void OpenMatchInSearch(MatchSummary match)
        {
            if (summoner is null)
            {
                return;
            }

            SearchNavigationPayload.Instance.Set(summoner.RiotId, summoner.Puuid, gameId: match.GameId);
            nav.Replace(AppRoute.Search);
        }

        void HandleOpenLootItem(OpenableLootItem item)
        {
            // 点击卡片弹数量选择弹窗（旧版 openLootModal），初始数量 = 最大可开启数
            setOpenDialogItem(item);
            setOpenQuantity(Math.Max(LootManagerView.MaxOpenQuantity(item), 0));
        }

        void ConfirmOpenLootItem()
        {
            if (openDialogItem is not { } item)
            {
                return;
            }

            int quantity = Math.Clamp(openQuantity, 1, Math.Max(LootManagerView.MaxOpenQuantity(item), 1));
            bool isFragment = item.IsKeyFragment;
            setOpenDialogItem(null);
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                string recipeName = isFragment ? "MATERIAL_key_fragment_FORGE" : item.RecipeName;
                var ingredients = !isFragment && item.NeedKey
                    ? new[] { item.LootId, item.KeyLootId ?? "MATERIAL_key" }
                    : new[] { item.LootId };
                var res = await careerService.OpenLootAsync(
                    recipeName,
                    ingredients,
                    quantity,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setLootNotice(res.Success
                    ? (isFragment
                        ? $"合成成功：{string.Join(", ", res.Rewards)}"
                        : $"开启成功：{string.Join(", ", res.Rewards)}")
                    : (isFragment ? $"合成失败：{res.ErrorMessage}" : $"开启失败：{res.ErrorMessage}"));
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

        void HandleToggleSelect(string lootId)
        {
            var next = new HashSet<string>(selectedLootIds, StringComparer.Ordinal);
            if (!next.Add(lootId))
            {
                next.Remove(lootId);
            }
            setSelectedLootIds(next);
        }

        // 切换类型筛选时清空已选（裁决：防误操作分解，对齐旧版 watch(filterType)）
        void HandleTypeFilterChanged(int index)
        {
            if (index != lootTypeFilter)
            {
                setLootTypeFilter(index);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
            }
        }

        void HandleSelectAllFiltered()
        {
            if (loot is not { } lootData)
            {
                return;
            }

            var filtered = LootManagerView.FilterInventory(
                lootData.InventoryItems,
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
            if (loot is not { } lootData)
            {
                return;
            }

            var itemsToDisenchant = lootData.InventoryItems
                .Where(i => selectedLootIds.Contains(i.LootId))
                .Select(i => (i.LootId, i.Count))
                .ToList();

            if (itemsToDisenchant.Count == 0) return;

            // 批量确认弹窗（裁决）：明细行 = 所选种类数 + 预计蓝/橙精粹回报
            int blue = lootData.InventoryItems
                .Where(i => selectedLootIds.Contains(i.LootId) && i.IsChampion)
                .Sum(i => i.DisenchantValue * i.Count);
            int orange = lootData.InventoryItems
                .Where(i => selectedLootIds.Contains(i.LootId) && !i.IsChampion)
                .Sum(i => i.DisenchantValue * i.Count);

            setConfirmDialog(new LootConfirmDialog(
                "分解选中项",
                $"您确定要分解当前选中的 {itemsToDisenchant.Count} 种碎片吗？此操作无法撤销。",
                new (string, string)[]
                {
                    ("所选碎片种类", $"{itemsToDisenchant.Count} 种"),
                    ("🔷 预计蓝色精粹回报", $"+ {blue}"),
                    ("🔶 预计橙色精粹回报", $"+ {orange}"),
                },
                () =>
                {
                    setConfirmDialog(null);
                    ExecuteDisenchant(itemsToDisenchant);
                }));
        }

        void ExecuteDisenchant(IReadOnlyList<(string LootId, int Count)> items)
        {
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.DisenchantLootsAsync(
                    items,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                setLootNotice(res.Success ? $"批量分解完成：成功 {res.SucceededCount} 种" : $"分解失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        // 重铸（裁决 A3）：按 lootId 分组、每组凑 3 的倍数、余数保留；
        // 一组都凑不齐时给「无法重随」提示，不再静默 return（裁决 D6 条目）。
        void HandleReroll()
        {
            if (loot is not { } lootData)
            {
                return;
            }

            var (items, groups, consumed, remaining) = LootManagerView.PlanReroll(lootData.InventoryItems, selectedLootIds);
            if (groups == 0)
            {
                setLootNotice("所选战利品凑不齐一组重随（每类至少 3 个）");
                return;
            }

            setConfirmDialog(new LootConfirmDialog(
                "重铸选中项",
                "您确定要将选中的碎片进行三合一重随吗？系统将按同类 3 个一组进行重随，余数保留。",
                new (string, string)[]
                {
                    ("重随合成组数", $"{groups} 组"),
                    ("消耗碎片总数", $"{consumed} 个"),
                    ("保留未配对数", $"{remaining} 个"),
                },
                () =>
                {
                    setConfirmDialog(null);
                    ExecuteReroll(items);
                }));
        }

        void ExecuteReroll(IReadOnlyList<(string LootId, int Count)> items)
        {
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.RerollLootsAsync(
                    items,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                setLootNotice(res.Success ? $"重铸完成：获得 {string.Join(", ", res.Rewards)}" : $"重铸失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        // 升级前置校验（对齐旧版 useLoot.ts:592-697）：
        // 无未拥有项 → 提示中止；SKIN 未拥有且对应英雄也未拥有 → 提示名单中止；
        // 总费用（英雄→蓝、其他→橙，单价×数量）超过余额 → 提示「需要 N / 拥有 M」中止。
        void HandleUpgrade()
        {
            if (loot is not { } lootData)
            {
                return;
            }

            var unowned = lootData.InventoryItems
                .Where(i => selectedLootIds.Contains(i.LootId) && !i.IsOwned)
                .ToList();

            if (unowned.Count == 0)
            {
                setLootNotice("没有选中任何未拥有的战利品进行操作！");
                return;
            }

            var missingChampionSkins = unowned
                .Where(i => string.Equals(i.DisplayCategories, "SKIN", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(i.ParentItemStatus, "OWNED", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (missingChampionSkins.Count > 0)
            {
                string names = string.Join("、", missingChampionSkins.Select(i => i.ItemDesc));
                setLootNotice($"未拥有以下皮肤对应的英雄：[{names}]，请先在游戏内解锁或购买该英雄！");
                return;
            }

            int totalBlue = unowned
                .Where(i => i.UpgradeEssenceCost > 0 && i.IsChampion)
                .Sum(i => i.UpgradeEssenceCost * i.Count);
            int totalOrange = unowned
                .Where(i => i.UpgradeEssenceCost > 0 && !i.IsChampion)
                .Sum(i => i.UpgradeEssenceCost * i.Count);

            if (totalBlue > lootData.BlueEssence)
            {
                setLootNotice($"蓝色精粹不足！升级需要 🔷 {totalBlue}，当前拥有 🔷 {lootData.BlueEssence}");
                return;
            }

            if (totalOrange > lootData.OrangeEssence)
            {
                setLootNotice($"橙色精粹不足！升级需要 🔶 {totalOrange}，当前拥有 🔶 {lootData.OrangeEssence}");
                return;
            }

            bool hasUpgrade = unowned.Any(i => i.UpgradeEssenceCost > 0);
            bool hasUnlock = unowned.Any(i => i.UpgradeEssenceCost == 0);
            string description = hasUpgrade && hasUnlock
                ? "消耗精粹升级部分碎片，其余永久道具将免费解锁"
                : hasUpgrade
                    ? "将消耗对应精粹并解锁为永久版"
                    : "将免费解锁/激活永久道具并添加到收藏";
            string title = hasUpgrade && !hasUnlock ? "升级选中项" : hasUnlock && !hasUpgrade ? "解锁选中项" : "升级 / 解锁选中项";

            var details = new List<(string, string)> { ("待处理战利品种类", $"{unowned.Count} 种") };
            if (totalBlue > 0)
            {
                details.Add(("预计消耗蓝色精粹", $"🔷 {totalBlue}"));
            }
            if (totalOrange > 0)
            {
                details.Add(("预计消耗橙色精粹", $"🔶 {totalOrange}"));
            }
            details.Add(("操作说明", description));

            var itemsToUpgrade = unowned.Select(i => (i.LootId, i.Count)).ToList();
            setConfirmDialog(new LootConfirmDialog(
                title,
                $"您确定要将选中的 {unowned.Count} 个未拥有项目进行升级或解锁吗？",
                details,
                () =>
                {
                    setConfirmDialog(null);
                    ExecuteUpgrade(itemsToUpgrade);
                }));
        }

        void ExecuteUpgrade(IReadOnlyList<(string LootId, int Count)> items)
        {
            _ = Task.Run(async () =>
            {
                setLootNotice(null);
                var res = await careerService.UpgradeLootsAsync(
                    items,
                    new Progress<LootOperationProgress>(p => setLootProgress(p)));
                setLootProgress(null);
                setSelectedLootIds(new HashSet<string>(StringComparer.Ordinal));
                setLootNotice(res.Success ? $"升级/解锁完成：成功 {res.SucceededCount} 种" : $"升级失败：{res.ErrorMessage}");
                setRefreshToken(refreshToken + 1);
            });
        }

        // 查看他人时强制停留在「生涯战绩」（旧版 watch(isViewingOther) 的等效实现）
        CareerTab effectiveTab = isViewingOther ? CareerTab.Matches : tab;

        // 「生涯战绩」页签：排位表 + 近期对局看板（含下拉） + 战绩卡片列表
        Element MatchesTab() => VStack(12,
            RankedTableView.Render(ranked, palette),
            RecentSummaryView.Render(
                summary,
                catalog,
                palette,
                filterLabels,
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
                    .Select(match => Component<CareerMatchRow, CareerMatchRowProps>(
                        new CareerMatchRowProps(match, catalog, palette, () => OpenMatchInSearch(match)))
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
                onHistory: GoToHistory,
                isViewingOther: isViewingOther,
                onBackToSelf: BackToMyCareer);

        // 页签栏：仅在查看自己时显示（查看他人时直接展示战绩内容，不显示「生涯战绩」等页签）
        Element tabBar = summoner is null || isViewingOther
            ? Empty()
            : VStack(8,
                HStack(8,
                    TabButton("生涯战绩", CareerTab.Matches, effectiveTab, setTab, palette),
                    TabButton("战利品", CareerTab.Loot, effectiveTab, setTab, palette),
                    TabButton("英雄熟练度", CareerTab.Mastery, effectiveTab, setTab, palette)),
                Border(Empty()).Height(1).Background(Theme.CardStroke).HAlign(HorizontalAlignment.Stretch));

        // D1：战利品/熟练度读取失败（null）给提示条；成功但为空由子视图给普通空态。
        Element lootFailureHint = loot is null && !loading
            ? FailureHint("获取战利品失败，请确认客户端已连接", () => setRefreshToken(refreshToken + 1))
            : Empty();
        Element masteryFailureHint = mastery is null && !loading
            ? FailureHint("获取英雄熟练度失败，请确认客户端已连接", () => setRefreshToken(refreshToken + 1))
            : Empty();

        Element content = effectiveTab switch
        {
            CareerTab.Loot => VStack(10,
                lootFailureHint,
                LootManagerView.Render(
                    loot ?? PlayerLootOverview.Empty,
                    palette,
                    onRefresh: () => setRefreshToken(refreshToken + 1),
                    onOpenAll: HandleSmartOpenAll,
                    onOpenItem: HandleOpenLootItem,
                    typeFilterIndex: lootTypeFilter,
                    onTypeFilterChanged: HandleTypeFilterChanged,
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
                    operationNotice: lootNotice)),
            CareerTab.Mastery => mastery is null
                ? masteryFailureHint
                : ChampionMasteryView.Render(
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

        // 弹窗层：单件数量弹窗 / 批量确认弹窗，覆盖在正文之上（就近遮罩，不用新窗口）
        Element? overlay = openDialogItem is { } dialogItem
            ? LootManagerView.RenderOpenQuantityDialog(
                dialogItem,
                openQuantity,
                palette,
                onQuantityChanged: setOpenQuantity,
                onConfirm: ConfirmOpenLootItem,
                onCancel: () => setOpenDialogItem(null))
            : confirmDialog is { } dialog
                ? LootManagerView.RenderConfirmDialog(
                    dialog,
                    palette,
                    onCancel: () => setConfirmDialog(null))
                : null;

        // 客户端未启动：整页替换为离线占位，与战绩查询、对局信息同一套表达
        // （旧版 Career.vue 的 <LcuOfflineState v-if="!store.isConnected" />）。
        // 位置必须在本方法末尾：所有 UseState/UseEffect 都得按固定顺序跑完，
        // 提前 return 会让 hook 数量随连接态变化，Reactor 直接判状态错位。
        if (connState.Status != LcuConnectionStatus.Connected)
        {
            return LcuOfflineState.Render();
        }

        Element pageBody = overlay is null
            ? body
            : Grid(
                [GridSize.Star()],
                [GridSize.Star()],
                [body, overlay]);

        return ScrollViewer(
            Border(pageBody)
                .Padding(24)
                .HAlign(HorizontalAlignment.Stretch));
    }

    /// <summary>
    /// 查看他人时资料解析失败的占位对象（旧版兜底：名「召唤师」、icon 29、等级 0），不阻断排位/战绩加载。
    /// 队友卡片传来的名字可能形如 <c>名字#TAG</c>，这里做与旧版一致的拆分。
    /// </summary>
    internal static SummonerProfile PlaceholderSummoner(string puuid, string? name = null, int profileIconId = 29)
    {
        string gameName = name ?? "召唤师";
        string tagLine = string.Empty;
        int hashIndex = gameName.IndexOf('#');
        if (hashIndex >= 0)
        {
            tagLine = gameName[(hashIndex + 1)..];
            gameName = gameName[..hashIndex];
        }

        return new SummonerProfile
        {
            AccountId = 0,
            DisplayName = gameName,
            GameName = gameName,
            TagLine = tagLine,
            Puuid = puuid,
            SummonerId = 0,
            SummonerLevel = 0,
            ProfileIconId = profileIconId,
            PercentCompleteForNextLevel = 0,
            XpSinceLastLevel = 0,
            XpUntilNextLevel = 0,
        };
    }

    /// <summary>D1：读取失败的提示条（对齐旧版失败 toast 的文案口径）。</summary>
    private static Element FailureHint(string message, Action onRetry) =>
        CareerVisuals.Card(
            HStack(12,
                Body(message).Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center),
                Button(Caption("重试"), onRetry).Height(30).Padding(14, 0).AutomationName($"重试：{message}")),
            padding: 16);

    /// <summary>未登录时的资料卡占位：说明原因而不是留白。</summary>
    private static Element EmptyHeader(bool loading) =>
        CareerVisuals.Card(
            VStack(6,
                BodyStrong(loading ? "正在读取生涯数据…" : "未读取到当前召唤师（客户端未登录）"),
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
        var text = Body(label)
            .Foreground(isSelected ? Theme.AccentText : Theme.SecondaryText)
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center);

        Element button = Button(text, () =>
        {
            if (target == current)
            {
                return;
            }

            setTab(target);
        })
        .Height(34)
        .Padding(16, 0)
        .CornerRadius(YuumiTheme.OverlayRadius)
        .AutomationName(label);

        return isSelected
            ? button
                .Background(Theme.SubtleFill)
                .WithBorder(Theme.CardStroke, 1)
            : button
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .BorderThickness(0);
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
    /// 自己的生涯：当前召唤师 → 排位总览 + 最近对局（数量由「默认对局数量」设置决定）+ 战利品 + 英雄熟练度。
    /// 四路并行，失败只显示空态或提示，不弹框打扰。
    /// </summary>
    private static async Task LoadSelfAsync(
        ISummonerService summonerService,
        ICareerService careerService,
        IMatchService matchService,
        int gamesNumber,
        bool forceRefresh,
        Func<bool> isStale,
        Action<SummonerProfile?> applySummoner,
        Action<RankedStats> applyRanked,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<PlayerLootOverview?> applyLoot,
        Action<MasteryOverview?> applyMastery,
        Action<bool> applyLoading,
        CancellationToken cancellationToken)
    {
        try
        {
            applyLoading(true);

            SummonerProfile? profile = await summonerService
                .GetCurrentSummonerAsync(cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            applySummoner(profile);
            if (profile is null)
            {
                // 未登录：全部给空态/失败提示，绝不回退样例数据（D1）
                applyRanked(RankedStats.Empty);
                applyMatches(EmptyMatches);
                applyLoot(null);
                applyMastery(null);
                return;
            }

            if (forceRefresh)
            {
                matchService.InvalidateCache();
            }

            Task<RankedStats> rankedTask = careerService.GetRankedStatsAsync(
                profile.Puuid, forceRefresh, cancellationToken);

            Task<MatchHistoryPage> historyTask = matchService.GetMatchHistoryAsync(
                profile.Puuid, 0, gamesNumber - 1, cancellationToken);

            Task<PlayerLootOverview?> lootTask = careerService.GetLootOverviewAsync(
                forceRefresh, cancellationToken);

            Task<MasteryOverview?> masteryTask = careerService.GetMasteryOverviewAsync(
                forceRefresh, cancellationToken);

            await Task.WhenAll(rankedTask, historyTask, lootTask, masteryTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || isStale())
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
            applyLoot(null);
            applyMastery(null);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                applyLoading(false);
            }
        }
    }

    /// <summary>
    /// 他人的生涯（§2.8）：优先用预载资料 → 按 puuid 拉权威资料 → 都失败用占位对象；
    /// 不阻断排位与战绩加载。战利品/熟练度属于当前登录账号，此模式不加载（页签已隐藏）。
    /// </summary>
    private static async Task LoadOtherCareerAsync(
        ISummonerService summonerService,
        ICareerService careerService,
        IMatchService matchService,
        CareerViewTarget target,
        int gamesNumber,
        bool forceRefresh,
        Func<bool> isStale,
        Action<SummonerProfile?> applySummoner,
        Action<RankedStats> applyRanked,
        Action<IReadOnlyList<MatchSummary>> applyMatches,
        Action<bool> applyLoading,
        CancellationToken cancellationToken)
    {
        try
        {
            applyLoading(true);

            // 1. 优先用预载/占位对象先让界面展示基础信息（对齐旧版 loadCareerSummoner）
            SummonerProfile profile = target.Preloaded ?? PlaceholderSummoner(target.Puuid);
            applySummoner(profile);

            // 2. 预载资料只是占位（如队友卡片只有名字）或等级为 0 时，拉一次权威资料；
            //    失败（404/未连接）保留占位对象，不阻断排位与战绩加载。
            if (target.Preloaded is null || target.Preloaded.SummonerLevel == 0)
            {
                SummonerProfile? remote = await summonerService
                    .FindByPuuidAsync(target.Puuid, cancellationToken)
                    .ConfigureAwait(false);
                if (remote is not null)
                {
                    profile = remote;
                    applySummoner(profile);
                }
            }

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            if (forceRefresh)
            {
                matchService.InvalidateCache();
            }

            Task<RankedStats> rankedTask = careerService.GetRankedStatsAsync(
                target.Puuid, forceRefresh, cancellationToken);

            Task<MatchHistoryPage> historyTask = matchService.GetMatchHistoryAsync(
                target.Puuid, 0, gamesNumber - 1, cancellationToken);

            await Task.WhenAll(rankedTask, historyTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || isStale())
            {
                return;
            }

            applyRanked(await rankedTask.ConfigureAwait(false));
            applyMatches((await historyTask.ConfigureAwait(false)).Games);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            applyRanked(RankedStats.Empty);
            applyMatches(EmptyMatches);
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
