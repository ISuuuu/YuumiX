namespace YuumiX.Core.Lcu;

/// <summary>
/// LCU 接口路径白名单。
///
/// 原实现（<c>client.rs</c> 的 <c>ALLOWED_API_PREFIXES</c>）以此防止前端传入任意路径，
/// 把 LCU 客户端当作通用 HTTP 代理使用。单进程后虽然调用方只剩本进程代码，
/// 但白名单仍有价值：它是一份「本项目到底依赖哪些 LCU 接口」的可审计清单，
/// 将来排查版本兼容问题时比翻代码快。
/// </summary>
public static class LcuApiCatalog
{
    private static readonly string[] AllowedPrefixes =
    [
        "/lol-gameflow/",
        "/lol-champ-select/",
        "/lol-matchmaking/",
        "/lol-summoner/",
        "/lol-chat/",
        "/lol-game-data/",
        "/lol-lobby/",
        "/lol-perks/",
        "/lol-ranked/",
        "/lol-match-history/",
        "/lol-champion-mastery/",
        "/lol-challenges/",
        "/lol-honor-v2/",
        "/lol-honor/",
        "/lol-pre-end-of-game/",
        "/lol-spectator/",
        "/lol-patch/",
        "/lol-item-sets/",
        "/lol-login/",
        "/lol-replays/",
        "/lol-inventory/",
        "/lol-champions/",
        "/riotclient/",
        "/system/",
    ];

    /// <summary>健康检查用接口：端口开放不代表 API 已就绪，探测器以此判定。</summary>
    public const string ReadinessPath = "/system/v1/builds";

    /// <summary>白名单内的路径前缀（只读视图）。</summary>
    public static IReadOnlyList<string> Prefixes => AllowedPrefixes;

    /// <summary>
    /// 判断路径是否允许访问。必须是绝对路径且命中白名单前缀。
    /// 刻意不做大小写不敏感匹配：LCU 路径本身区分大小写，放宽只会掩盖拼写错误。
    /// </summary>
    public static bool IsAllowed(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        foreach (var prefix in AllowedPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
