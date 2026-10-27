using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.SavedPlayers.Models;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>
/// 路人集页的页头、工具栏与列表容器（自 <c>SavedPlayersPage</c> 拆出，R14）。
/// </summary>
internal static class SavedPlayersVisuals
{
    /// <summary>顶部标题、胶囊 Badge 与导入/导出按钮组</summary>
    internal static Element RenderHeader(long totalCount, Action onImport, Action onExport, YuumiPalette palette)
    {
        var badge = totalCount > 0
            ? Badge.Create(
                Caption($"{totalCount}")
                    .SemiBold()
                    .Foreground(Theme.Accent)
                    .Margin(4, 1),
                Theme.CardBackground,
                Theme.Accent)
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
                        .Padding(12, 4),
                    Button(Caption("导出标记"), onExport)
                        .AutomationName("导出标记")
                        .Padding(12, 4)
                )
                .VerticalAlignment(VerticalAlignment.Center)
                .Grid(column: 1),
            ]
        )
        .Margin(0, 0, 0, 12);
    }

    /// <summary>搜索框与筛选 Tabs</summary>
    internal static Element RenderToolbar(
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
                .Padding(4, 2)
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
                .Padding(12, 4);

            return isSelected
                ? btn.Background(Theme.Accent)
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
        .Margin(0, 0, 0, 12);
    }

    /// <summary>卡片列表视图与分页（空态三分支：加载中 / 搜索无结果 / 无记录）</summary>
    internal static Element RenderList(
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
                        .Padding(12, 4)
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

        var cards = players.Select(p => SavedPlayerCard.Render(
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
            .Margin(0, 8, 0, 16);
        }
        else if (players.Count < totalCount)
        {
            footerSentinel = Button(Caption("加载更多玩家"), onLoadMorePlayers)
                .AutomationName("加载更多玩家")
                .Padding(16, 4)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 16);
        }
        else if (players.Count >= totalCount && totalCount > 0)
        {
            footerSentinel = Caption("已加载全部玩家记录")
                .Foreground(Theme.SecondaryText)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 16);
        }

        if (footerSentinel is not null)
        {
            cards.Add(footerSentinel);
        }

        return ScrollViewer(
            VStack(cards.ToArray()).Spacing(8)
        );
    }
}
