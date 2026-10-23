using System.Text.Json;
using System.Text.Json.Nodes;

namespace YuumiX.Core.Tests;

/// <summary>测试期间使用的临时目录，释放时尽力清理。</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "yuumix-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 残留临时目录不影响测试结论
        }
    }
}

/// <summary>读取随生成输出复制的配置夹具。</summary>
internal static class Fixture
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "config.v3.json");

    public static string ReadText() => File.ReadAllText(Path);

    public static JsonObject ReadObject() => JsonNode.Parse(ReadText())!.AsObject();
}

/// <summary>
/// 以共享方式读取仍被写入方打开的文件。
///
/// 日志 provider 以 <see cref="FileShare.Read"/> 持有句柄（允许别人读），
/// 但默认的 <see cref="File.ReadAllText(string)"/> 只授予 <see cref="FileShare.Read"/>，
/// 与句柄已有的写访问冲突。Windows 的共享校验是双向的，因此读侧也必须声明 ReadWrite。
/// </summary>
internal static class SharedRead
{
    public static string Text(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string[] Lines(string path) => Text(path)
        .Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.Length > 0)
        .ToArray();
}

/// <summary>
/// 逐键比较两个 JSON 树，返回人类可读的差异列表。
///
/// 不用 <see cref="JsonNode.DeepEquals"/> 单独断言，是因为它只回答「是否相等」，
/// 而这里最需要的答案是「到底丢了哪个字段」——验收标准是「不丢失字段」，
/// 失败信息必须直接点名。
/// </summary>
internal static class JsonDiff
{
    public static IReadOnlyList<string> Compare(JsonNode? expected, JsonNode? actual)
    {
        var diffs = new List<string>();
        Walk(expected, actual, "$", diffs);
        return diffs;
    }

    private static void Walk(JsonNode? expected, JsonNode? actual, string path, List<string> diffs)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                diffs.Add($"{path}: 一侧为 null（expected={Describe(expected)}, actual={Describe(actual)}）");
            }

            return;
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var (key, expectedValue) in expectedObject)
            {
                if (actualObject.TryGetPropertyValue(key, out var actualValue))
                {
                    Walk(expectedValue, actualValue, $"{path}.{key}", diffs);
                }
                else
                {
                    diffs.Add($"{path}.{key}: 写回后丢失");
                }
            }

            foreach (var key in actualObject.Select(pair => pair.Key))
            {
                if (!expectedObject.ContainsKey(key))
                {
                    diffs.Add($"{path}.{key}: 写回后新增（原文件无此键）");
                }
            }

            return;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
            {
                diffs.Add($"{path}: 数组长度 {expectedArray.Count} → {actualArray.Count}");
                return;
            }

            for (var i = 0; i < expectedArray.Count; i++)
            {
                Walk(expectedArray[i], actualArray[i], $"{path}[{i}]", diffs);
            }

            return;
        }

        if (!JsonNode.DeepEquals(expected, actual))
        {
            diffs.Add($"{path}: {expected.ToJsonString()} → {actual.ToJsonString()}");
        }
    }

    private static string Describe(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    public static string Format(IReadOnlyList<string> diffs) => diffs.Count == 0
        ? "(无差异)"
        : string.Join(Environment.NewLine, diffs.Select(d => "  " + d));
}
