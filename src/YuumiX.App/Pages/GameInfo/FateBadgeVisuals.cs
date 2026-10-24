using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 宿命徽标文案与 tooltip 装配（对应旧版 <c>useFateBadge.getFateTitle</c> 与「友/敌/曾」文案细分，§2.2 / §2.5）。
/// </summary>
internal static class FateBadgeVisuals
{
    /// <summary>
    /// 徽标文案：上一局命中（<see cref="GameInfoPlayer.FateIsLastGame"/>，默认 true）时 ally→「友」、enemy→「敌」；
    /// 近期命中时均显示「曾」。
    /// </summary>
    public static string GetBadgeText(GameInfoPlayer d)
    {
        bool isAlly = d.FateFlag == "ally";
        return !d.FateIsLastGame ? "曾" : (isAlly ? "友" : "敌");
    }

    /// <summary>
    /// 完整 tooltip：标题（上一局队友/上一局对手/近期队友/近期对手）
    /// + 使用英雄 + 对局时间（秒/毫秒时间戳归一化）
    /// + 总次数≥2 且队友对手次数均&gt;0 时追加「近期交手 N 次」明细。
    /// </summary>
    public static Element BuildToolTip(GameInfoPlayer d)
    {
        bool isAlly = d.FateFlag == "ally";
        string title = d.FateIsLastGame
            ? (isAlly ? "上一局队友" : "上一局对手")
            : (isAlly ? "近期队友" : "近期对手");

        var lines = new List<Element>
        {
            TextBlock(title).FontSize(12).FontWeight(Microsoft.UI.Text.FontWeights.Bold),
        };

        if (!string.IsNullOrEmpty(d.RecentlyChampionName))
        {
            lines.Add(TextBlock($"使用: {d.RecentlyChampionName}").FontSize(11));
        }

        if (d.FateGameCreation is { } creation && creation > 0)
        {
            lines.Add(TextBlock($"对局时间: {FormatTimestamp(creation)}").FontSize(11));
        }

        int total = d.FateAllyCount + d.FateEnemyCount;
        if (total >= 2 && d.FateAllyCount > 0 && d.FateEnemyCount > 0)
        {
            lines.Add(TextBlock($"近期交手 {total} 次: {d.FateAllyCount} 局队友 · {d.FateEnemyCount} 局对手").FontSize(11));
        }

        return VStack(2, lines.ToArray());
    }

    /// <summary>时间戳格式化为本地时区 <c>yyyy-MM-dd HH:mm</c>；秒级时间戳归一化为毫秒。</summary>
    public static string FormatTimestamp(long value)
    {
        long milliseconds = value < 100_000_000_000 ? value * 1000 : value;
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    // ── 路人集徽标（对应旧版 useFateBadge 的 savedInfo / savedBadgeTitle，§2.2 / §2.5 / §4.8）──

    /// <summary>
    /// 该玩家在路人集中的标记：无 puuid / 不在映射 / 当前登录召唤师自身 → null（自身不显示徽章）。
    /// puuid 兜底顺序与旧版一致：playerData.info.puuid → player.puuid。
    /// </summary>
    public static SavedPlayerMarker? ResolveSavedInfo(
        GameInfoPlayer? d,
        PremadePlayerLike? p,
        IReadOnlyDictionary<string, SavedPlayerMarker>? savedMap,
        string? selfPuuid)
    {
        string? puuid = d?.Info?.Puuid;
        if (string.IsNullOrEmpty(puuid))
        {
            puuid = p?.Puuid;
        }

        if (string.IsNullOrEmpty(puuid) || savedMap is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(selfPuuid) && puuid == selfPuuid)
        {
            return null;
        }

        return savedMap.TryGetValue(puuid, out var marker) ? marker : null;
    }

    /// <summary>徽标显隐：有自定义 tag 显示 tag；无 tag 且玩家无宿命徽标时显示灰「历史」；有宿命徽标时只保留自定义 tag。</summary>
    public static bool ShouldShowSavedBadge(SavedPlayerMarker marker, GameInfoPlayer? d) =>
        !string.IsNullOrEmpty(marker.Tag) || string.IsNullOrEmpty(d?.FateFlag);

    /// <summary>徽标文案：tag 优先，无 tag 时为「历史」。</summary>
    public static string SavedBadgeText(SavedPlayerMarker marker) =>
        string.IsNullOrEmpty(marker.Tag) ? "历史" : marker.Tag;

    /// <summary>
    /// 路人集徽标元素（贴近旧版 saved-badge 样式）：有 tag 主色小圆角胶囊；
    /// 无 tag 灰「历史」。超宽 ellipsis 截断（旧版 max-width 50px）。
    /// </summary>
    public static Element BuildSavedBadge(SavedPlayerMarker marker, double fontSize)
    {
        bool hasTag = !string.IsNullOrEmpty(marker.Tag);
        return Border(
                TextBlock(SavedBadgeText(marker))
                    .FontSize(fontSize)
                    .FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)
                    .Foreground(hasTag ? "#FF3B82F6" : "#FFAAAAAA")
                    .TextTrimming(TextTrimming.CharacterEllipsis))
            .Background(hasTag ? "#263B82F6" : "#1AFFFFFF")
            .WithBorder(hasTag ? "#663B82F6" : "#00000000", hasTag ? 1 : 0)
            .CornerRadius(6)
            .Padding(4, 0.5)
            .MaxWidth(50)
            .VAlign(VerticalAlignment.Center)
            .WithToolTip(BuildSavedToolTip(marker));
    }

    /// <summary>
    /// 路人集徽标 tooltip：「曾同局 N 次 · 标记: tag」（无 tag 省略标记段）
    /// + 有 lastMetAt 时换行追加「最近相遇: yyyy-MM-dd HH:mm」（秒/毫秒时间戳归一化）。
    /// </summary>
    public static Element BuildSavedToolTip(SavedPlayerMarker marker)
    {
        string baseTip = string.IsNullOrEmpty(marker.Tag)
            ? $"曾同局 {marker.EncounterCount} 次"
            : $"曾同局 {marker.EncounterCount} 次 · 标记: {marker.Tag}";

        var lines = new List<Element>
        {
            TextBlock(baseTip).FontSize(12),
        };

        if (marker.LastMetAt is { } lastMet && lastMet > 0)
        {
            lines.Add(TextBlock($"最近相遇: {FormatTimestamp(lastMet)}").FontSize(11));
        }

        return VStack(2, lines.ToArray());
    }
}
