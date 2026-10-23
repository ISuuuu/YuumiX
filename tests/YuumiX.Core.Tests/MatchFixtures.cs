using System.Text.Json.Nodes;

namespace YuumiX.Core.Tests;

/// <summary>清单 <c>manifest.json</c> 中的一条夹具记录。</summary>
/// <param name="File">相对清单的路径，如 <c>real/top-solo.json</c>。</param>
/// <param name="IsSynthetic">是否合成用例。</param>
/// <param name="GameId">对局 ID；合成用例的清单项可缺省（合成载荷里的 <c>gameId</c> 是编造的）。</param>
/// <param name="QueueId">队列 ID。</param>
/// <param name="Win">胜负；合成用例可缺省。</param>
/// <param name="LaneRole">清单标注的 <c>lane/role</c> 组合。</param>
/// <param name="Covers">该夹具覆盖的解析要点，失败时用于定位。</param>
internal sealed record MatchFixture(
    string File,
    bool IsSynthetic,
    long? GameId,
    int QueueId,
    bool? Win,
    string? LaneRole,
    string Covers)
{
    /// <summary>夹具文件的绝对路径（生成输出目录下）。</summary>
    public string FullPath => Path.Combine(MatchFixtures.Root, File.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>标签，供测试用例的显示名使用。</summary>
    public string Label => IsSynthetic ? $"synthetic/{Path.GetFileName(File)}" : $"real/{Path.GetFileName(File)}";
}

/// <summary>
/// 战绩夹具的访问入口——三个并行分支（解析器 / 服务 / 界面）共用，避免各自遍历目录。
///
/// 夹具本身来自原项目 <c>E:/Code/Yuumi/src-tauri/testdata/match</c>，原样复制、未做任何改动
/// （ADR 中的"testdata 直接复用"）。<c>manifest.json</c> 是清单的唯一事实来源：
/// 每条记录都写明该夹具覆盖的解析要点，逐字段回归失败时应先读这一列。
/// </summary>
internal static class MatchFixtures
{
    /// <summary>清单声明的真实抓包数量。</summary>
    public const int ExpectedRealCount = 8;

    /// <summary>清单声明的合成用例数量。</summary>
    public const int ExpectedSyntheticCount = 4;

    private static readonly Lazy<IReadOnlyList<MatchFixture>> Cached = new(Load);

    /// <summary>夹具根目录（随生成输出复制）。</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "match");

    /// <summary>清单文件路径。</summary>
    public static string ManifestPath => Path.Combine(Root, "manifest.json");

    /// <summary>全部夹具，真实抓包在前、合成用例在后，顺序与清单一致。</summary>
    public static IReadOnlyList<MatchFixture> All => Cached.Value;

    /// <summary>按文件名（不含目录与扩展名）取一条，找不到即失败——拼错文件名时不要静默跳过。</summary>
    public static MatchFixture ByName(string name)
    {
        var match = All.FirstOrDefault(f => string.Equals(
            Path.GetFileNameWithoutExtension(f.File),
            name,
            StringComparison.OrdinalIgnoreCase));

        return match ?? throw new InvalidOperationException(
            $"夹具 {name} 不在清单内。可用：{string.Join(", ", All.Select(f => Path.GetFileNameWithoutExtension(f.File)))}");
    }

    /// <summary>读取单条夹具的原始文本。</summary>
    public static string ReadText(this MatchFixture fixture) => File.ReadAllText(fixture.FullPath);

    /// <summary>读取单条夹具的 JSON 树。</summary>
    public static JsonNode ReadJson(this MatchFixture fixture)
    {
        var text = fixture.ReadText();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException($"{fixture.File} 解析为 null");
    }

    private static IReadOnlyList<MatchFixture> Load()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))
            ?? throw new InvalidOperationException("manifest.json 解析为 null");

        var result = new List<MatchFixture>(ExpectedRealCount + ExpectedSyntheticCount);
        result.AddRange(ReadSection(manifest, "real", isSynthetic: false));
        result.AddRange(ReadSection(manifest, "synthetic", isSynthetic: true));
        return result;
    }

    private static IEnumerable<MatchFixture> ReadSection(JsonNode manifest, string section, bool isSynthetic)
    {
        var array = manifest[section]?.AsArray()
            ?? throw new InvalidOperationException($"manifest.json 缺少 {section} 数组");

        foreach (var node in array)
        {
            if (node is null)
            {
                throw new InvalidOperationException($"manifest.json 的 {section} 段含 null 项");
            }

            yield return new MatchFixture(
                File: node["file"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"manifest.json 的 {section} 段有缺少 file 的项"),
                IsSynthetic: isSynthetic,
                GameId: node["gameId"]?.GetValue<long>(),
                QueueId: node["queueId"]?.GetValue<int>()
                    ?? throw new InvalidOperationException($"manifest.json 的 {section} 段有缺少 queueId 的项"),
                Win: node["win"]?.GetValue<bool>(),
                LaneRole: node["laneRole"]?.GetValue<string>(),
                Covers: node["covers"]?.GetValue<string>() ?? string.Empty);
        }
    }
}
