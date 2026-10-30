using Xunit;
using YuumiX.Core.Automation;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

public sealed class FriendAutomationRegressionTests
{
    private sealed class Transport(Func<LcuRequest, CancellationToken, Task<LcuResponse>> send) : ILcuTransport
    {
        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default) => send(request, cancellationToken);
        public void Dispose() { }
    }

    [Theory]
    [InlineData(200, "{\"games\":{\"games\":[]}}", 200, "[]", FriendActivityStatus.None)]
    [InlineData(503, "", 503, "", FriendActivityStatus.Hidden)]
    [InlineData(200, "{\"games\":{\"games\":[]}}", 503, "", FriendActivityStatus.Hidden)]
    [InlineData(403, "FORBIDDEN", 200, "[]", FriendActivityStatus.Hidden)]
    [InlineData(200, "{}", 200, "[]", FriendActivityStatus.Hidden)]
    public async Task FriendScan_OnlyReliableEmptyResultsAreDeletable(int matchCode, string matchBody, int masteryCode, string masteryBody, FriendActivityStatus expected)
    {
        var transport = new Transport((request, _) => Task.FromResult(request.Path switch
        {
            "/lol-chat/v1/friends" => new LcuResponse(200, """[{"id":"1","puuid":"p","availability":"offline"}]"""),
            var path when path.Contains("match-history") => new LcuResponse(matchCode, matchBody),
            _ => new LcuResponse(masteryCode, masteryBody),
        }));
        var entry = Assert.Single(await new FriendCleanerService(() => transport).ScanFriendsAsync());
        Assert.Equal(expected, entry.MatchStatus.Status);
        Assert.Equal(expected == FriendActivityStatus.None, entry.CanDelete);
    }

    [Fact]
    public async Task FriendScan_ExceptionsProtectAndCancellationPropagates()
    {
        var transport = new Transport((request, ct) => request.Path == "/lol-chat/v1/friends"
            ? Task.FromResult(new LcuResponse(200, """[{"id":"1","puuid":"p","availability":"offline"}]"""))
            : ct.IsCancellationRequested ? Task.FromCanceled<LcuResponse>(ct) : Task.FromException<LcuResponse>(new HttpRequestException()));
        var cleaner = new FriendCleanerService(() => transport);
        Assert.False(Assert.Single(await cleaner.ScanFriendsAsync()).CanDelete);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleaner.ScanFriendsAsync(cancellationToken: cts.Token));
    }

    private static ChampSelectSession Session() => new()
    {
        LocalPlayerCellId = 1,
        MyTeam = [new ChampSelectPlayer { CellId = 1, AssignedPosition = "middle" }],
        Actions = [[new ChampSelectAction { Id = 7, ActorCellId = 1, ActionType = "pick", IsInProgress = true }]],
    };

    private static AppConfig Config(bool preset)
    {
        var config = new AppConfig();
        config.Functions.EnableAutoHoverChampion = preset;
        config.Functions.AutoSelectChampion = [157];
        config.Functions.EnableAutoSetSpells = !preset;
        config.Functions.AutoSetSummonerSpell = [4, 14];
        return config;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task AutomaticWrites_RetryFailureAndDeduplicateSuccess(bool preset, bool throws)
    {
        int count = 0;
        var transport = new Transport((_, _) =>
        {
            int n = Interlocked.Increment(ref count);
            if (n == 1 && throws) return Task.FromException<LcuResponse>(new HttpRequestException());
            return Task.FromResult(new LcuResponse(n == 1 ? 500 : 200, "{}"));
        });
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");
        var config = Config(preset);
        await using var agent = new AutoBanPickAgent(() => transport, () => config, new AgentSupervisor(), lifecycle);
        await agent.StartAsync();
        await agent.HandleSessionEventAsync(Session());
        await agent.HandleSessionEventAsync(Session());
        await agent.HandleSessionEventAsync(Session());
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticWrites_DeduplicateInFlightAndIgnoreOldSessionCompletion(bool preset)
    {
        int count = 0;
        var oldResponse = new TaskCompletionSource<LcuResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport((_, _) =>
        {
            int n = Interlocked.Increment(ref count);
            return n == 1 ? oldResponse.Task : Task.FromResult(new LcuResponse(n == 2 ? 500 : 200, "{}"));
        });
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");
        var config = Config(preset);
        await using var agent = new AutoBanPickAgent(() => transport, () => config, new AgentSupervisor(), lifecycle);
        await agent.StartAsync();
        var oldEvent = agent.HandleSessionEventAsync(Session());
        await agent.HandleSessionEventAsync(Session());
        Assert.Equal(1, count);
        await agent.HandleSessionEventAsync(new ChampSelectSession { LocalPlayerCellId = -1 });
        await agent.HandleSessionEventAsync(Session());
        Assert.Equal(2, count);
        oldResponse.SetResult(new LcuResponse(200, "{}"));
        await oldEvent;
        await agent.HandleSessionEventAsync(Session());
        await agent.HandleSessionEventAsync(Session());
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Preset_DoesNotOverrideManualSelection()
    {
        int count = 0;
        var transport = new Transport((_, _) => { count++; return Task.FromResult(new LcuResponse(200, "{}")); });
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");
        var config = Config(true);
        await using var agent = new AutoBanPickAgent(() => transport, () => config, new AgentSupervisor(), lifecycle);
        await agent.StartAsync();
        var session = Session();
        session.MyTeam[0].ChampionPickIntent = 22;
        await agent.HandleSessionEventAsync(session);
        Assert.Equal(0, count);
    }
}
