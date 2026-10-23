using Xunit;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 游戏资源映射表加载：五个端点、标准/灵活两套解析、整轮重试、缓存语义。
///
/// 全部走假 LCU 服务端，因为这一层的故障几乎都出在「接口返回的结构和预期不一样」，
/// 而那正是要测的东西，mock 掉解析输入就失去意义了。
/// </summary>
public sealed class GameDataLoaderTests
{
    [Fact]
    public async Task LoadAsync_ParsesAllFiveEndpoints()
    {
        await using var server = FakeLcuServer.Start();
        server.Handler = request => request.Path switch
        {
            GameDataLoader.ItemsPath => FakeResponse.Ok("""
                [{"id":1001,"name":"疾行之靴","iconPath":"/lol-game-data/assets/Items/1001.png"},
                 {"id":1002,"iconPath":"/lol-game-data/assets/Items/1002.png"},
                 {"id":1003,"name":"缺少图标"}]
                """),
            GameDataLoader.SpellsPath => FakeResponse.Ok("""[{"id":4,"iconPath":"/spells/4.png"}]"""),
            GameDataLoader.PerksPath => FakeResponse.Ok("""[{"id":8000,"iconPath":"/perks/8000.png"}]"""),
            GameDataLoader.ChampionsPath => FakeResponse.Ok("""
                [{"id":1,"name":"安妮"},{"id":2,"name":""},{"id":3}]
                """),
            GameDataLoader.AugmentsPath => FakeResponse.Ok("""
                [{"id":10,"augmentSmallIconPath":"/aug/10.png","nameTRA":"强化十"},
                 {"id":11,"iconPath":"/aug/11.png","name":"十一"},
                 {"id":12,"name":"缺少图标"}]
                """),
            _ => FakeResponse.NotFound(),
        };

        var loader = NewLoader(server);
        var catalog = await loader.LoadAsync();

        Assert.True(catalog.HasCoreData);

        // 缺 iconPath 的条目必须被丢弃，否则下游会拼出空图片地址
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal("/lol-game-data/assets/Items/1001.png", catalog.Items[1001]);
        Assert.Single(catalog.Spells);
        Assert.Single(catalog.Runes);

        // 名字为空的英雄不入表（上传时会被写成空名）
        Assert.Single(catalog.Champions);
        Assert.Equal("安妮", catalog.Champions[1]);

        // 图标字段有三种历史命名，都要认
        Assert.Equal(2, catalog.Augments.Count);
        Assert.Equal("强化十", catalog.Augments[10].Name);
        Assert.Equal("/aug/11.png", catalog.Augments[11].IconPath);
    }

    /// <summary>
    /// 顶层是对象（按名称分组）时，标准解析拿不到数据，必须由灵活解析接手。
    /// 这是原实现遇到过的真实结构漂移。
    /// </summary>
    [Fact]
    public async Task LoadAsync_FallsBackToFlexibleParserForObjectShape()
    {
        await using var server = FakeLcuServer.Start();
        server.Handler = request => request.Path switch
        {
            GameDataLoader.ItemsPath => FakeResponse.Ok("""
                {
                  "Boots": {"id":1001,"iconPath":"/items/1001.png"},
                  "Group": [{"id":1002,"iconPath":"/items/1002.png"}]
                }
                """),
            GameDataLoader.SpellsPath => FakeResponse.Ok("""[{"id":4,"iconPath":"/spells/4.png"}]"""),
            GameDataLoader.PerksPath => FakeResponse.Ok("""[{"id":8000,"iconPath":"/perks/8000.png"}]"""),
            GameDataLoader.ChampionsPath => FakeResponse.Ok("""[{"id":1,"name":"安妮"}]"""),
            GameDataLoader.AugmentsPath => FakeResponse.Ok("[]"),
            _ => FakeResponse.NotFound(),
        };

        var catalog = await NewLoader(server).LoadAsync();

        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal("/items/1001.png", catalog.Items[1001]);
        Assert.Equal("/items/1002.png", catalog.Items[1002]);
    }

