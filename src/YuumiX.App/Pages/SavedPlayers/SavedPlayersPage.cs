using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Parsers;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>
/// 路人集页面：对齐 docs/pic/路人集.png 与原 SavedPlayers.vue。
///
/// 功能：
///   - 顶栏：标题 + 总数胶囊 Badge + 说明文案 + 导入/导出
///   - 工具栏：搜索框（支持召唤师名、TagLine、标记） + "全部" / "标记玩家" / "多次相遇" 切换
///   - 玩家卡片列表：双头像（召唤师 + 英雄）、召唤师名、#TagLine、模式/频次/自定义标记 Badge、相遇时间
///   - 操作：展开相遇对局列表、内联编辑自定义标记、删除玩家
/// </summary>
public sealed class SavedPlayersPage : Component
{
    private static readonly IReadOnlyList<SavedPlayerRecord> EmptyPlayers = [];
    private static readonly IReadOnlyList<EncounteredGameRecord> EmptyGames = [];

    public override Element Render()
    {
        var services = UseContext(AppHost.Context);
        var theme = UseContext(AppHost.ThemeContext);
        var store = services.Services.GetRequiredService<SavedPlayersStore>();
        var summonerService = services.Services.GetService<ISummonerService>();

        var (palette, setPalette) = UseState(theme.Current, threadSafe: true);
        UseEffect(
            () =>
            {
                var subscription = theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        var (players, setPlayers) = UseState(EmptyPlayers, threadSafe: true);
        var (totalCount, setTotalCount) = UseState(0L, threadSafe: true);
        var (loading, setLoading) = UseState(true, threadSafe: true);
        var (filter, setFilter) = UseState("all", threadSafe: true); // "all", "tagged", "multiple"
        var (searchQuery, setSearchQuery) = UseState(string.Empty, threadSafe: true);
        var (selfPuuid, setSelfPuuid) = UseState<string?>(null, threadSafe: true);

        // 展开与编辑状态
        var (expandedPuuid, setExpandedPuuid) = UseState<string?>(null, threadSafe: true);
        var (encounteredGames, setEncounteredGames) = UseState(EmptyGames, threadSafe: true);
        var (gamesLoading, setGamesLoading) = UseState(false, threadSafe: true);
        var (editingPuuid, setEditingPuuid) = UseState<string?>(null, threadSafe: true);
        var (editingTag, setEditingTag) = UseState(string.Empty, threadSafe: true);

        // 获取当前账号 selfPuuid
        UseEffect(
            () =>
            {
                if (summonerService is not null)
                {
                    _ = summonerService.GetCurrentSummonerAsync().ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result is not null)
                        {
                            setSelfPuuid(t.Result.Puuid);
                        }
                    }, TaskScheduler.Default);
                }
                return () => { };
            },
            0);

