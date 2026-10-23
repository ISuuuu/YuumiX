namespace YuumiX.App.Navigation;

/// <summary>
/// 跨页面搜索跳转载荷，对应原 Vue 的 <c>navigateSearchPayload</c>。
/// 用于从生涯资料卡、最近队友卡片、路人集等页面跳转到战绩查询页并自动发起查询。
/// </summary>
public sealed class SearchNavigationPayload
{
    public static SearchNavigationPayload Instance { get; } = new();

    public string? PendingQuery { get; set; }
    public string? PendingPuuid { get; set; }
    public long? PendingGameId { get; set; }

    public void Set(string? query, string? puuid = null, long? gameId = null)
    {
        PendingQuery = query;
        PendingPuuid = puuid;
        PendingGameId = gameId;
    }

    public (string? Query, string? Puuid, long? GameId) Consume()
    {
        var result = (PendingQuery, PendingPuuid, PendingGameId);
        PendingQuery = null;
        PendingPuuid = null;
        PendingGameId = null;
        return result;
    }
}
