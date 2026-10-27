using System.Text.Json;
using Xunit;
using YuumiX.Core.Common;
using YuumiX.Core.Features.AramBench;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// 大乱斗板凳席悬浮窗的状态机测试。
///
/// 覆盖的四条都是"看代码看不出来、只有对着 LCU 跑才会踩"的行为：
/// 前 15 秒保护期、请求成功但静默不生效、未拥有置灰、跨阶段复位。
/// </summary>
public sealed class AramBenchServiceTests
{
    private sealed class MockTransport : ILcuTransport
    {
        public List<LcuRequest> Requests { get; } = [];

        public Func<LcuRequest, LcuResponse> Handler { get; set; } = _ => new LcuResponse(200, "[]");

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _subscription;

        public Harness(int[]? pickable = null)
        {
            Transport.Handler = request => request.Path switch
            {
                // null = 模拟"选人会话未激活"：LCU 对 pickable-champion-ids 返回 404
                "/lol-champ-select/v1/pickable-champion-ids" => pickable is null
                    ? new LcuResponse(404, string.Empty)
                    : new LcuResponse(200, JsonSerializer.Serialize(pickable)),
                _ => new LcuResponse(200, "{}"),
            };

            Service = new AramBenchService(() => Transport, Events, ConnectionStates);
            _subscription = Service.States.Subscribe(States.Add);
        }

        public EventStream<LcuEvent> Events { get; } = new();

        public EventStream<LcuConnectionState> ConnectionStates { get; } = new();

        public MockTransport Transport { get; } = new();

        public AramBenchService Service { get; }

        public List<AramBenchState> States { get; } = [];

        public void PublishSession(string json) =>
            Events.Publish(new LcuEvent("/lol-champ-select/v1/session", "Update", Parse(json)));

        public void PublishPhase(string phase) =>
            Events.Publish(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonSerializer.SerializeToElement(phase)));