        // 数据加载
        void RefreshList()
        {
            setLoading(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await store.QueryPlayersAsync(
                        selfPuuid: selfPuuid,
                        page: 1,
                        pageSize: 100,
                        filter: filter,
                        search: searchQuery);

                    setPlayers(result.Items);
                    setTotalCount(result.TotalCount);
                }
                finally
                {
                    setLoading(false);
                }
            });
        }

        var depsKey = $"filter={filter}&search={searchQuery}&self={selfPuuid}";
        UseEffect(
            () =>
            {
                RefreshList();
                return () => { };
            },
            depsKey);

        // 展开相遇记录
        void ToggleExpand(SavedPlayerRecord player)
        {
            if (expandedPuuid == player.Puuid)
            {
                setExpandedPuuid(null);
                setEncounteredGames(EmptyGames);
                return;
            }

            setExpandedPuuid(player.Puuid);
            setGamesLoading(true);
            _ = Task.Run(async () =>
            {
                try
                {
                    var res = await store.QueryEncounteredGamesAsync(player.Puuid, selfPuuid, 1, 20);
                    setEncounteredGames(res.Items);
                }
                finally
                {
                    setGamesLoading(false);
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
                setEditingPuuid(null);
                RefreshList();
            });
        }

        // 删除玩家
        void HandleDelete(SavedPlayerRecord player)
        {
            _ = Task.Run(async () =>
            {
                await store.DeletePlayerAsync(player.Puuid, player.SelfPuuid);
                if (expandedPuuid == player.Puuid)
                {
                    setExpandedPuuid(null);
                }
                RefreshList();
            });
        }

        return Grid(
            [GridSize.Star()],
            [GridSize.Auto, GridSize.Auto, GridSize.Star()],
            [
                // 1. 页头 Header
                RenderHeader(totalCount, palette).Grid(row: 0),

                // 2. 工具栏 Toolbar
                RenderToolbar(searchQuery, setSearchQuery, filter, setFilter, palette).Grid(row: 1),

                // 3. 列表区域
                RenderList(
                    players,
                    loading,
                    expandedPuuid,
                    encounteredGames,
                    gamesLoading,
                    editingPuuid,
                    editingTag,
                    setEditingTag,
                    setEditingPuuid,
                    ToggleExpand,
                    HandleSaveTag,
                    HandleDelete,
                    palette).Grid(row: 2),
            ]
        )
        .Padding(20, 16, 20, 16);
    }

    /// <summary>
    /// 顶部标题与说明
    /// </summary>
    private static Element RenderHeader(long totalCount, YuumiPalette palette)
    {
        var badge = Border(
            TextBlock($"{totalCount}")
                .FontSize(11)
                .FontWeight(FontWeights.SemiBold)
                .Foreground(palette.Accent)
                .Margin(6, 1)
        )
        .Background(Theme.CardBackground)
        .WithBorder(palette.Accent, 1)
        .CornerRadius(10)
        .VerticalAlignment(VerticalAlignment.Center);

        return Grid(
            [GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                // 左侧标题与描述
                HStack(
                    8,
                    TextBlock("路人集")
                        .FontSize(22)
                        .FontWeight(FontWeights.Bold),
                    badge,
                    TextBlock("·")
                        .FontSize(14)
                        .Foreground(Theme.SecondaryText),
                    TextBlock("对局结束后自动记录同局的队友与对手，可添加标记以便选人阶段提醒")
                        .FontSize(12)
                        .Foreground(Theme.SecondaryText)
                        .VerticalAlignment(VerticalAlignment.Center)
                )
                .VerticalAlignment(VerticalAlignment.Center)
                .Grid(column: 0),

                // 右侧导入/导出
                HStack(
                    8,
                    Button("导入标记")
                        .FontSize(12)
                        .Padding(12, 4),
                    Button("导出标记")
                        .FontSize(12)
                        .Padding(12, 4)
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
        string searchQuery,
        Action<string> setSearchQuery,
        string filter,
        Action<string> setFilter,
        YuumiPalette palette)
    {
        var searchBox = TextBox(
            searchQuery,
            setSearchQuery,
            "搜索召唤师名称、TagLine 或标记..."
        )
        .AutomationName("搜索召唤师名称、TagLine 或标记")
        .Width(280)
        .Height(32);

        Element FilterButton(string label, string key)
        {
            var isSelected = filter == key;
            var btn = Button(label, () => setFilter(key))
                .AutomationName(label)
                .FontSize(12)
                .Padding(14, 4);

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
    /// 卡片列表视图
    /// </summary>
    private static Element RenderList(
        IReadOnlyList<SavedPlayerRecord> players,
        bool loading,
        string? expandedPuuid,
        IReadOnlyList<EncounteredGameRecord> encounteredGames,
        bool gamesLoading,
        string? editingPuuid,
        string editingTag,
        Action<string> setEditingTag,
        Action<string?> setEditingPuuid,
        Action<SavedPlayerRecord> onToggleExpand,
        Action<SavedPlayerRecord> onSaveTag,
        Action<SavedPlayerRecord> onDelete,
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
            return VStack(
                6,
                TextBlock("暂无路人玩家记录")
                    .FontSize(15)
                    .FontWeight(FontWeights.SemiBold)
                    .Foreground(Theme.SecondaryText)
                    .HorizontalAlignment(HorizontalAlignment.Center),
                TextBlock("在完成一局游戏后，系统将自动记录同局的队友与对手")
                    .FontSize(12)
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
            gamesLoading,
            editingPuuid == p.Puuid,
            editingTag,
            setEditingTag,
            setEditingPuuid,
            () => onToggleExpand(p),
            () => onSaveTag(p),
            () => onDelete(p),
            palette
        )).ToArray();

        return ScrollViewer(
            VStack(8, cards)
        );
    }

    /// <summary>
    /// 单个玩家卡片
    /// </summary>
    private static Element RenderPlayerCard(
        SavedPlayerRecord player,
        bool isExpanded,
        IReadOnlyList<EncounteredGameRecord> encounteredGames,
        bool gamesLoading,
        bool isEditing,
        string editingTag,
        Action<string> setEditingTag,
        Action<string?> setEditingPuuid,
        Action onToggle,
        Action onSaveTag,
        Action onDelete,
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

        // 2. 信息行与 Badge
        var modeName = FormatQueue(player.LastQueueType);
        var infoStack = VStack(
            4,
            // 第一行：名字 + #TagLine
            HStack(
                6,
                TextBlock(player.SummonerName)
                    .FontSize(14)
                    .FontWeight(FontWeights.SemiBold),
                TextBlock(string.IsNullOrEmpty(player.TagLine) ? "#—" : $"#{player.TagLine}")
                    .FontSize(12)
                    .Foreground(Theme.SecondaryText)
                    .VerticalAlignment(VerticalAlignment.Center)
            ),

            // 第二行：Badge 组
            HStack(
                6,
                // 自定义 Tag
                !string.IsNullOrEmpty(player.Tag)
                    ? Border(
                        TextBlock(player.Tag)
                            .FontSize(11)
                            .FontWeight(FontWeights.SemiBold)
                            .Margin(6, 1)
                      )
                      .Background(palette.Accent)
                      .CornerRadius(4)
                    : null,

                // 模式 Badge
                !string.IsNullOrEmpty(modeName)
                    ? Border(
                        TextBlock(modeName)
                            .FontSize(11)
                            .Foreground(Theme.SecondaryText)
                            .Margin(6, 1)
                      )
                      .Background(Theme.CardBackground)
                      .WithBorder(Theme.CardStroke, 1)
                      .CornerRadius(4)
                    : null,

                // 多次相遇 Badge
                player.EncounterCount >= 2
                    ? Border(
                        TextBlock($"相遇 {player.EncounterCount} 次")
                            .FontSize(11)
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
            TextBlock(timeText)
                .FontSize(12)
                .Foreground(Theme.SecondaryText)
                .VerticalAlignment(VerticalAlignment.Center)
                .Margin(0, 0, 12, 0),

            Button("编辑标记", () =>
            {
                setEditingTag(player.Tag ?? string.Empty);
                setEditingPuuid(player.Puuid);
            })
            .FontSize(11)
            .Padding(8, 3),

            Button("删除", onDelete)
                .FontSize(11)
                .Padding(8, 3)
                .Foreground(palette.LoseAccent),

            Button(isExpanded ? "▾" : "▸", onToggle)
                .AutomationName("展开或折叠对局历史")
                .FontSize(12)
                .Padding(8, 3)
        )
        .VerticalAlignment(VerticalAlignment.Center);

        // 卡片主行
        var mainRow = Grid(
            [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            [GridSize.Auto],
            [
                avatarRow.Grid(column: 0),
                infoStack.Grid(column: 1),
                rightStack.Grid(column: 2),
            ]
        );

        // 编辑行（若处于编辑中）
        Element? editRow = null;
        if (isEditing)
        {
            editRow = HStack(
                6,
                TextBox(
                    editingTag,
                    setEditingTag,
                    "请输入该玩家的自定义标记/备注..."
                )
                .AutomationName("编辑玩家标记")
                .Width(260)
                .Height(30),
                Button("保存", onSaveTag)
                    .FontSize(11)
                    .Padding(12, 4)
                    .Background(palette.Accent),
                Button("取消", () => setEditingPuuid(null))
                    .FontSize(11)
                    .Padding(12, 4)
            )
            .Margin(0, 10, 0, 0);
        }

        // 展开对局列表
        Element? historySection = null;
        if (isExpanded)
        {
            if (gamesLoading)
            {
                historySection = HStack(
                    8,
                    ProgressRing().Width(18).Height(18).IsActive(true),
                    TextBlock("加载相遇对局...").FontSize(11).Foreground(Theme.SecondaryText)
                )
                .Margin(0, 10, 0, 4);
            }
            else if (encounteredGames.Count == 0)
            {
                historySection = TextBlock("无具体相遇对局记录")
                    .FontSize(11)
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 10, 0, 4);
            }
            else
            {
                var gameRows = encounteredGames.Select(g =>
                    HStack(
                        12,
                        TextBlock(FormatQueue(g.QueueType))
                            .FontSize(11)
                            .FontWeight(FontWeights.SemiBold)
                            .Width(90),
                        TextBlock(FormatTime(g.UpdateAt))
                            .FontSize(11)
                            .Foreground(Theme.SecondaryText)
                    )
                    .Margin(0, 2, 0, 2)
                ).ToArray();

                historySection = VStack(
                    4,
                    TextBlock("相遇对局历史：")
                        .FontSize(11)
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 8, 0, 2),
                    VStack(2, gameRows)
                )
                .Margin(0, 6, 0, 0);
            }
        }

        return Border(
            VStack(
                4,
                mainRow,
                editRow,
                historySection
            )
            .Padding(14, 10)
        )
        .Background(Theme.CardBackground)
        .WithBorder(Theme.CardStroke, 1)
        .CornerRadius(8);
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
        if (!unixMs.HasValue || unixMs.Value <= 0) return "—";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value).ToLocalTime();
        return dt.ToString("yyyy-MM-dd HH:mm");
    }
}
