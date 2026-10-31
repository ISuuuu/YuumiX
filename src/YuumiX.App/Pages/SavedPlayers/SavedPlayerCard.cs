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
        Action onRequestDelete,
        Action onToggle,
        Action onSaveTag,
        YuumiPalette palette)
    {
        var i18n = Composition.AppHost.Services?.I18n;
        var editTagText = i18n?.T("savedPlayersPage.tagEdit", "编辑标记") ?? "编辑标记";
        var deleteText = i18n?.T("savedPlayersPage.delete", "删除") ?? "删除";
        var encounterText = i18n?.T("savedPlayersPage.encounterCount", $"相遇 {player.EncounterCount} 次").Replace("{count}", player.EncounterCount.ToString()) ?? $"相遇 {player.EncounterCount} 次";

        // 1. 头像区（召唤师头像 + 英雄头像并列）
        var avatarRow = HStack(
            6,
            Component<LcuImage, LcuImageProps>(
                new LcuImageProps($"/lol-game-data/assets/v1/profile-icons/{player.ProfileIconId}.jpg", 40, 20)),
            player.ChampionId > 0
                ? // 英雄图标 = Riot 原生方 tile（ControlRadius），与生涯/战绩页一致；仅召唤师头像保留圆形
                  Component<LcuImage, LcuImageProps>(
                    new LcuImageProps($"/lol-game-data/assets/v1/champion-icons/{player.ChampionId}.png", 32, YuumiTheme.ControlRadius))
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
                            .Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush"))
                            .Margin(4, 1)
                      )
                      .Background(Theme.Accent)
                      .CornerRadius(YuumiTheme.ControlRadius)
                    : null,

                !string.IsNullOrEmpty(modeName)
                    ? Border(
                        Caption(modeName)
                            .Foreground(Theme.SecondaryText)
                            .Margin(4, 1)
                      )
                      .Background(Theme.CardBackground)
                      .WithBorder(Theme.CardStroke, 1)
                      .CornerRadius(YuumiTheme.ControlRadius)
                    : null,

                player.EncounterCount >= 2
                    ? Border(
                        Caption(encounterText)
                            .Foreground(Theme.Accent)
                            .Margin(4, 1)
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

            Button(Body(editTagText), () =>
            {
                setEditingTag(player.Tag ?? string.Empty);
                setEditingPuuid(player.Puuid);
            })
            .AutomationName(editTagText)
            .Padding(8, 4),

            Button(Body(deleteText), onRequestDelete)
                .AutomationName(deleteText)
                .Padding(8, 4)
                .Foreground(palette.LoseAccent),

            Button(Body(isExpanded ? "▾" : "▸"), onToggle)
                .AutomationName("展开或折叠对局历史")
                .Padding(8, 4)
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

        // 编辑行
        Element? editRow = null;
        if (isEditing)
        {
            editRow = HStack(
                6,
                TextBox(
                    editingTag,
                    setEditingTag,
                    i18n?.T("savedPlayersPage.tagPlaceholder", "添加标记...") ?? "添加标记..."
                )
                .AutomationName("编辑玩家标记")
                .Width(260)
                .Height(30),
                Button(i18n?.T("savedPlayersPage.tagSave", "保存") ?? "保存", onSaveTag)
                    .AutomationName("保存")
                    .Padding(12, 4)
                    .AccentButton(),
                Button(Body(i18n?.T("savedPlayersPage.tagCancel", "取消") ?? "取消"), () => setEditingPuuid(null))
                    .AutomationName("取消")
                    .Padding(12, 4)
            )
            .Margin(0, 8, 0, 2);
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
                    Body(i18n?.T("savedPlayersPage.loadingMore", "加载相遇对局...") ?? "加载相遇对局...").Foreground(Theme.SecondaryText)
                )
                .Margin(0, 8, 0, 4);
            }
            else if (encounteredGames.Count == 0)
            {
                historySection = Body(i18n?.T("savedPlayersPage.noEncounteredGames", "暂无相遇对局记录") ?? "暂无相遇对局记录")
                    .Foreground(Theme.SecondaryText)
                    .Margin(0, 8, 0, 4);
            }
            else
            {
                var gameRows = encounteredGames.Select(g =>
                    HStack(
                        12,
                        Body(FormatQueue(g.QueueType))
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
                            Button(Body(i18n?.T("savedPlayersPage.loadMore", "加载更多对局记录") ?? "加载更多对局记录"), onLoadMoreGames)
                                .Padding(8, 4)
                                .AutomationName("加载更多对局记录")
                        )
                        .Margin(0, 4, 0, 2)
                    );
                }

                historySection = VStack(
                    4,
                    Body(i18n?.T("savedPlayersPage.encounteredGames", "相遇对局历史：") ?? "相遇对局历史：")
                        .Foreground(Theme.SecondaryText)
                        .Margin(0, 8, 0, 2),
                    VStack(gameRows.ToArray()).Spacing(2)
                )
                .Margin(0, 4, 0, 2);
            }
        }

        return Border(
            VStack(
                4,
                mainRow,
                editRow,
                historySection
            )
            .Padding(12, 8)
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