        public void Dispose()
        {
            _subscription.Dispose();
            Service.Dispose();
        }
    }

    /// <summary>
    /// 构造一份选人会话。默认 <c>total=30000 / left=1000</c> 表示"已经过了 29 秒"，
    /// 即保护期之外；测保护期时把 <c>left</c> 调大即可。
    /// </summary>
    private static string Session(
        int myChampionId,
        int[] bench,
        string phase = "BAN_PICK",
        ulong total = 30000,
        ulong left = 1000) =>
        JsonSerializer.Serialize(new
        {
            localPlayerCellId = 0,
            benchEnabled = true,
            benchChampions = bench.Select(id => new { championId = id }).ToArray(),
            myTeam = new[] { new { cellId = 0, championId = myChampionId, championPickIntent = 0 } },
            timer = new { phase, totalTimeInPhase = total, adjustedTimeLeftInPhase = left },
        });

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Session_OutsideProtection_AllBenchChampionsClickable()
    {
        using var harness = new Harness(pickable: [11, 22]);

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22]));

        var state = harness.Service.Current;
        Assert.True(state.IsChampSelect);
        Assert.True(state.BenchEnabled);
        Assert.False(state.IsProtected);
        Assert.Equal([11, 22], state.Champions.Select(c => c.ChampionId));
        Assert.All(state.Champions, champion => Assert.True(champion.Clickable));
    }

    [Fact]
    public void Session_InProtection_TeammateChampionsDisabled()
    {
        using var harness = new Harness(pickable: [11, 22]);

        // total 30000 - left 25000 = 已过 5 秒，仍在 15 秒保护期内；板凳席英雄初始置灰
        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], left: 25000));

        var state = harness.Service.Current;
        Assert.True(state.IsProtected);
        Assert.All(state.Champions, champion => Assert.False(champion.Clickable));
        Assert.Contains("保护期", state.Champions[0].Reason);
    }

    [Fact]
    public void Session_FinalizationPhase_OpensEvenWithinFirst15Seconds()
    {
        using var harness = new Harness(pickable: [11, 22]);

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], phase: "FINALIZATION", left: 25000));

        Assert.False(harness.Service.Current.IsProtected);
        Assert.All(harness.Service.Current.Champions, champion => Assert.True(champion.Clickable));
    }

    [Fact]
    public async Task Swap_ConfirmedBySession_UnlocksPreviouslyOwnedChampionInProtection()
    {
        // 这条覆盖的是"本地保护期判定偏保守"时的兜底：会话确认过的英雄不再置灰。
        // 正常时序下保护期内所有英雄都是灰的（见 Session_InProtection_TeammateChampionsDisabled），
        // 因为保护期内的交换根本拿不到确认——这里用假传输层直接构造"已确认"来验证兜底本身。
        using var harness = new Harness(pickable: [11, 22]);

        // 模拟一次真正生效的交换：POST 返回 2xx 的同时会话把我手持的英雄推成 22
        harness.Transport.Handler = request =>
        {
            if (request.Path.EndsWith("/bench/swap/22", StringComparison.Ordinal))
            {
                harness.PublishSession(Session(myChampionId: 22, bench: [11, 22], left: 24000));
                return new LcuResponse(200, "{}");
            }

            return new LcuResponse(200, "[11,22]");
        };

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], left: 25000));

        var outcome = await harness.Service.SwapAsync(22);

        Assert.Equal(AramBenchSwapOutcome.Swapped, outcome);

        // 22 是刚抢到的，11 是本局换下的：两者都属"我持有过"，保护期内仍可点
        Assert.True(harness.Service.Current.IsProtected);
        Assert.All(harness.Service.Current.Champions, champion => Assert.True(champion.Clickable));
    }

    [Fact]
    public async Task Swap_NotOwned_ReturnsNotOwnedWithoutRequest()
    {
        using var harness = new Harness(pickable: [11]);

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22]));

        var outcome = await harness.Service.SwapAsync(22);

        Assert.Equal(AramBenchSwapOutcome.NotOwned, outcome);
        Assert.DoesNotContain(harness.Transport.Requests, request => request.Path.Contains("bench/swap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Swap_RejectedByLcu_ReturnsProtected()
    {
        using var harness = new Harness(pickable: [11, 22]);
        harness.Transport.Handler = request => request.Path.EndsWith("/bench/swap/22", StringComparison.Ordinal)
            ? new LcuResponse(400, "{\"errorCode\":\"RPC_ERROR\"}")
            : new LcuResponse(200, "[11,22]");

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], left: 25000));

        Assert.Equal(AramBenchSwapOutcome.Protected, await harness.Service.SwapAsync(22));
    }

    [Fact]
    public async Task Swap_SuccessWithoutSessionConfirmation_ReturnsProtected()
    {
        using var harness = new Harness(pickable: [11, 22]);
        harness.Transport.Handler = _ => new LcuResponse(200, "{}");

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], left: 25000));

        // LCU 对队友英雄同样返回 2xx 但静默不生效：必须等会话确认，超时即判为保护期
        Assert.Equal(AramBenchSwapOutcome.Protected, await harness.Service.SwapAsync(22));
    }

    [Fact]
    public async Task Swap_WhenTargetIsCurrentChampion_ReturnsSwappedImmediatelyWithoutRequest()
    {
        using var harness = new Harness(pickable: [11, 22]);
        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22]));

        var outcome = await harness.Service.SwapAsync(11);

        Assert.Equal(AramBenchSwapOutcome.Swapped, outcome);
        Assert.DoesNotContain(harness.Transport.Requests, request => request.Path.Contains("bench/swap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Swap_LcuReturns500_ConfirmedBySessionWithinGracePeriod_ReturnsSwapped()
    {
        using var harness = new Harness(pickable: [11, 22]);
        harness.Transport.Handler = request =>
        {
            if (request.Path.EndsWith("/bench/swap/22", StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    harness.PublishSession(Session(myChampionId: 22, bench: [11, 22], left: 24000));
                });
                return new LcuResponse(500, "{\"errorCode\":\"INTERNAL_SERVER_ERROR\"}");
            }

            return new LcuResponse(200, "[11,22]");
        };

        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], left: 25000));

        var outcome = await harness.Service.SwapAsync(22);

        Assert.Equal(AramBenchSwapOutcome.Swapped, outcome);
        Assert.True(harness.Service.Current.IsProtected);
        Assert.All(harness.Service.Current.Champions, champion => Assert.True(champion.Clickable));
    }

    [Fact]
    public void Session_InGameSwapOrRoll_TracksPreviousChampionAsUnlocked()
    {
        using var harness = new Harness(pickable: [11, 22, 33]);
        harness.PublishSession(Session(myChampionId: 11, bench: [22, 33], left: 25000));
        Assert.All(harness.Service.Current.Champions, champion => Assert.False(champion.Clickable));

        // 玩家在游戏客户端内 roll 或与队友换，当前手持变为 22，换下的 11 扔到板凳席
        harness.PublishSession(Session(myChampionId: 22, bench: [11, 33], left: 24000));

        // 11 是此前手持过的专属英雄，保护期内依然解锁可点
        var champion11 = Assert.Single(harness.Service.Current.Champions, c => c.ChampionId == 11);
        Assert.True(champion11.Clickable);

        // 33 从未持有过，仍在保护期置灰
        var champion33 = Assert.Single(harness.Service.Current.Champions, c => c.ChampionId == 33);
        Assert.False(champion33.Clickable);
    }

    [Fact]
    public async Task Session_InProtection_TimerExpires_PublishesUnprotectedState()
    {
        using var harness = new Harness(pickable: [11, 22]);
        // total 30000, 15 秒保护期在 left <= 15000 结束。left = 15050ms 代表还有 50ms 到期
        harness.PublishSession(Session(myChampionId: 11, bench: [11, 22], total: 30000, left: 15050));
        Assert.True(harness.Service.Current.IsProtected);

        // 等待定时器触发
        await Task.Delay(150);

        Assert.False(harness.Service.Current.IsProtected);
        Assert.All(harness.Service.Current.Champions, champion => Assert.True(champion.Clickable));
    }

    [Fact]
    public void Phase_LeavingChampSelect_ResetsToIdle()
    {
        using var harness = new Harness(pickable: [11]);

        harness.PublishSession(Session(myChampionId: 11, bench: [11]));
        Assert.True(harness.Service.Current.IsChampSelect);

        harness.PublishPhase("InProgress");

        Assert.False(harness.Service.Current.IsChampSelect);
        Assert.False(harness.Service.Current.BenchEnabled);
        Assert.Empty(harness.Service.Current.Champions);
    }

    [Fact]
    public void Session_PickleableIdsUnavailable_DoesNotDisableChampions()
    {
        // pickable: null → 404。拉不到清单时不能把全部英雄误判成"不可用"
        using var harness = new Harness(pickable: null);

        harness.PublishSession(Session(myChampionId: 0, bench: [11, 22]));

        Assert.All(harness.Service.Current.Champions, champion => Assert.True(champion.Clickable));
    }

    [Fact]
    public void Session_ConnectionLost_ResetsToIdle()
    {
        using var harness = new Harness(pickable: [11]);

        harness.PublishSession(Session(myChampionId: 11, bench: [11]));
        harness.ConnectionStates.Publish(LcuConnectionState.Disconnected("客户端已退出"));

        Assert.False(harness.Service.Current.IsChampSelect);
        Assert.Empty(harness.Service.Current.Champions);
    }
}
