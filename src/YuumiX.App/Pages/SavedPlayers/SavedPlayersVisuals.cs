using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
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
    /// <summary>顶部标题、胶囊 Badge 与刷新/导入/导出按钮组</summary>
    internal static Element RenderHeader(
        long totalCount,
        Action onRefresh,
        bool isRefreshing,
        Action onImport,
        Action onExport,
        YuumiPalette palette)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        var title = i18n?.T("savedPlayersPage.title", "路人集") ?? "路人集";
        var desc = i18n?.T("savedPlayersPage.desc", "对局结束后自动记录同局的队友与对手，可添加标记以便选人阶段提醒") ?? "对局结束后自动记录同局的队友与对手，可添加标记以便选人阶段提醒";
        var refreshBtnText = isRefreshing
            ? (i18n?.T("savedPlayersPage.refreshing", "正在刷新...") ?? "正在刷新...")
            : (i18n?.T("savedPlayersPage.refresh", "刷新") ?? "刷新");
        var importBtnText = i18n?.T("savedPlayersPage.import", "导入标记") ?? "导入标记";
        var exportBtnText = i18n?.T("savedPlayersPage.export", "导出标记") ?? "导出标记";

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
                    Subtitle(title)
                        .HeadingLevel(AutomationHeadingLevel.Level2),
                    badge,
                    Body("·")
                        .Foreground(Theme.SecondaryText),
                    Caption(desc)
                        .Foreground(Theme.SecondaryText)
                        .VerticalAlignment(VerticalAlignment.Center)
                )
                .VerticalAlignment(VerticalAlignment.Center)
                .Grid(column: 0),

                // 右侧刷新/导入/导出操作
                HStack(
                    8,
                    Button(Body(refreshBtnText), onRefresh)
                        .AutomationName(refreshBtnText)
                        .IsEnabled(!isRefreshing)
                        .Padding(12, 5),
                    Button(Body(importBtnText), onImport)
                        .AutomationName(importBtnText)
                        .Padding(12, 5),
                    Button(Body(exportBtnText), onExport)
                        .AutomationName(exportBtnText)
                        .Padding(12, 5)
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
        var i18n = Composition.AppHost.Services?.I18n;
        var placeholder = i18n?.T("savedPlayersPage.searchPlaceholder", "搜索召唤师名称、TagLine 或标记...") ?? "搜索召唤师名称、TagLine 或标记...";
        var filterAllText = i18n?.T("savedPlayersPage.filterAll", "全部") ?? "全部";
        var filterTaggedText = i18n?.T("savedPlayersPage.filterTagged", "标记玩家") ?? "标记玩家";
        var filterMultipleText = i18n?.T("savedPlayersPage.filterMultiple", "多次相遇") ?? "多次相遇";

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
                placeholder
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
            var btn = Button(Body(label), () => setFilter(key))
                .AutomationName(label)
                .Padding(12, 5);

            return isSelected
                ? btn.Background(Theme.Accent)
                : btn;
        }

        var filterGroup = HStack(
            6,
            FilterButton(filterAllText, "all"),
            FilterButton(filterTaggedText, "tagged"),
            FilterButton(filterMultipleText, "multiple")
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
            // 加载中且暂无数据时直接用卡片骨架占位：与真实卡片同结构（头像 + 名字 + 右侧操作），
            // 列表到达时整列原地填充，消除白屏与高度跳变。
            return SkeletonList();
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
                    Button(Body("清空搜索条件"), onClearSearch)
                        .Padding(12, 5)
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
                Body("对局结束后同局的队友与对手会自动出现在这里")
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
                Body("正在加载更多玩家...").Foreground(Theme.SecondaryText)
            )
            .HorizontalAlignment(HorizontalAlignment.Center)
            .Margin(0, 8, 0, 16);
        }
        else if (players.Count < totalCount)
        {
            footerSentinel = Button(Body("加载更多玩家"), onLoadMorePlayers)
                .AutomationName("加载更多玩家")
                .Padding(16, 4)
                .HorizontalAlignment(HorizontalAlignment.Center)
                .Margin(0, 8, 0, 16);
        }
        else if (players.Count >= totalCount && totalCount > 0)
        {
            footerSentinel = Body("已加载全部玩家记录")
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

    /// <summary>
    /// 路人集卡片骨架：与 <c>SavedPlayerCard</c> 的收起态同结构——
    /// 40 头像圆 + 名字与标签两行条 + 右侧操作位靠右，padding 与圆角一致。
    /// 铺 4 张：够填满首屏视口，又不至于让骨架比真实列表长太多。
    /// </summary>
    private static Element SkeletonList(int count = 4)
    {
        var cards = new Element[count];
        for (int i = 0; i < count; i++)
        {
            cards[i] = Border(
                    Grid(
                        [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                        [GridSize.Auto],
                        [
                            Border(Empty())
                                .Width(40)
                                .Height(40)
                                .CornerRadius(20)
                                .Background(Theme.SubtleFill)
                                .Margin(0, 0, 12, 0)
                                .Grid(row: 0, column: 0),
                            VStack(4,
                                Border(Empty())
                                    .Width(140)
                                    .Height(16)
                                    .CornerRadius(4)
                                    .Background(Theme.SubtleFill)
                                    .HAlign(HorizontalAlignment.Left),
                                Border(Empty())
                                    .Width(64)
                                    .Height(14)
                                    .CornerRadius(4)
                                    .Background(Theme.SubtleFill)
                                    .HAlign(HorizontalAlignment.Left))
                                .VAlign(VerticalAlignment.Center)
                                .Grid(row: 0, column: 1),
                            Border(Empty())
                                .Width(72)
                                .Height(20)
                                .CornerRadius(4)
                                .Background(Theme.SubtleFill)
                                .VAlign(VerticalAlignment.Center)
                                .Grid(row: 0, column: 2),
                        ]))
                .Padding(12, 8)
                .Background(Theme.CardBackground)
                .WithBorder(Theme.CardStroke, 1)
                .CornerRadius(YuumiTheme.OverlayRadius);
        }

        return ScrollViewer(VStack(cards).Spacing(8));
    }
}
