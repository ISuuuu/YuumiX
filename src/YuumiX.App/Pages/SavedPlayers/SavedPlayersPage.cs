using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Theming;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>展开查看相遇对局的目标：Nonce 允许重复点击同一人重新拉取第一页。</summary>
internal sealed record ExpandedTarget(string Puuid, string? SelfPuuid, int Nonce);

/// <summary>
/// 路人集页面：对齐 docs/pic/路人集.png 与原 SavedPlayers.vue。
/// 本页只做状态编排（R14）：页头/工具栏/列表在 <see cref="SavedPlayersVisuals"/>，
/// 玩家卡片在 <see cref="SavedPlayerCard"/>，账号解析与导入导出在 <see cref="SavedPlayersLoaders"/>。
///
/// R7：玩家列表与相遇对局都是 offset 分页，走 <c>UseInfiniteResource</c>
///（cursor 为页号，null 表示首页/末页）；标记保存/删除/导入/导出走 <c>UseMutation</c>；
/// 账号解析走 <c>UseResource</c>（连接状态变化重拉）。搜索防抖用 effect 定时器取代类字段 Timer（R6）。
///
/// <b>规模</b>：本文件 309 行（R14 过渡期，超 300 理由：双 infinite resource + 四个 mutation 的
/// hooks 编排必须整块留在 Render 内保证调用顺序；视觉已全部外移）。
/// </summary>
public sealed class SavedPlayersPage : Component
{
    private const int PageSize = 50;
    private const int GamesPageSize = 20;

    private static readonly IReadOnlyList<SavedPlayerRecord> EmptyPlayers = [];
    private static readonly IReadOnlyList<EncounteredGameRecord> EmptyGames = [];

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var store = services.Services.GetRequiredService<SavedPlayersStore>();
        var summonerService = services.Services.GetService<ISummonerService>();
        var lcu = services.Services.GetService<LcuConnection>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        // 全局 Toast 服务（R5）：队列由 AppShell 提供并在其 ToastHost 呈现
        var toast = UseContext(AppHost.ToastContext)!;

        var (filter, setFilter) = UseState("all", threadSafe: true); // "all", "tagged", "multiple"

        // 搜索框状态：草稿（及时响应键入）与生效词（防抖触发）
        var (searchDraft, setSearchDraft) = UseState(string.Empty, threadSafe: true);
        var (committedSearch, setCommittedSearch) = UseState(string.Empty, threadSafe: true);

        // 内联编辑与安全删除状态
        var (editingPuuid, setEditingPuuid) = UseState<string?>(null, threadSafe: true);
        var (editingTag, setEditingTag) = UseState(string.Empty, threadSafe: true);
        var (pendingDeletePuuid, setPendingDeletePuuid) = UseState<string?>(null, threadSafe: true);

        // 展开与收起相遇对局
        var (expanded, setExpanded) = UseState<ExpandedTarget?>(null, threadSafe: true);
        var expandNonceRef = UseRef(0);

        // 连接态订阅 → 账号解析（UseResource）：连接状态变化即重拉当前登录账号
        var (connStatus, setConnStatus) = UseState(
            lcu?.CurrentState.Status ?? LcuConnectionStatus.Disconnected,
            threadSafe: true);
        UseEffect(
            () =>
            {
                if (lcu is null) return static () => { };
                var subscription = lcu.States.Subscribe(state => setConnStatus(state.Status));
                return () => subscription.Dispose();
            },
            0);

        var selfPuuidResource = UseResource(
            ct => SavedPlayersLoaders.ResolveSelfPuuidAsync(lcu, summonerService, store, ct),
            deps: new object[] { connStatus },
            options: new ResourceOptions(CacheKey: "savedplayers/self-puuid"));
        string? selfPuuid = selfPuuidResource switch
        {
            AsyncValue<string?>.Data d => d.Value,
            _ => null,
        };
        bool accountLoading = selfPuuidResource is AsyncValue<string?>.Loading;

