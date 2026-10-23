namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// 连胜/连负信息。
/// </summary>
/// <param name="Type">"win" 或 "loss"</param>
/// <param name="Count">连胜/连负场次（>=2 才会生成）</param>
public sealed record StreakInfo(string Type, int Count)
{
    public bool IsWin => string.Equals(Type, "win", StringComparison.OrdinalIgnoreCase);
}
