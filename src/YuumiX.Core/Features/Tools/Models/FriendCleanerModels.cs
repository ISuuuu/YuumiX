using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Tools.Models;

/// <summary>
/// LCU 好友基础数据（对应 /lol-chat/v1/friends）。
/// </summary>
public sealed class LcuFriend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("puuid")]
    public string Puuid { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("gameName")]
    public string? GameName { get; set; }

    [JsonPropertyName("gameTag")]
    public string? GameTag { get; set; }

    [JsonPropertyName("icon")]
    public int Icon { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("group")]
    public string? GroupName { get; set; }

    [JsonPropertyName("availability")]
    public string Availability { get; set; } = "offline";
}

/// <summary>
/// 好友对局活跃状态类型。
/// </summary>
public enum FriendActivityStatus
{
    /// <summary>正常查到对局记录</summary>
    Ok,
    /// <summary>当前在线或游戏中</summary>
    Online,
    /// <summary>通过熟练度穿透获取</summary>
    Mastery,
    /// <summary>战绩已隐藏/隐私保护（严禁纳入批量删除）</summary>
    Hidden,
    /// <summary>从未进行过任何对局 (0 场战绩)</summary>
    None,
}

/// <summary>
/// 好友战绩活跃检测快照。
/// </summary>
public sealed record FriendMatchStatus(long? LastGameTime, FriendActivityStatus Status);

/// <summary>
/// 筛选分类。
/// </summary>
public enum FriendFilterType
{
    Days180,
    Days360,
    Days90,
    Never,
    Hidden,
    All,
}

/// <summary>
/// 综合好友条目模型（用于展示与批量操作）。
/// </summary>
public sealed class FriendEntry
{
    public required LcuFriend Friend { get; init; }
    public required FriendMatchStatus MatchStatus { get; init; }
    public int? InactiveDays { get; init; }

    public bool CanDelete => MatchStatus.Status != FriendActivityStatus.Hidden;

    public string DisplayName => !string.IsNullOrWhiteSpace(Friend.GameName)
        ? (!string.IsNullOrWhiteSpace(Friend.GameTag) ? $"{Friend.GameName}#{Friend.GameTag}" : Friend.GameName)
        : (!string.IsNullOrWhiteSpace(Friend.Name) ? Friend.Name : "未知召唤师");

    public string FormattedLastMatchTime
    {
        get
        {
            if (MatchStatus.Status == FriendActivityStatus.Online) return "当前在线/游戏中";
            if (MatchStatus.Status == FriendActivityStatus.Hidden) return "🔒 战绩已隐藏";
            if (MatchStatus.Status == FriendActivityStatus.None) return "从未对局 (0场战绩)";

            if (InactiveDays is null) return "无对局记录";
            if (InactiveDays == 0) return "今天曾对局";
            if (InactiveDays < 30) return $"{InactiveDays} 天前对局";
            if (InactiveDays < 365) return $"{InactiveDays / 30} 个月前 ({InactiveDays}天)";
            return $"{(InactiveDays.Value / 365.0):F1} 年前 ({InactiveDays}天)";
        }
    }

    public string TagType => MatchStatus.Status switch
    {
        FriendActivityStatus.Online => "success",
        FriendActivityStatus.Hidden => "warning",
        FriendActivityStatus.None => "error",
        _ when InactiveDays is null => "default",
        _ when InactiveDays >= 180 => "error",
        _ when InactiveDays >= 90 => "warning",
        _ when InactiveDays < 30 => "success",
        _ => "default",
    };
}
