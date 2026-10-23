using System.Text.Json;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.App.Pages.Search;

/// <summary>
/// 战绩页的演示数据源：直接读随程序分发的夹具（原项目 <c>testdata/match</c> 的 12 份样本）。
///
/// 为什么界面阶段读夹具而不是接 <c>IMatchService</c>：
/// <list type="bullet">
///   <item>视觉调整需要反复看同一批数据，真实接口每刷新一次内容就变一遍，没法比对；</item>
///   <item>夹具覆盖了重开、大乱斗、斗魂、自定义、稀疏载荷这几种极端形态，
///         正好用来确认布局在"最难看的数据"下不塌。</item>
/// </list>
///
/// <b>接真实数据的改动点只有两个</b>：把 <see cref="All"/> 换成
/// <c>IMatchService.GetMatchHistoryAsync</c>（异步 + 状态流），把 <see cref="LoadDetail"/> 换成
/// <c>GetMatchDetailAsync</c>。组件与布局不需要动——它们只认契约模型。
///
/// 与测试项目里的 <c>MatchFixtures</c> 的关系：那里读的是测试输出目录，这里读的是 App 输出目录，
/// 两份文件内容相同（同一批原始夹具），各自复制一次是为了避免生产程序引用测试项目。
/// </summary>
internal static class SearchFixtures
{
    private const string FolderName = "Fixtures";

    private static readonly Lazy<State> Cached = new(Load);
    private static readonly Dictionary<long, MatchDetail?> DetailCache = [];
    private static readonly object DetailGate = new();

    /// <summary>全部对局，按开局时间从新到旧。</summary>
    public static IReadOnlyList<MatchSummary> All => Cached.Value.Summaries;

    /// <summary>数据来源说明，显示在页面头部，避免被误当成真实战绩。</summary>
    public static string SourceNote => $"本地夹具 · {All.Count} 场 · 覆盖重开/大乱斗/斗魂/自定义/稀疏载荷";

    /// <summary>
    /// 取单局详情。
    /// </summary>
    /// <remarks>
    /// 夹具来自 <c>matches</c> 列表接口，每局只含本人 1 名参与者，
    /// 因此详情面板里对手阵营是空的——这不是解析问题，是样本本身的形状。
    /// </remarks>
    public static MatchDetail? LoadDetail(long gameId)
    {
        // 详情在页面上会随每次重渲染被请求（选中变化、主题变化都会触发），
        // 每次都读盘并解析一遍没有必要，按 gameId 缓存解析结果。
        lock (DetailGate)
        {
            if (DetailCache.TryGetValue(gameId, out MatchDetail? cached))
            {
                return cached;
            }
        }

        MatchDetail? detail = ParseDetailFile(gameId);

        lock (DetailGate)
        {
            DetailCache[gameId] = detail;
        }

        return detail;
    }

    private static MatchDetail? ParseDetailFile(long gameId)
    {
        if (!Cached.Value.Files.TryGetValue(gameId, out string? path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return MatchParser.ParseDetail(FirstGame(document.RootElement));
        }
        catch (Exception)
        {
            // 一份夹具读坏了不该让整页崩掉，详情区显示"无数据"即可
            return null;
        }
    }

    private static State Load()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Pages", "Search", FolderName);
        var summaries = new List<MatchSummary>();
        var files = new Dictionary<long, string>();

        if (!Directory.Exists(directory))
        {
            return new State(summaries, files);
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

                // 每份夹具约定只含一局；仍是列表外层形状，因此走 ParseHistory
                MatchSummary? summary = MatchParser.ParseHistory(document.RootElement).FirstOrDefault();
                if (summary is null)
                {
                    continue;
                }

                summaries.Add(summary);
                files[summary.GameId] = path;
            }
            catch (Exception)
            {
                // 跳过坏文件：演示数据缺一条不影响页面可用性
            }
        }

        return new State([.. summaries.OrderByDescending(summary => summary.GameCreation)], files);
    }

    private static JsonElement FirstGame(JsonElement response) =>
        response.GetProperty("games").GetProperty("games")[0];

    private sealed record State(IReadOnlyList<MatchSummary> Summaries, IReadOnlyDictionary<long, string> Files);
}
