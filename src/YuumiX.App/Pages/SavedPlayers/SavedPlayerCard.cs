using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Controls;
using YuumiX.App.Theming;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>单个路人玩家卡片（自 <c>SavedPlayersPage</c> 拆出，R14）：主行 + 安全删除条 + 编辑行 + 相遇对局。</summary>
internal static class SavedPlayerCard
{
    internal static Element Render(
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
                      .Background(Theme.Accent)
                      .CornerRadius(YuumiTheme.ControlRadius)
                    : null,

                !string.IsNullOrEmpty(modeName)
                    ? Border(
                        Caption(modeName)
                            .Foreground(Theme.SecondaryText)
                            .Margin(6, 1)
                      )
                      .Background(Theme.CardBackground)
                      .WithBorder(Theme.CardStroke, 1)
                      .CornerRadius(YuumiTheme.ControlRadius)
                    : null,

                player.EncounterCount >= 2
                    ? Border(
                        Caption($"相遇 {player.EncounterCount} 次")
                            .Foreground(Theme.Accent)
                            .Margin(6, 1)
                      )
                      .Background(Theme.CardBackground)
                      .WithBorder(Theme.Accent, 1)
                      .CornerRadius(YuumiTheme.ControlRadius)
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
            .CornerRadius(YuumiTheme.OverlayRadius)
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
                    .Background(Theme.Accent),
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
        .CornerRadius(YuumiTheme.OverlayRadius);
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
