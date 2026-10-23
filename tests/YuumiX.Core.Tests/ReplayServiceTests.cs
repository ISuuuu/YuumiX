using Xunit;
using YuumiX.Core.Features.Replay;
using YuumiX.Core.Lcu;
using YuumiX.Testing.Lcu;

namespace YuumiX.Core.Tests;

public sealed class ReplayServiceTests
{
    [Fact]
    public async Task GetReplayStatusAsync_ReturnsStatusFromLcu()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-replays/v1/metadata/12345")
            {
                return FakeResponse.Ok("""{ "state": "watch" }""");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = new(
            server.BaseAddress,
            server.AuthorizationHeader,
            4,
            TimeSpan.FromSeconds(5),
            null);
        var service = new ReplayService(() => transport);

        string status = await service.GetReplayStatusAsync(12345);

        Assert.Equal("watch", status);
    }

    [Fact]
    public async Task DownloadReplayAsync_TriggersCreateAndDownload()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-replays/v2/metadata/12345/create")
            {
                return FakeResponse.Ok("{}");
            }
            if (request.Path == "/lol-replays/v1/rofls/12345/download")
            {
                return FakeResponse.Ok("{}");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = new(
            server.BaseAddress,
            server.AuthorizationHeader,
            4,
            TimeSpan.FromSeconds(5),
            null);
        var service = new ReplayService(() => transport);

        bool success = await service.DownloadReplayAsync(12345);

        Assert.True(success);
    }

    [Fact]
    public async Task WatchReplayAsync_TriggersWatch()
    {
        await using FakeLcuServer server = FakeLcuServer.Start();
        server.Handler = request =>
        {
            if (request.Path == "/lol-replays/v1/rofls/12345/watch")
            {
                return FakeResponse.Ok("{}");
            }
            return FakeResponse.NotFound();
        };

        using LcuHttpTransport transport = new(
            server.BaseAddress,
            server.AuthorizationHeader,
            4,
            TimeSpan.FromSeconds(5),
            null);
        var service = new ReplayService(() => transport);

        bool success = await service.WatchReplayAsync(12345);

        Assert.True(success);
    }
}
