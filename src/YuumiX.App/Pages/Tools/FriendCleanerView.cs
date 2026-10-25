using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.App.Pages.Tools;

/// <summary>
/// 好友管理与僵尸号清理视图组件（对应原 FriendCleanerCard.vue）。
/// </summary>
internal static class FriendCleanerView
{
    private static readonly string[] FilterLabels =
    [
        "超过 180 天未打游戏",
        "超过 360 天未打游戏",
        "超过 90 天未打游戏",
        "从未打过游戏 (0场)",
        "🔒 战绩已隐藏 (安全保护)",
        "全部好友",
    ];

    public static Element Render(
        IReadOnlyList<FriendEntry> allFriends,
        bool isScanning,
        bool isDeleting,
        int scanCompleted,
        int scanTotal,
        string searchQuery,
        Action<string> onSearchQueryChanged,
        int filterIndex,
        Action<int> onFilterIndexChanged,
        HashSet<string> selectedIds,
        Action<string> onToggleSelect,
        Action onSelectAllFiltered,
        Action onClearSelection,
        bool isConfirmingDelete,
        Action onPromptDelete,
        Action onCancelDelete,
        Action onConfirmDelete,
        Action onStartScan,
        YuumiPalette palette)
    {
        // 筛选逻辑
        var filtered = FilterFriends(allFriends, searchQuery, filterIndex);

        Element filterBar = Grid(
            [GridSize.Star(), GridSize.Auto, GridSize.Auto],
            [GridSize.Auto],
            [
                TextBox(searchQuery, onSearchQueryChanged, "搜索好友昵称、Riot ID 或备注...")
                    .Margin(new Thickness(0, 0, 10, 0))
                    .AutomationName("好友搜索框")
                    .Grid(row: 0, column: 0),

                ComboBox(FilterLabels, filterIndex, onFilterIndexChanged)
                    .Width(200)
                    .Margin(new Thickness(0, 0, 10, 0))
                    .AutomationName("好友筛选下拉")
                    .Grid(row: 0, column: 1),

                Button(isScanning ? $"扫描中 ({scanCompleted}/{scanTotal})" : "重新扫描好友", onStartScan)
                    .IsEnabled(!isScanning && !isDeleting)
                    .AutomationName("重新扫描好友")
                    .Grid(row: 0, column: 2),
            ]
        ).Margin(new Thickness(0, 6, 0, 10));

        int selectedCount = selectedIds.Count;

        Element actionToolbar;
        if (isConfirmingDelete)
        {
            actionToolbar = Border(
                HStack(12,
                    Caption($"⚠️ 确定要从游戏好友列表中彻底删除选中的 {selectedCount} 位好友吗？此操作无法撤销。")
                        .Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                        .VAlign(VerticalAlignment.Center),
                    Button("确认删除", onConfirmDelete)
                        .Background(Theme.Ref("SystemFillColorCriticalBrush"))
                        .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                        .IsEnabled(!isDeleting)
                        .AutomationName("确认批量删除"),
                    Button("取消", onCancelDelete)
                        .AutomationName("取消批量删除")
                )
            )
            .Padding(12, 8)
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
            .WithBorder(Theme.Ref("SystemFillColorCriticalBrush"), 1)
            .Margin(new Thickness(0, 0, 0, 10));
        }
        else
        {
            actionToolbar = HStack(10,
                Button("全选当前筛选", onSelectAllFiltered)
                    .IsEnabled(!isScanning && filtered.Count > 0)
                    .AutomationName("全选好友"),
                Button("取消选择", onClearSelection)
                    .IsEnabled(selectedCount > 0)
                    .AutomationName("取消选择好友"),
                Button(isDeleting ? "正在删除..." : $"批量清理选中的好友 ({selectedCount})", onPromptDelete)
                    .Background(selectedCount > 0 ? Theme.Ref("SystemFillColorCriticalBrush") : Theme.Ref("SubtleFillColorTransparentBrush"))
                    .Foreground(selectedCount > 0 ? Theme.Ref("TextOnAccentFillColorPrimaryBrush") : Theme.SecondaryText)
                    .IsEnabled(!isScanning && !isDeleting && selectedCount > 0)
                    .AutomationName("批量清理好友"),
                Caption($"当前筛选显示 {filtered.Count} 位好友")
                    .Foreground(Theme.SecondaryText)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(new Thickness(10, 0, 0, 0))
            ).Margin(new Thickness(0, 0, 0, 10));
        }

        Element friendList;
        if (filtered.Count == 0)
        {
            friendList = Border(
                Caption(allFriends.Count == 0 ? "尚未扫描好友，请点击上方「重新扫描好友」开始分析" : "没有符合当前筛选条件的好友")
                    .Foreground(Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .Margin(new Thickness(0, 30, 0, 30))
            );
        }
        else
        {
            var rows = filtered.Select(entry => RenderFriendRow(entry, selectedIds.Contains(entry.Friend.Id), onToggleSelect, palette)).ToArray();
            friendList = Border(
                ScrollViewer(
                    VStack(4, rows)
                ).Height(320)
            )
            .CornerRadius(YuumiTheme.OverlayRadius)
            .Background(Theme.Ref("LayerFillColorDefaultBrush"))
            .WithBorder(Theme.CardStroke, 1)
            .Padding(8);
        }

        return VStack(0,
            filterBar,
            actionToolbar,
            friendList);
    }

    private static Element RenderFriendRow(
        FriendEntry entry,
        bool isSelected,
        Action<string> onToggleSelect,
        YuumiPalette palette)
    {
        bool canSelect = entry.CanDelete;

        var tagColor = entry.TagType switch
        {
            "success" => Theme.Ref("SystemFillColorSuccessBrush"),
            "warning" => Theme.Ref("SystemFillColorCautionBrush"),
            "error" => Theme.Ref("SystemFillColorCriticalBrush"),
            _ => Theme.SecondaryText,
        };

        return Border(
            Grid(
                [GridSize.Auto, GridSize.Star(), GridSize.Auto],
                [GridSize.Auto],
                [
                    CheckBox(isSelected, isChecked =>
                    {
                        if (canSelect && isChecked != isSelected)
                        {
                            onToggleSelect(entry.Friend.Id);
                        }
                    })
                    .IsEnabled(canSelect)
                    .Margin(new Thickness(0, 0, 12, 0))
                    .VAlign(VerticalAlignment.Center)
                    .AutomationName($"选择好友_{entry.DisplayName}")
                    .Grid(row: 0, column: 0),

                    VStack(2,
                        HStack(8,
                            Body(entry.DisplayName),
                            string.IsNullOrWhiteSpace(entry.Friend.Note)
                                ? Empty()
                                : Caption($"({entry.Friend.Note})")
                                    .Foreground(Theme.SecondaryText)
                                    .VAlign(VerticalAlignment.Center)
                        ),
                        Caption(entry.FormattedLastMatchTime)
                            .Foreground(tagColor)
                    ).VAlign(VerticalAlignment.Center).Grid(row: 0, column: 1),

                    Border(
                        Caption(entry.Friend.Availability switch
                        {
                            "chat" => "在线",
                            "away" => "离开",
                            "dnd" => "请勿打扰",
                            _ => "离线",
                        })
                        .Foreground(entry.Friend.Availability == "chat" ? Theme.Ref("SystemFillColorSuccessBrush") : Theme.SecondaryText)
                    )
                    .Padding(6, 2)
                    .CornerRadius(YuumiTheme.ControlRadius)
                    .Background(Theme.Ref("CardBackgroundFillColorDefaultBrush"))
                    .VAlign(VerticalAlignment.Center)
                    .Grid(row: 0, column: 2),
                ]
            ).Padding(new Thickness(8, 6, 8, 6))
        )
        .CornerRadius(YuumiTheme.ControlRadius)
        .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
        .WithKey(entry.Friend.Id);
    }

    public static List<FriendEntry> FilterFriends(
        IReadOnlyList<FriendEntry> all,
        string searchQuery,
        int filterIndex)
    {
        string q = searchQuery.Trim().ToLowerInvariant();
        return all.Where(f =>
        {
            if (!string.IsNullOrEmpty(q))
            {
                string name = f.DisplayName.ToLowerInvariant();
                string note = (f.Friend.Note ?? "").ToLowerInvariant();
                if (!name.Contains(q) && !note.Contains(q)) return false;
            }

            return filterIndex switch
            {
                0 => f.MatchStatus.Status != FriendActivityStatus.Hidden && f.InactiveDays is { } d && d >= 180,
                1 => f.MatchStatus.Status != FriendActivityStatus.Hidden && f.InactiveDays is { } d && d >= 360,
                2 => f.MatchStatus.Status != FriendActivityStatus.Hidden && f.InactiveDays is { } d && d >= 90,
                3 => f.MatchStatus.Status == FriendActivityStatus.None,
                4 => f.MatchStatus.Status == FriendActivityStatus.Hidden,
                5 => true,
                _ => true,
            };
        }).ToList();
    }
}
