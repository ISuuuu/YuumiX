using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Opgg.Models;

/// <summary>
/// LCU 符文系（流派）结构（如精密、主宰等）。
/// </summary>
public sealed record PerkStyle
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("iconPath")]
    public string IconPath { get; init; } = "";

    [JsonPropertyName("slots")]
    public IReadOnlyList<PerkSlot> Slots { get; init; } = [];
}

/// <summary>
/// 符文插槽层（每层包含若干候选符文 ID）。
/// </summary>
public sealed record PerkSlot
{
    [JsonPropertyName("perks")]
    public IReadOnlyList<int> Perks { get; init; } = [];
}

/// <summary>
/// 单个符文完整详情（名称、图标、去 HTML 后的纯文本描述）。
/// </summary>
public sealed record PerkDetail(
    int Id,
    string Name,
    string IconPath,
    string ShortDesc,
    string CleanedDesc);
