using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.App.Pages.GameInfo;

/// <summary>
/// 玩家名称解析与跳转文本规范化（对应旧版 <c>usePlayerSearch.ts</c>）。
/// 纯逻辑收敛在 Core 层的 <see cref="PlayerIdentity"/>，本类提供界面侧快捷入口。
/// </summary>
public static class PlayerNameVisuals
{
    public static bool IsPlaceholderName(string? name) => PlayerIdentity.IsPlaceholderName(name);

    public static string GetSearchName(PremadePlayerLike p, GameInfoPlayer? d) => PlayerIdentity.GetSearchName(p, d);

    public static string GetDisplayName(PremadePlayerLike p, GameInfoPlayer? d, int index) => PlayerIdentity.GetDisplayName(p, d, index);
}