        // 搜索防抖（R6：effect 定时器取代类字段 Timer）：输入停顿 300ms 后把草稿提交为生效词
        UseEffect(
            () =>
            {
                if (searchDraft.Trim() == committedSearch)
                {
                    return static () => { };
                }

                var cancellation = new CancellationTokenSource();
                _ = SavedPlayersLoaders.CommitSearchDebouncedAsync(searchDraft, setCommittedSearch, cancellation.Token);
                return () =>
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                };
            },
            searchDraft);

        // 玩家列表（offset 分页 → UseInfiniteResource）：账号/筛选/生效词变化重置第一页；
        // 账号未解析或库无记录时返回空页，与旧行为一致（不查询直接置空）。
        var playersResource = UseInfiniteResource<SavedPlayerRecord, int?>(
            async (cursor, ct) =>
            {
                if (string.IsNullOrWhiteSpace(selfPuuid))
                {
                    return new Page<SavedPlayerRecord, int?>(EmptyPlayers, null, 0);
                }

                int page = cursor ?? 1;
                var result = await store.QueryPlayersAsync(selfPuuid, page, PageSize, filter, committedSearch, ct);
                bool hasMore = (long)page * PageSize < result.TotalCount;
                return new Page<SavedPlayerRecord, int?>(
                    result.Items,
                    hasMore ? page + 1 : null,
                    (int)result.TotalCount);
            },
            deps: new object[] { selfPuuid ?? string.Empty, filter, committedSearch });

        List<SavedPlayerRecord> players = [.. playersResource.Items.OfType<SavedPlayerRecord>()];
        long totalCount = playersResource.TotalCount ?? 0;
        bool loading = accountLoading
            || (selfPuuid is not null && players.Count == 0 && playersResource.LoadState is LoadState.Loading);
        bool loadingMore = players.Count > 0 && playersResource.LoadState is LoadState.Loading;

        // 列表加载失败：一次性 toast（LoadState 值相等，同一失败不重复弹）
        UseEffect(
            () =>
            {
                if (playersResource.LoadState is LoadState.Error e)
                {
                    toast.Error($"加载路人集失败: {e.Exception.Message}");
                }

                return static () => { };
            },
            playersResource.LoadState);

        // 相遇对局（第二个 infinite）：展开某人时拉第一页，「加载更多」翻页；收起返回空页
        var gamesResource = UseInfiniteResource<EncounteredGameRecord, int?>(
            async (cursor, ct) =>
            {
                if (expanded is not { } target)
                {
                    return new Page<EncounteredGameRecord, int?>(EmptyGames, null, 0);
                }

                int page = cursor ?? 1;
                var result = await store.QueryEncounteredGamesAsync(target.Puuid, target.SelfPuuid, page, GamesPageSize, ct);
                bool hasMore = (long)page * GamesPageSize < result.TotalCount;
                return new Page<EncounteredGameRecord, int?>(
                    result.Items,
                    hasMore ? page + 1 : null,
                    (int)result.TotalCount);
            },
            deps: new object[] { expanded?.Puuid ?? string.Empty, expanded?.SelfPuuid ?? string.Empty, expanded?.Nonce ?? 0 });

        List<EncounteredGameRecord> encounteredGames = [.. gamesResource.Items.OfType<EncounteredGameRecord>()];
        long encounteredTotal = gamesResource.TotalCount ?? 0;
        bool gamesLoading = gamesResource.LoadState is LoadState.Loading;

        // 标记保存 / 删除 / 导入 / 导出（UseMutation）：成功后 toast + Refresh 回第一页
        var saveTagMutation = UseMutation<(SavedPlayerRecord Player, string Tag), object?>(
            mutator: async (input, ct) =>
            {
                string tag = input.Tag.Trim();
                await store.SaveTagAsync(input.Player.Puuid, input.Player.SelfPuuid, string.IsNullOrEmpty(tag) ? null : tag, ct);
                return null;
            },
            options: new MutationOptions<(SavedPlayerRecord Player, string Tag), object?>(
                OnSuccess: (_, _) =>
                {
                    setEditingPuuid(null);
                    toast.Success("标记已更新");
                    playersResource.Refresh();
                }));

        var deleteMutation = UseMutation<SavedPlayerRecord, object?>(
            mutator: async (player, ct) =>
            {
                await store.DeletePlayerAsync(player.Puuid, player.SelfPuuid, ct);
                return null;
            },
            options: new MutationOptions<SavedPlayerRecord, object?>(
                OnSuccess: (_, player) =>
                {
                    setPendingDeletePuuid(null);
                    if (expanded?.Puuid == player.Puuid)
                    {
                        setExpanded(null);
                    }

                    toast.Success($"已删除玩家 {player.SummonerName}");
                    playersResource.Refresh();
                }));

        var exportMutation = UseMutation<string?, string>(
            mutator: (puuid, ct) => SavedPlayersLoaders.ExportAsync(store, puuid, ct),
            options: new MutationOptions<string?, string>(
                OnSuccess: (path, _) =>
                {
                    if (!string.IsNullOrEmpty(path))
                    {
                        toast.Success($"已成功导出到: {path}");
                    }
                },
                OnError: (ex, _) => toast.Error($"导出失败: {ex.Message}")));

        var importMutation = UseMutation<string?, int?>(
            mutator: (puuid, ct) => SavedPlayersLoaders.ImportAsync(store, puuid, ct),
            options: new MutationOptions<string?, int?>(
                OnSuccess: (count, _) =>
                {
                    if (count is > 0)
                    {
                        toast.Success($"成功导入 {count} 名标记玩家");
                        playersResource.Refresh();
                    }
                },
                OnError: (ex, _) => toast.Error($"导入失败: {ex.Message}")));

        void OnSearchInputChanged(string text) => setSearchDraft(text);

        void OnSearchSubmitted() => setCommittedSearch(searchDraft.Trim());

        void OnClearSearch()
        {
            setSearchDraft(string.Empty);
            setCommittedSearch(string.Empty);
        }

        void ToggleExpand(SavedPlayerRecord player)
        {
            expandNonceRef.Current += 1;
            setExpanded(expanded?.Puuid == player.Puuid
                ? null
                : new ExpandedTarget(player.Puuid, player.SelfPuuid, expandNonceRef.Current));
        }

        void HandleSaveTag(SavedPlayerRecord player) =>
            _ = saveTagMutation.RunAsync((player, editingTag));

        void HandleConfirmDelete(SavedPlayerRecord player) =>
            _ = deleteMutation.RunAsync(player);

        void HandleExport() => _ = exportMutation.RunAsync(selfPuuid);

        void HandleImport() => _ = importMutation.RunAsync(selfPuuid);

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            [
                // 1. 页头 Header（带导入导出）
                SavedPlayersVisuals.RenderHeader(totalCount, HandleImport, HandleExport, palette).Grid(row: 0),

                // 2. 工具栏 Toolbar（防抖搜索与清空、分类筛选）
                SavedPlayersVisuals.RenderToolbar(
                    searchDraft,
                    OnSearchInputChanged,
                    OnSearchSubmitted,
                    OnClearSearch,
                    filter,
                    setFilter,
                    palette).Grid(row: 1),

                // 3. 列表区域（带无限滚动与相遇历史）
                SavedPlayersVisuals.RenderList(
                    players,
                    totalCount,
                    loading,
                    loadingMore,
                    playersResource.FetchNext,
                    committedSearch,
                    OnClearSearch,
                    expanded?.Puuid,
                    encounteredGames,
                    encounteredTotal,
                    gamesLoading,
                    _ => gamesResource.FetchNext(),
                    editingPuuid,
                    editingTag,
                    setEditingTag,
                    setEditingPuuid,
                    pendingDeletePuuid,
                    setPendingDeletePuuid,
                    ToggleExpand,
                    HandleSaveTag,
                    HandleConfirmDelete,
                    palette).Grid(row: 2),
            ]
        )
        .Padding(20, 16, 20, 16);
    }
}
