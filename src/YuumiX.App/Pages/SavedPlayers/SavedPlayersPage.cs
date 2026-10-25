using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>
/// 路人集页面：对齐 docs/pic/路人集.png 与原 SavedPlayers.vue。
/// </summary>
public sealed class SavedPlayersPage : Component
{
    private const int PageSize = 50;
    private const int GamesPageSize = 20;

    private static readonly IReadOnlyList<SavedPlayerRecord> EmptyPlayers = [];
    private static readonly IReadOnlyList<EncounteredGameRecord> EmptyGames = [];
    private static readonly IReadOnlyList<ToastItem> EmptyToasts = [];

    // Toast 与防抖定时器
    private static long _nextToastId;
    private readonly List<ToastItem> _activeToasts = [];
    private readonly List<Timer> _toastTimers = [];
    private Timer? _searchDebounceTimer;
    private DispatcherQueue? _dispatcher;

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var store = services.Services.GetRequiredService<SavedPlayersStore>();
        var summonerService = services.Services.GetService<ISummonerService>();
        var lcu = services.Services.GetService<LcuConnection>();

        _dispatcher ??= DispatcherQueue.GetForCurrentThread();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () =>
                {
                    subscription.Dispose();
                    lock (_toastTimers)
                    {
                        foreach (var t in _toastTimers) t.Dispose();
                        _toastTimers.Clear();
                    }
                    _searchDebounceTimer?.Dispose();
                };
            },
            0);

        var (toasts, setToasts) = UseState(EmptyToasts, threadSafe: true);
        var (players, setPlayers) = UseState(EmptyPlayers, threadSafe: true);
        var (totalCount, setTotalCount) = UseState(0L, threadSafe: true);
        var (currentPage, setCurrentPage) = UseState(1, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (loadingMore, setLoadingMore) = UseState(false, threadSafe: true);
        var (filter, setFilter) = UseState("all", threadSafe: true); // "all", "tagged", "multiple"

        // 搜索框状态：草稿（及时响应键入）与生效词（防抖触发）
        var (searchDraft, setSearchDraft) = UseState(string.Empty, threadSafe: true);
        var (committedSearch, setCommittedSearch) = UseState(string.Empty, threadSafe: true);
        var (selfPuuid, setSelfPuuid) = UseState<string?>(null, threadSafe: true);
        var (isResolved, setIsResolved) = UseState(false, threadSafe: true);

        // 展开与编辑状态
        var (expandedPuuid, setExpandedPuuid) = UseState<string?>(null, threadSafe: true);
        var (encounteredGames, setEncounteredGames) = UseState(EmptyGames, threadSafe: true);
        var (encounteredTotal, setEncounteredTotal) = UseState(0L, threadSafe: true);
        var (encounteredPage, setEncounteredPage) = UseState(1, threadSafe: true);
        var (gamesLoading, setGamesLoading) = UseState(false, threadSafe: true);

        // 内联编辑与安全删除状态
        var (editingPuuid, setEditingPuuid) = UseState<string?>(null, threadSafe: true);
        var (editingTag, setEditingTag) = UseState(string.Empty, threadSafe: true);
        var (pendingDeletePuuid, setPendingDeletePuuid) = UseState<string?>(null, threadSafe: true);

        // Toast 提示
        void ShowToast(string message, bool isError = false)
        {
            var id = Interlocked.Increment(ref _nextToastId);
            var item = new ToastItem(id, message, isError);

            lock (_activeToasts)
            {
                _activeToasts.Add(item);
                if (_activeToasts.Count > 4) _activeToasts.RemoveAt(0);
                setToasts(_activeToasts.ToList());
            }

            Timer? timer = null;
            timer = new Timer(_ =>
            {
                _dispatcher?.TryEnqueue(() =>
                {
                    lock (_activeToasts)
                    {
                        _activeToasts.RemoveAll(t => t.Id == id);
                        setToasts(_activeToasts.ToList());
                    }
                    lock (_toastTimers)
                    {
                        if (timer is not null)
                        {
                            _toastTimers.Remove(timer);
                            timer.Dispose();
                        }
                    }
                });
            }, null, 2800, Timeout.Infinite);

            lock (_toastTimers)
            {
                _toastTimers.Add(timer);
            }
        }

        // 解析当前应展示的账号：优先当前登录账号，离线或未登录则回退为最后一位登录者
        void ResolveAccount()
        {
            _ = Task.Run(async () =>
            {
                string? resolved = null;
                if (lcu?.CurrentState.Status == LcuConnectionStatus.Connected && summonerService is not null)
                {
                    try
                    {
                        var me = await summonerService.GetCurrentSummonerAsync();
                        if (me is not null && !string.IsNullOrWhiteSpace(me.Puuid))
                        {
                            resolved = me.Puuid;
                        }
                    }
                    catch
                    {
                        // 忽略异常，降级到数据库回退
                    }
                }

                if (string.IsNullOrWhiteSpace(resolved))
                {
                    resolved = await store.GetLatestSelfPuuidAsync();
                }

                _dispatcher?.TryEnqueue(() =>
                {
                    setSelfPuuid(resolved);
                    setIsResolved(true);
                });
            });
        }

        UseEffect(
            () =>
            {
                ResolveAccount();
                var sub = lcu?.States.Subscribe(_ => ResolveAccount());
                return () => sub?.Dispose();
            },
            0);

        // 分页与数据加载
        void LoadData(int page, bool append)
        {
            if (!isResolved) return;

            if (string.IsNullOrWhiteSpace(selfPuuid))
            {
                // 库中没有任何历史记录且未连客户端，直接置空
                setPlayers(EmptyPlayers);
                setTotalCount(0);
                setCurrentPage(1);
                setLoading(false);
                setLoadingMore(false);
                return;
            }

            if (append)
            {
                setLoadingMore(true);
            }
            else
            {
                setLoading(true);
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await store.QueryPlayersAsync(
                        selfPuuid: selfPuuid,
                        page: page,
                        pageSize: PageSize,
                        filter: filter,
                        search: committedSearch);

                    _dispatcher?.TryEnqueue(() =>
                    {
                        if (append)
                        {
                            // 合并并去重
                            var existingKeys = new HashSet<string>(players.Select(p => $"{p.Puuid}_{p.SelfPuuid}"));
                            var newItems = result.Items.Where(p => !existingKeys.Contains($"{p.Puuid}_{p.SelfPuuid}")).ToList();
                            setPlayers(players.Concat(newItems).ToList());
                        }
                        else
                        {
                            setPlayers(result.Items);
                        }
                        setTotalCount(result.TotalCount);
                        setCurrentPage(page);
                    });
                }
                catch (Exception ex)
                {
                    _dispatcher?.TryEnqueue(() =>
                    {
                        ShowToast($"加载路人集失败: {ex.Message}", isError: true);
                    });
                }
                finally
                {
                    _dispatcher?.TryEnqueue(() =>
                    {
                        if (append) setLoadingMore(false);
                        else setLoading(false);
                    });
                }
            });
        }

        // 依赖改变时重置为第一页加载
        var depsKey = $"resolved={isResolved}&filter={filter}&search={committedSearch}&self={selfPuuid}";
        UseEffect(
            () =>
            {
                LoadData(1, append: false);
                return () => { };
            },
            depsKey);

        // 搜索框防抖处理
        void OnSearchInputChanged(string text)
        {
            setSearchDraft(text);
            _searchDebounceTimer?.Dispose();
            _searchDebounceTimer = new Timer(_ =>
            {
                _dispatcher?.TryEnqueue(() =>
                {
                    setCommittedSearch(text.Trim());
                });
            }, null, 300, Timeout.Infinite);
        }

        void OnSearchSubmitted()
        {
            _searchDebounceTimer?.Dispose();
            setCommittedSearch(searchDraft.Trim());
        }

        void OnClearSearch()
        {
            _searchDebounceTimer?.Dispose();
            setSearchDraft(string.Empty);
            setCommittedSearch(string.Empty);
        }

        // 加载更多列表数据
        void HandleLoadMorePlayers()
        {
            if (loading || loadingMore || players.Count >= totalCount) return;
            LoadData(currentPage + 1, append: true);
        }

        // 展开与收起相遇对局
        void ToggleExpand(SavedPlayerRecord player)
        {
            if (expandedPuuid == player.Puuid)
            {
                setExpandedPuuid(null);
                setEncounteredGames(EmptyGames);
                setEncounteredTotal(0);
                setEncounteredPage(1);
                return;
            }

            setExpandedPuuid(player.Puuid);
            setGamesLoading(true);
            setEncounteredPage(1);
            _ = Task.Run(async () =>
            {
                try
                {
                    var res = await store.QueryEncounteredGamesAsync(player.Puuid, player.SelfPuuid, 1, GamesPageSize);
                    _dispatcher?.TryEnqueue(() =>
                    {
                        setEncounteredGames(res.Items);
                        setEncounteredTotal(res.TotalCount);
                    });
                }
                finally
                {
                    _dispatcher?.TryEnqueue(() => setGamesLoading(false));
                }
            });
        }

        // 加载更多相遇对局
        void HandleLoadMoreGames(SavedPlayerRecord player)
        {
            if (gamesLoading || encounteredGames.Count >= encounteredTotal) return;
            var nextPage = encounteredPage + 1;
            setGamesLoading(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    var res = await store.QueryEncounteredGamesAsync(player.Puuid, player.SelfPuuid, nextPage, GamesPageSize);
                    _dispatcher?.TryEnqueue(() =>
                    {
                        setEncounteredGames(encounteredGames.Concat(res.Items).ToList());
                        setEncounteredPage(nextPage);
                        setEncounteredTotal(res.TotalCount);
                    });
                }
                finally
                {
                    _dispatcher?.TryEnqueue(() => setGamesLoading(false));
                }
            });
        }

        // 保存标记
        void HandleSaveTag(SavedPlayerRecord player)
        {
            var targetTag = editingTag.Trim();
            _ = Task.Run(async () =>
            {
                await store.SaveTagAsync(player.Puuid, player.SelfPuuid, string.IsNullOrEmpty(targetTag) ? null : targetTag);
                _dispatcher?.TryEnqueue(() =>
                {
                    setEditingPuuid(null);
                    ShowToast("标记已更新");
                    LoadData(1, append: false);
                });
            });
        }

        // 删除玩家确认
        void HandleConfirmDelete(SavedPlayerRecord player)
        {
            _ = Task.Run(async () =>
            {
                await store.DeletePlayerAsync(player.Puuid, player.SelfPuuid);
                _dispatcher?.TryEnqueue(() =>
                {
                    setPendingDeletePuuid(null);
                    if (expandedPuuid == player.Puuid)
                    {
                        setExpandedPuuid(null);
                    }
                    ShowToast($"已删除玩家 {player.SummonerName}");
                    LoadData(1, append: false);
                });
            });
        }

        // 导出标记
        void HandleExport()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var path = Win32FileDialog.ShowSaveFileDialog("导出标记玩家", "tagged_players.json");
                    if (string.IsNullOrEmpty(path)) return;

                    var json = await store.ExportTaggedPlayersJsonAsync(selfPuuid);
                    await System.IO.File.WriteAllTextAsync(path, json);

                    _dispatcher?.TryEnqueue(() =>
                    {
                        ShowToast($"已成功导出到: {System.IO.Path.GetFileName(path)}");
                    });
                }
                catch (Exception ex)
                {
                    _dispatcher?.TryEnqueue(() =>
                    {
                        ShowToast($"导出失败: {ex.Message}", isError: true);
                    });
                }
            });
        }

        // 导入标记
        void HandleImport()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var path = Win32FileDialog.ShowOpenFileDialog("导入标记玩家");
                    if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

                    var json = await System.IO.File.ReadAllTextAsync(path);
                    var count = await store.ImportTaggedPlayersJsonAsync(json, selfPuuid);

                    _dispatcher?.TryEnqueue(() =>
                    {
                        ShowToast($"成功导入 {count} 名标记玩家");
                        LoadData(1, append: false);
                    });
                }
                catch (Exception ex)
                {
                    _dispatcher?.TryEnqueue(() =>
                    {
                        ShowToast($"导入失败: {ex.Message}", isError: true);
                    });
                }
            });
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            [
                // 1. 页头 Header（带导入导出）
                RenderHeader(totalCount, HandleImport, HandleExport, palette).Grid(row: 0),

                // 2. 工具栏 Toolbar（防抖搜索与清空、分类筛选）
                RenderToolbar(
                    searchDraft,
                    OnSearchInputChanged,
                    OnSearchSubmitted,
                    OnClearSearch,
                    filter,
                    setFilter,
                    palette).Grid(row: 1),

                // 3. 列表区域（带无限滚动与相遇历史）
                RenderList(
                    players,
                    totalCount,
                    loading,
                    loadingMore,
                    HandleLoadMorePlayers,
                    committedSearch,
                    OnClearSearch,
                    expandedPuuid,
                    encounteredGames,
                    encounteredTotal,
                    gamesLoading,
                    HandleLoadMoreGames,
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

                // 4. 浮动 Toast 反馈
                RenderToastHost(toasts, palette).Grid(row: 0),
            ]
        )
        .Padding(20, 16, 20, 16);
    }

    /// <summary>
    /// 顶部标题、胶囊 Badge 与导入/导出按钮组
    /// </summary>
    private static Element RenderHeader(long totalCount, Action onImport, Action onExport, YuumiPalette palette)
    {
        var badge = totalCount > 0
            ? Border(
                Caption($"{totalCount}")
                    .SemiBold()
                    .Foreground(palette.Accent)
                    .Margin(6, 1)
              )
              .Background(Theme.CardBackground)
              .WithBorder(palette.Accent, 1)
              .CornerRadius(10)
              .VerticalAlignment(VerticalAlignment.Center)
            : null;

        return Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                // 左侧标题与说明
                HStack(
                    8,
                    Subtitle("路人集"),
                    badge,
                    Body("·")
                        .Foreground(Theme.SecondaryText),
                    Caption("对局结束后自动记录同局的队友与对手，可添加标记以便选人阶段提醒")
                        .Foreground(Theme.SecondaryText)
                        .VerticalAlignment(VerticalAlignment.Center)
                )
                .VerticalAlignment(VerticalAlignment.Center)
                .Grid(column: 0),

                // 右侧导入/导出操作
                HStack(
                    8,
                    Button(Caption("导入标记"), onImport)
                        .AutomationName("导入标记")
                        .Padding(12, 5),
                    Button(Caption("导出标记"), onExport)
                        .AutomationName("导出标记")
                        .Padding(12, 5)
                )
                .VerticalAlignment(VerticalAlignment.Center)
                .Grid(column: 1),
            ]
        )
        .Margin(0, 0, 0, 14);
    }

    /// <summary>
    /// 搜索框与筛选 Tabs
    /// </summary>
    private static Element RenderToolbar(
        string searchDraft,
        Action<string> onSearchInput,
        Action onSearchSubmit,
        Action onClearSearch,
        string filter,
        Action<string> setFilter,
        YuumiPalette palette)
    {
        var clearBtn = !string.IsNullOrEmpty(searchDraft)
            ? Button(Caption("✕"), onClearSearch)
                .AutomationName("清空搜索")
                .Padding(6, 2)
                .VerticalAlignment(VerticalAlignment.Center)
            : null;

        var searchBox = HStack(
            6,
            TextBox(
                searchDraft,
                onSearchInput,
                "搜索召唤师名称、TagLine 或标记..."
            )
            .AutomationName("搜索召唤师名称、TagLine 或标记")
            .Width(280)
            .Height(32),
            clearBtn
        )
        .VerticalAlignment(VerticalAlignment.Center);

        Element FilterButton(string label, string key)
        {
            var isSelected = filter == key;
            var btn = Button(Caption(label), () => setFilter(key))
                .AutomationName(label)
                .Padding(14, 5);

            return isSelected
                ? btn.Background(palette.Accent)
                : btn;
        }

        var filterGroup = HStack(
            6,
            FilterButton("全部", "all"),
            FilterButton("标记玩家", "tagged"),
            FilterButton("多次相遇", "multiple")
        );

        return Grid(
            [GridSize.Auto, GridSize.Star()],
            [GridSize.Auto],
            [
                searchBox.Grid(column: 0),
                filterGroup.HorizontalAlignment(HorizontalAlignment.Right).Grid(column: 1),
            ]
        )
        .Margin(0, 0, 0, 14);
    }

    /// <summary>
    /// 卡片列表视图与分页
    /// </summary>
    private static Element RenderList(
        IReadOnlyList<SavedPlayerRecord> players,
        long totalCount,
        bool loading,
        bool loadingMore,
        Action onLoadMorePlayers,
        string committedSearch,
        Action onClearSearch,
        string? expandedPuuid,
        IReadOnlyList<EncounteredGameRecord> encounteredGames,
        long encounteredTotal,
        bool gamesLoading,
        Action<SavedPlayerRecord> onLoadMoreGames,
        string? editingPuuid,
        string editingTag,
        Action<string> setEditingTag,
        Action<string?> setEditingPuuid,
        string? pendingDeletePuuid,
        Action<string?> setPendingDeletePuuid,
        Action<SavedPlayerRecord> onToggleExpand,
        Action<SavedPlayerRecord> onSaveTag,
        Action<SavedPlayerRecord> onConfirmDelete,
        YuumiPalette palette)
    {
        if (loading && players.Count == 0)
        {
            return VStack(
                12,
                ProgressRing().Width(32).Height(32).IsActive(true),
                TextBlock("正在加载路人集数据...").Foreground(Theme.SecondaryText)
            )
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Center);
        }

        if (players.Count == 0)
        {
            if (!string.IsNullOrEmpty(committedSearch))
            {
                return VStack(
                    8,
                    BodyStrong($"未找到与 “{committedSearch}” 相关的玩家")
                        .Foreground(Theme.SecondaryText)
                        .HorizontalAlignment(HorizontalAlignment.Center),
                    Button(Caption("清空搜索条件"), onClearSearch)
                        .Padding(14, 5)
                        .HorizontalAlignment(HorizontalAlignment.Center)
                        .AutomationName("清空搜索条件")
                )
                .HorizontalAlignment(HorizontalAlignment.Center)
                .VerticalAlignment(VerticalAlignment.Center);
            }

            return VStack(
                6,
                BodyStrong("暂无路人玩家记录")
                    .Foreground(Theme.SecondaryText)
                    .HorizontalAlignment(HorizontalAlignment.Center),
                Caption("对局结束后同局的队友与对手会自动出现在这里")
                    .Foreground(Theme.SecondaryText)
                    .HorizontalAlignment(HorizontalAlignment.Center)
            )
            .HorizontalAlignment(HorizontalAlignment.Center)
            .VerticalAlignment(VerticalAlignment.Center);
        }

        var cards = players.Select(p => RenderPlayerCard(
            p,
            expandedPuuid == p.Puuid,
            encounteredGames,
            encounteredTotal,
            gamesLoading,
            () => onLoadMoreGames(p),
            editingPuuid == p.Puuid,
            editingTag,
            setEditingTag,
            setEditingPuuid,
            pendingDeletePuuid == p.Puuid,
            setPendingDeletePuuid,
            () => onToggleExpand(p),
            () => onSaveTag(p),
            () => onConfirmDelete(p),
            palette
        )).ToList();

        // 列表底部加载状态或按钮
        Element? footerSentinel = null;
        if (loadingMore)
        {
            footerSentinel = HStack(
                8,
                ProgressRing().Width(16).Height(16).IsActive(true),
                Caption("正在加载更多玩家...").Foreground(Theme.SecondaryText)
            )
            .HorizontalAlignment(HorizontalAlignment.Center)
            .Margin(0, 10, 0, 16);
        }
        else if (players.Count < totalCount)
        {
            footerSentinel = Button(Caption("加载更多玩家"), onLoadMorePlayers)
                .AutomationName("加载更多玩家")
                .Padding(18, 5)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 16);
        }
        else if (players.Count >= totalCount && totalCount > 0)
        {
            footerSentinel = Caption("已加载全部玩家记录")
                .Foreground(Theme.SecondaryText)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 10, 0, 16);
        }

        if (footerSentinel is not null)
        {
            cards.Add(footerSentinel);
        }

        return ScrollViewer(
            VStack(cards.ToArray()).Spacing(8)
        );
    }

    /// <summary>
    /// 单个玩家卡片
    /// </summary>
    private static Element RenderPlayerCard(
        SavedPlayerRecord player,
        bool isExpanded,
        IReadOnlyList<EncounteredGameRecord> encounteredGames,
        long encounteredTotal,
        bool gamesLoading,
        Action onLoadMoreGames,
        bool isEditing,
        string editingTag,
        Action<string> setEditingTag,
        Action<string?> setEditingPuuid,
        bool isPendingDelete,
        Action<string?> setPendingDeletePuuid,
        Action onToggle,
        Action onSaveTag,
        Action onConfirmDelete,
        YuumiPalette palette)
    {
        // 1. 头像区（召唤师头像 + 英雄头像并列）
        var avatarRow = HStack(
            6,
            Component<LcuImage, LcuImageProps>(
                new LcuImageProps($"/lol-game-data/assets/v1/profile-icons/{player.ProfileIconId}.jpg", 40, 20)),
            player.ChampionId > 0
                ? Component<LcuImage, LcuImageProps>(
                    new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{player.ChampionId}.png", 32, 16))
                : null
        )
        .VerticalAlignment(VerticalAlignment.Center)
        .Margin(0, 0, 12, 0);

        // 2. 玩家名称与徽章
        var modeName = FormatQueue(player.LastQueueType);
        var infoStack = VStack(
            4,
            HStack(
                6,
                BodyStrong(player.SummonerName),
                Caption(string.IsNullOrEmpty(player.TagLine) ? "#—" : $"#{player.TagLine}")
                    .Foreground(Theme.SecondaryText)
                    .VerticalAlignment(VerticalAlignment.Center)
            ),
            HStack(
                6,
                !string.IsNullOrEmpty(player.Tag)
                    ? Border(
                        Caption(player.Tag)
                            .Margin(6, 1)
                      )
                      .Background(palette.Accent)
                      .CornerRadius(4)
                    : null,

                !string.IsNullOrEmpty(modeName)
                    ? Border(
                        Caption(modeName)
                            .Foreground(Theme.SecondaryText)
                            .Margin(6, 1)
                      )
                      .Background(Theme.CardBackground)
                      .WithBorder(Theme.CardStroke, 1)
                      .CornerRadius(4)
                    : null,

                player.EncounterCount >= 2
                    ? Border(
                        Caption($"相遇 {player.EncounterCount} 次")
                            .Foreground(palette.Accent)
                            .Margin(6, 1)
                      )
                      .Background(Theme.CardBackground)
                      .WithBorder(palette.Accent, 1)
                      .CornerRadius(4)
                    : null
            )
        )
        .VerticalAlignment(VerticalAlignment.Center);

        // 3. 右侧时间与操作
        var timeText = FormatTime(player.LastMetAt);
        var rightStack = HStack(
            6,
            Caption(timeText)
                .Foreground(Theme.SecondaryText)
                .VerticalAlignment(VerticalAlignment.Center)
                .Margin(0, 0, 12, 0),

            Button(Caption("编辑标记"), () =>
            {
                setEditingTag(player.Tag ?? string.Empty);
                setEditingPuuid(player.Puuid);
            })
            .AutomationName("编辑标记")
            .Padding(8, 3),

            Button(Caption("删除"), () => setPendingDeletePuuid(player.Puuid))
                .AutomationName("删除")
                .Padding(8, 3)
                .Foreground(palette.LoseAccent),

            Button(Caption(isExpanded ? "▾" : "▸"), onToggle)
                .AutomationName("展开或折叠对局历史")
                .Padding(8, 3)
        )
        .VerticalAlignment(VerticalAlignment.Center);

        // 可点击主体行（头像 + 名字 + 徽章，点击整块直接展开/折叠对局历史）
        var clickableBody = Button(
            HStack(
                avatarRow,
                infoStack
            )
            .VerticalAlignment(VerticalAlignment.Center),
            onToggle
        )
        .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
        .BorderThickness(0)
        .Padding(4, 2)
        .HorizontalAlignment(HorizontalAlignment.Stretch)
        .HorizontalContentAlignment(HorizontalAlignment.Left)
        .AutomationName($"查看 {player.SummonerName} 的相遇对局历史");

        // 卡片主行
        var mainRow = Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                clickableBody.Grid(column: 0),
                rightStack.Grid(column: 1),
            ]
        );

        // 安全删除确认条（防误触）
        Element? deleteConfirmRow = null;
        if (isPendingDelete)
        {
            deleteConfirmRow = Border(
                HStack(
                    10,
                    Caption("⚠️ 确定要删除该玩家及其所有相遇记录吗？")
                        .Foreground(palette.LoseAccent)
                        .VerticalAlignment(VerticalAlignment.Center),
                    Button(Caption("确认删除"), onConfirmDelete)
                        .Padding(10, 3)
                        .Background(palette.LoseAccent)
                        .AutomationName("确认删除"),
                    Button(Caption("取消"), () => setPendingDeletePuuid(null))
                        .Padding(10, 3)
                        .AutomationName("取消")
                )
                .Padding(10, 6)
            )
            .Background(Theme.CardBackground)
            .WithBorder(palette.LoseAccent, 1)
            .CornerRadius(6)
            .Margin(0, 8, 0, 0);
        }

        // 编辑行
        Element? editRow = null;
        if (isEditing)
        {
            editRow = HStack(
                6,
                TextBox(
                    editingTag,
                    setEditingTag,
                    "添加标记..."
                )
                .AutomationName("编辑玩家标记")
                .Width(260)
                .Height(30),
                Button(Caption("保存"), onSaveTag)
                    .AutomationName("保存")
                    .Padding(12, 4)
                    .Background(palette.Accent),
                Button(Caption("取消"), () => setEditingPuuid(null))
                    .AutomationName("取消")
                    .Padding(12, 4)
            )
            .Margin(0, 10, 0, 0);
        }

        // 展开对局列表
        Element? historySection = null;
        if (isExpanded)
        {
            if (gamesLoading && encounteredGames.Count == 0)
            {
                historySection = HStack(
                    8,
                    ProgressRing().Width(18).Height(18).IsActive(true),
                    Caption("加载相遇对局...").Foreground(Theme.SecondaryText)
                )
                .Margin(0, 10, 0, 4);
            }
            else if (encounteredGames.Count == 0)
            {
                historySection = Caption("暂无相遇对局记录")
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 10, 0, 4);
            }
            else
            {
                var gameRows = encounteredGames.Select(g =>
                    HStack(
                        12,
                        Caption(FormatQueue(g.QueueType))
                            .Width(110),
                        Caption(FormatTime(g.UpdateAt))
                            .Foreground(Theme.SecondaryText)
                    )
                    .Margin(0, 2, 0, 2)
                ).ToList();

                if (encounteredGames.Count < encounteredTotal)
                {
                    gameRows.Add(
                        HStack(
                            8,
                            Button(Caption("加载更多对局记录"), onLoadMoreGames)
                                .Padding(10, 3)
                                .AutomationName("加载更多对局记录")
                        )
                        .Margin(0, 4, 0, 2)
                    );
                }

                historySection = VStack(
                    4,
                    Caption("相遇对局历史：")
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 8, 0, 2),
                    VStack(gameRows.ToArray()).Spacing(2)
                )
                .Margin(0, 6, 0, 0);
            }
        }

        return Border(
            VStack(
                4,
                mainRow,
                deleteConfirmRow,
                editRow,
                historySection
            )
            .Padding(14, 10)
        )
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(8);
    }

    /// <summary>
    /// 浮动 Toast 呈现
    /// </summary>
    private static Element RenderToastHost(IReadOnlyList<ToastItem> toasts, YuumiPalette palette)
    {
        if (toasts.Count == 0) return Border(null);

        var items = toasts.Select(t =>
            Border(
                Caption(t.Message)
                    .Foreground(t.IsError ? palette.LoseAccent : palette.Accent)
                    .Margin(12, 6)
            )
            .Background(Theme.CardBackground)
            .WithBorder(t.IsError ? palette.LoseAccent : palette.Accent, 1)
            .CornerRadius(6)
        ).ToArray();

        return VStack(6, items)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .VerticalAlignment(VerticalAlignment.Top)
            .Margin(0, 8, 16, 0);
    }

    private static string FormatQueue(string? queueType)
    {
        if (string.IsNullOrEmpty(queueType)) return "未知模式";
        if (int.TryParse(queueType, out var qid))
        {
            return MatchQueueCatalog.Resolve(qid).Name;
        }
        return queueType;
    }

    private static string FormatTime(long? unixMs)
    {
        if (!unixMs.HasValue || unixMs.Value <= 0) return "尚未相遇";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value).ToLocalTime();
        return dt.ToString("yyyy-MM-dd HH:mm");
    }
}