    /// <summary>客户端刚连上时接口可能整体返回空数组，必须重试到有数据为止。</summary>
    [Fact]
    public async Task LoadAsync_RetriesWhenEverythingIsEmpty()
    {
        await using var server = FakeLcuServer.Start();

        var round = 1;
        server.Handler = request => round == 1
            ? FakeResponse.Ok("[]")
            : FakeResponse.Ok(DataFor(request.Path));

        var loader = new GameDataLoader(
            () => NewTransport(server),
            retryDelay: TimeSpan.FromMilliseconds(300));

        var loading = loader.LoadAsync();
        await Task.Delay(100); // 首轮已全部返回空
        round = 2;

        var catalog = await loading;

        Assert.True(catalog.HasCoreData);
        Assert.Single(catalog.Items);
    }

    /// <summary>重试耗尽时返回空表而不是抛异常——调用方拿到的永远是「可以用但可能为空」的表。</summary>
    [Fact]
    public async Task LoadAsync_ReturnsEmptyAfterRetriesExhausted()
    {
        await using var server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");

        var loader = new GameDataLoader(
            () => NewTransport(server),
            retryDelay: TimeSpan.FromMilliseconds(10));

        var catalog = await loader.LoadAsync();

        Assert.False(catalog.HasCoreData);
        Assert.Empty(catalog.Items);
        Assert.True(server.Requests.Count >= 5, "应当至少完整跑过一轮五个接口");
    }

    [Fact]
    public async Task LoadAsync_WithoutTransport_ReturnsEmptyWithoutRequests()
    {
        await using var server = FakeLcuServer.Start();

        var loader = new GameDataLoader(() => null);
        var catalog = await loader.LoadAsync();

        Assert.False(catalog.HasCoreData);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Service_CachesCatalogAcrossCalls()
    {
        await using var server = FakeLcuServer.Start();
        server.Handler = request => FakeResponse.Ok(DataFor(request.Path));

        using var service = new GameDataService(NewLoader(server));

        var first = await service.GetAsync();
        var requestsAfterFirst = server.Requests.Count;
        var second = await service.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(requestsAfterFirst, server.Requests.Count);

        service.Invalidate();

        await service.GetAsync();
        Assert.True(server.Requests.Count > requestsAfterFirst);
    }

    /// <summary>空表不能进缓存：否则客户端刚启动时加载失败一次，整场都要不到图标。</summary>
    [Fact]
    public async Task Service_DoesNotCacheEmptyCatalog()
    {
        await using var server = FakeLcuServer.Start();
        server.Handler = _ => FakeResponse.Ok("[]");

        using var service = new GameDataService(NewLoader(server));

        await service.GetAsync();
        Assert.Null(service.Current);

        var afterFirst = server.Requests.Count;
        await service.GetAsync();

        Assert.True(server.Requests.Count > afterFirst, "空结果不应被缓存，第二次调用必须重新加载");
    }

    private static GameDataLoader NewLoader(FakeLcuServer server) =>
        new(() => NewTransport(server), retryDelay: TimeSpan.FromMilliseconds(10));

    private static LcuHttpTransport NewTransport(FakeLcuServer server)
        => new(server.BaseAddress, server.AuthorizationHeader);

    private static string DataFor(string path) => path switch
    {
        GameDataLoader.ItemsPath => """[{"id":1001,"iconPath":"/items/1001.png"}]""",
        GameDataLoader.SpellsPath => """[{"id":4,"iconPath":"/spells/4.png"}]""",
        GameDataLoader.PerksPath => """[{"id":8000,"iconPath":"/perks/8000.png"}]""",
        GameDataLoader.ChampionsPath => """[{"id":1,"name":"安妮"}]""",
        GameDataLoader.AugmentsPath => """[{"id":10,"augmentSmallIconPath":"/aug/10.png","name":"强化"}]""",
        _ => "{}",
    };
}
