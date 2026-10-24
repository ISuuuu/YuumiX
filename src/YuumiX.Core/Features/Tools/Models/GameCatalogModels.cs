namespace YuumiX.Core.Features.Tools.Models;

/// <summary>
/// 英雄摘要条目（供英雄选择器使用）。
/// </summary>
public sealed record ChampionSummaryItem(
    int Id,
    string Name,
    string Alias,
    string Title,
    string IconPath);

/// <summary>
/// 召唤师技能条目（供技能选择器使用）。
/// </summary>
public sealed record SummonerSpellItem(
    int Id,
    string Name,
    string Description,
    string IconPath);
