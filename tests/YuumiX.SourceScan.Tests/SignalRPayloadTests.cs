using Xunit;

namespace YuumiX.SourceScan.Tests;

public sealed class SignalRPayloadTests
{
    [Fact]
    public void ProductionPayloads_HaveSourceGeneratedMetadata()
    {
        var root = FindRepoRoot();
        var files = new[] { "YuumiX.App", "YuumiX.Core" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        var violations = SignalRPayloadScanner.Scan(files.Select(path =>
            (Path.GetRelativePath(root, path), File.ReadAllText(path))));

        Assert.True(violations.Count == 0, "SignalR 载荷 AOT 门禁失败（AGENTS.md §12.7）：\n" +
            string.Join("\n", violations.Select(v => $"{v.File}:{v.Line}: {v.Detail}")));
    }

    [Theory]
    [InlineData("connection.On<JsonElement, string>(\"evt\", (data, id) => { });", false)]
    [InlineData("connection.On(\"evt\", (JsonElement data) => { });", false)]
    [InlineData("connection.On(\"evt\", (Dto data) => { });", true)]
    [InlineData("connection.On<Guid>(\"evt\", value => { });", true)]
    [InlineData("connection.On<decimal>(\"evt\", value => { });", true)]
    [InlineData("connection.On<JsonNode>(\"evt\", value => { });", true)]
    [InlineData("connection.On<Config>(\"evt\", value => { });", false)]
    [InlineData("connection.On<List<string>>(\"evt\", value => { });", false)]
    [InlineData("connection.On<string[]>(\"evt\", value => { });", false)]
    [InlineData("connection.On<System.Text.Json.JsonElement>(\"evt\", value => { });", false)]
    [InlineData("await connection.SendAsync(\"evt\", text, token);", false)]
    [InlineData("await connection.SendAsync(\"evt\", new Config(), token);", false)]
    [InlineData("await connection.SendAsync(\"evt\", new Dto(), token);", true)]
    [InlineData("await connection.SendAsync(\"evt\", value, token);", true)]
    [InlineData("await connection.SendAsync(\"evt\", (object)text, token);", true)]
    [InlineData("await connection.SendAsync(\"evt\", Guid.NewGuid(), token);", true)]
    [InlineData("await connection.SendAsync(\"evt\", null, token);", false)]
    [InlineData("await connection.InvokeAsync<int>(\"evt\", text, token);", false)]
    [InlineData("await connection.InvokeAsync<int>(\"evt\", new Dto(), token);", true)]
    [InlineData("await connection.InvokeAsync<Guid>(\"evt\", token);", true)]
    [InlineData("await connection.StreamAsChannelAsync<Guid>(\"evt\", token);", true)]
    [InlineData("await connection.SendCoreAsync(\"evt\", new object[] { new Dto() }, token);", true)]
    [InlineData("await connection.InvokeCoreAsync(\"evt\", typeof(Dto), Array.Empty<object>(), token);", true)]
    [InlineData("connection.On<\nDto\n>(\"evt\", value => { });", true)]
    [InlineData("await connection\n.SendAsync(\"evt\",\nnew Dto(), token);", true)]
    [InlineData("await Microsoft.AspNetCore.SignalR.Client.HubConnectionExtensions.SendAsync(connection, \"evt\", new Dto(), token);", true)]
    [InlineData("// connection.On<Dto>(\"evt\", value => { });", false)]
    [InlineData("var example = \"connection.On<Dto>()\";", false)]
    [InlineData("await http.SendAsync(new HttpRequestMessage(), token);", false)]
    [InlineData("new Fake().On<Guid>();", false)]
    public void Scanner_ChecksActualPayloads(string call, bool expectViolation)
    {
        var source = """
            using System.Text.Json;
            using System.Text.Json.Nodes;
            using Microsoft.AspNetCore.SignalR.Client;
            using Config = YuumiX.Core.Persistence.AppConfig;
            class Dto { }
            class Fake { public void On<T>() { } }
            class Sample
            {
                async Task Run(HubConnection connection, HttpClient http, string text, object value, CancellationToken token)
                {
            """ + "\n" + call + "\n}}";

        var violations = SignalRPayloadScanner.Scan([("sample.cs", source)]);
        Assert.Equal(expectViolation, violations.Count > 0);
        Assert.All(violations, violation => Assert.Equal("sample.cs", violation.File));
    }

    [Fact]
    public void Scanner_ResolvesReceiverAcrossFilesAndReportsLocation()
    {
        var violations = SignalRPayloadScanner.Scan([
            ("receiver.cs", """
                using Microsoft.AspNetCore.SignalR.Client;
                partial class Sample
                {
                    HubConnection connection;
                }
                """),
            ("calls.cs", """
                using Microsoft.AspNetCore.SignalR.Client;
                partial class Sample
                {
                    async Task Run()
                    {
                        await connection.SendAsync("evt", Guid.NewGuid());
                    }
                }
                """)]);

        var violation = Assert.Single(violations);
        Assert.Equal("calls.cs", violation.File);
        Assert.Equal(6, violation.Line);
        Assert.Contains("System.Guid", violation.Detail);
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "YuumiX.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("未定位到仓库根目录");
    }
}
