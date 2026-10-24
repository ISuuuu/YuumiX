using Xunit;
using YuumiX.Core.Automation;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Tests;

public sealed class AutoBanPickOpggEventTests
{
    private sealed class MockOpggService : IOpggService
    {
        public List<OpggBuildEvent> PublishedEvents { get; } = [];

        public IObservable<OpggBuildEvent> ChampSelectBuildEvents => throw new NotImplementedException();

        public void PublishChampSelectBuild(OpggBuildEvent e)
        {
            PublishedEvents.Add(e);
        }

        public Task<IReadOnlyList<TierListItem>> GetTierListAsync(string region, string mode, string tier, string? position = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<OpggBuildPayload?> GetBuildAsync(string region, string mode, string tier, int championId, string? position = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> ApplyRunePageAsync(string name, int primaryStyleId, int subStyleId, IReadOnlyList<int> selectedPerkIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> ApplyItemSetAsync(int championId, string champName, string? position, OpggBuildData buildData, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class MockTransport : ILcuTransport
    {
        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LcuResponse(200, "{}"));
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task HandleSessionEventAsync_TriggersOpggBuildEvent_WhenPickCompleted()
    {
        var cfg = new AppConfig();
        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");

        var opggMock = new MockOpggService();
        var transport = new MockTransport();

        var agent = new AutoBanPickAgent(
            () => transport,
            () => cfg,
            supervisor,
            lifecycle,
            opggService: opggMock);

        await agent.StartAsync();

        // 1. 本地玩家未完成 pick
        var sessionIncomplete = new ChampSelectSession
        {
            LocalPlayerCellId = 2,
            QueueId = 420, // 排位
            MyTeam = [new ChampSelectPlayer { CellId = 2, ChampionId = 103, AssignedPosition = "middle" }],
            Actions = [
                [
                    new ChampSelectAction { Id = 1, ActorCellId = 2, ActionType = "pick", Completed = false }
                ]
            ]
        };

        await agent.HandleSessionEventAsync(sessionIncomplete);
        Assert.Empty(opggMock.PublishedEvents);

        // 2. 本地玩家完成 pick
        var sessionComplete = new ChampSelectSession
        {
            LocalPlayerCellId = 2,
            QueueId = 420,
            MyTeam = [new ChampSelectPlayer { CellId = 2, ChampionId = 103, AssignedPosition = "middle" }],
            Actions = [
                [
                    new ChampSelectAction { Id = 1, ActorCellId = 2, ActionType = "pick", Completed = true }
                ]
            ]
        };

        await agent.HandleSessionEventAsync(sessionComplete);
        Assert.Single(opggMock.PublishedEvents);
        var ev = opggMock.PublishedEvents[0];
        Assert.Equal(103, ev.ChampionId);
        Assert.Equal("ranked", ev.Mode);
        Assert.Equal("MID", ev.Position);

        // 3. 同一局重复帧：不重复触发
        await agent.HandleSessionEventAsync(sessionComplete);
        Assert.Single(opggMock.PublishedEvents);
    }
}
