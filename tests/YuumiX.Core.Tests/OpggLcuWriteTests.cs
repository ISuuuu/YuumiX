using System.Text.Json.Nodes;
using Xunit;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

public sealed class OpggLcuWriteTests : IDisposable
{
    private sealed class MockTransport : ILcuTransport
    {
        public Func<LcuRequest, LcuResponse> Handler { get; set; } = _ => new LcuResponse(200, "{}");

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Handler(request));
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private readonly string _tempDir;
    private readonly RuntimePaths _paths;

    public OpggLcuWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "YuumiX_OpggLcuWriteTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = RuntimePaths.Detect(executableDirectory: _tempDir, roamingAppData: _tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }

    [Fact]
    public async Task ApplyRunePageAsync_BacksUpDeletablePage_AndRollsBackOnFailure()
    {
        var cfg = new AppConfig();
        using var httpClient = new OpggHttpClient(() => cfg, _paths);

        var calls = new List<string>();
        bool failCreate = true;

        var transport = new MockTransport
        {
            Handler = req =>
            {
                calls.Add($"{req.Method.Method} {req.Path}");

                if (req.Method == HttpMethod.Get && req.Path == "/lol-perks/v1/currentpage")
                {
                    return new LcuResponse(200, """{"id": 1234, "isDeletable": true, "name": "Old Deletable Page"}""");
                }
                if (req.Method == HttpMethod.Get && req.Path == "/lol-perks/v1/pages/1234")
                {
                    return new LcuResponse(200, """{"id": 1234, "name": "Old Deletable Page", "primaryStyleId": 8000, "selectedPerkIds": [8005, 8008], "current": true}""");
                }
                if (req.Method == HttpMethod.Delete && req.Path == "/lol-perks/v1/pages/1234")
                {
                    return new LcuResponse(200, "{}");
                }
                if (req.Method == HttpMethod.Post && req.Path == "/lol-perks/v1/pages")
                {
                    if (failCreate)
                    {
                        failCreate = false; // Next POST will be rollback
                        return new LcuResponse(400, """{"message":"Page limit reached"}""");
                    }
                    return new LcuResponse(200, """{"id": 5678}""");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var service = new OpggService(() => transport, httpClient);

        // 调用应用符文页，预期抛出异常但成功触发回滚
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyRunePageAsync("Yuumi: Ahri", 8100, 8200, [8112, 8126, 8138, 8135, 8226, 8210, 5008, 5008, 5002]));

        Assert.Contains("Page limit reached", ex.Message);

        // 验证执行链路：
        // 1. GET /currentpage
        // 2. GET /pages/1234 (备份)
        // 3. DELETE /pages/1234 (腾位)
        // 4. POST /pages (新建，模拟失败)
        // 5. POST /pages (回滚原页)
        Assert.Equal(5, calls.Count);
        Assert.Equal("GET /lol-perks/v1/currentpage", calls[0]);
        Assert.Equal("GET /lol-perks/v1/pages/1234", calls[1]);
        Assert.Equal("DELETE /lol-perks/v1/pages/1234", calls[2]);
        Assert.Equal("POST /lol-perks/v1/pages", calls[3]);
        Assert.Equal("POST /lol-perks/v1/pages", calls[4]);
    }

    [Fact]
    public async Task ApplyItemSetAsync_ReplacesYuumiSet_KeepsUserSets_AndUsesPut()
    {
        var cfg = new AppConfig();
        using var httpClient = new OpggHttpClient(() => cfg, _paths);

        string? putPath = null;
        string? putBody = null;

        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Method == HttpMethod.Get && req.Path == "/lol-summoner/v1/current-summoner")
                {
                    return new LcuResponse(200, """{"summonerId": 99999, "accountId": 88888}""");
                }
                if (req.Method == HttpMethod.Get && req.Path == "/lol-item-sets/v1/item-sets/99999/sets")
                {
                    return new LcuResponse(200, """
                    {
                        "accountId": 88888,
                        "itemSets": [
                            {
                                "uid": "user-custom-1",
                                "title": "My Custom ADC Set",
                                "blocks": []
                            },
                            {
                                "uid": "old-yuumi-set",
                                "title": "[Yuumi] 亚索 - MID",
                                "blocks": []
                            }
                        ]
                    }
                    """);
                }
                if (req.Method == HttpMethod.Put)
                {
                    putPath = req.Path;
                    putBody = req.JsonBody;
                    return new LcuResponse(200, "{}");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var service = new OpggService(() => transport, httpClient);

        var buildData = new OpggBuildData
        {
            StarterItems = [new OpggIdWinRate { Ids = [1056, 2003], Win = 10, Play = 20 }],
            CoreItems = [new OpggIdWinRate { Ids = [6655, 4645], Win = 10, Play = 20 }],
            Boots = [new OpggIdWinRate { Ids = [3020], Win = 10, Play = 20 }],
            LastItems = [new OpggIdWinRate { Ids = [3157, 6655], Win = 10, Play = 20 }] // 6655 already in core, should be excluded
        };

        var result = await service.ApplyItemSetAsync(103, "阿狸", "MID", buildData);

        Assert.Contains("装备页应用成功", result);
        Assert.Equal("/lol-item-sets/v1/item-sets/99999/sets", putPath);
        Assert.NotNull(putBody);

        var rootNode = JsonNode.Parse(putBody) as JsonObject;
        Assert.NotNull(rootNode);

        var itemSets = rootNode["itemSets"] as JsonArray;
        Assert.NotNull(itemSets);

        // 应该保留了 "My Custom ADC Set"，剔除了旧的 "[Yuumi] 亚索 - MID"，并追加了新的 "[Yuumi] 阿狸 - MID"
        Assert.Equal(2, itemSets.Count);
        Assert.Equal("My Custom ADC Set", itemSets[0]?["title"]?.GetValue<string>());
        Assert.Equal("[Yuumi] 阿狸 - MID", itemSets[1]?["title"]?.GetValue<string>());

        var blocks = itemSets[1]?["blocks"] as JsonArray;
        Assert.NotNull(blocks);
        Assert.Equal(3, blocks.Count);

        // 验证可选装备去重：6655在核心中已有，不应在可选装备池出现
        var lastItems = blocks[2]?["items"] as JsonArray;
        Assert.NotNull(lastItems);
        Assert.Single(lastItems);
        Assert.Equal("3157", lastItems[0]?["id"]?.GetValue<string>());
    }
}
