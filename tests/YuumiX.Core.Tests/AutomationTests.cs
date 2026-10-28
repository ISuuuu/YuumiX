using Xunit;
using System.Text.Json;
using YuumiX.Core.Automation;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task Supervisor_RejectNew_RejectsConcurrentTrigger()
    {
        await using var supervisor = new AgentSupervisor();
        var tcs = new TaskCompletionSource<bool>();

        var taskId1 = await supervisor.SpawnAsync(TaskKind.AutoAccept, "first", async ct =>
        {
            await tcs.Task.WaitAsync(ct);
        });

        Assert.True(taskId1 > 0);

        // 第二次触发同类 RejectNew 任务，必须被拒绝
        await Assert.ThrowsAsync<DuplicateRejectedException>(() =>
            supervisor.SpawnAsync(TaskKind.AutoAccept, "second", async ct =>
            {
                await Task.Delay(100, ct);
            }));

        // 结束第一个任务
        tcs.SetResult(true);
        await Task.Delay(50);

        // 第一个退出后，可以重新启动
        var (spawned, taskId3) = await supervisor.TrySpawnAsync(TaskKind.AutoAccept, "third", async ct =>
        {
            await Task.Yield();
        });

        Assert.True(spawned);
        Assert.True(taskId3 > taskId1);
    }

    [Fact]
    public async Task Supervisor_ReplaceOld_CancelsAndWaitsOldTask()
    {
        await using var supervisor = new AgentSupervisor(TimeSpan.FromMilliseconds(500));
        var firstCancelled = false;
        var firstCleanedUp = false;

        var taskId1 = await supervisor.SpawnAsync(TaskKind.AutoPickLock, "first-pick", async ct =>
        {
            try
            {
                await Task.Delay(10_000, ct);
            }
            catch (OperationCanceledException)
            {
                firstCancelled = true;
                await Task.Delay(50); // 模拟收尾
                firstCleanedUp = true;
            }
        });

        // 第二次触发 ReplaceOld 任务：旧任务应被取消且新任务在旧任务收尾后启动
        var taskId2 = await supervisor.SpawnAsync(TaskKind.AutoPickLock, "second-pick", async ct =>
        {
            Assert.True(firstCancelled);
            Assert.True(firstCleanedUp);
            await Task.Yield();
        });

        Assert.True(taskId2 > taskId1);
        Assert.True(firstCancelled);
        Assert.True(firstCleanedUp);
    }

    [Fact]
    public async Task Supervisor_AllowConcurrent_AllowsMultipleTasks()
    {
        await using var supervisor = new AgentSupervisor();
        var tcs1 = new TaskCompletionSource<bool>();
        var tcs2 = new TaskCompletionSource<bool>();

        var id1 = await supervisor.SpawnAsync(TaskKind.AutoHonor, "honor-1", async ct => await tcs1.Task.WaitAsync(ct));
        var id2 = await supervisor.SpawnAsync(TaskKind.AutoHonor, "honor-2", async ct => await tcs2.Task.WaitAsync(ct));

        Assert.True(id1 > 0);
        Assert.True(id2 > id1);

        var active = supervisor.GetActiveTasks();
        Assert.Equal(2, active.Count);

        tcs1.SetResult(true);
        tcs2.SetResult(true);
    }

    [Fact]
    public void Lifecycle_TransitionsAndDeduplication()
    {
        var lifecycle = new AgentLifecycle();
        var snap0 = lifecycle.Snapshot();

        Assert.Equal(GamePhase.None, snap0.Phase);
        Assert.Equal(1, snap0.MatchEpoch);
        Assert.Equal(1, snap0.PhaseRevision);

        // 重复推送 None，返回 NoChange
        var t0 = lifecycle.UpdatePhase("None");
        Assert.IsType<PhaseTransition.NoChange>(t0);

        // None -> Lobby，同处于非活跃期
        var t1 = lifecycle.UpdatePhase("Lobby");
        var changed1 = Assert.IsType<PhaseTransition.PhaseChanged>(t1);
        Assert.Equal(GamePhase.None, changed1.Previous);
        Assert.Equal(GamePhase.Lobby, changed1.Current);
        Assert.Equal(1, changed1.MatchEpoch);
        Assert.Equal(2, changed1.PhaseRevision);

        // Lobby -> ReadyCheck，开启全新对局
        var t2 = lifecycle.UpdatePhase("ReadyCheck");
        var newMatch = Assert.IsType<PhaseTransition.NewMatchStarted>(t2);
        Assert.Equal(GamePhase.Lobby, newMatch.Previous);
        Assert.Equal(GamePhase.ReadyCheck, newMatch.Current);
        Assert.Equal(2, newMatch.MatchEpoch);
        Assert.Equal(3, newMatch.PhaseRevision);

        // ReadyCheck -> ChampSelect，同一对局内推演
        var t3 = lifecycle.UpdatePhase("ChampSelect");
        var changed2 = Assert.IsType<PhaseTransition.PhaseChanged>(t3);
        Assert.Equal(GamePhase.ReadyCheck, changed2.Previous);
        Assert.Equal(GamePhase.ChampSelect, changed2.Current);
        Assert.Equal(2, changed2.MatchEpoch);
        Assert.Equal(4, changed2.PhaseRevision);

        // 签发 Guard
        var guard = lifecycle.CaptureGuard(requireSession: true);
        Assert.Equal(2, guard.Epoch);
        Assert.Equal(4, guard.Revision);
        Assert.NotNull(guard.RequiredSessionId);

        // 验证 Guard 通过
        guard.EnsureValid(lifecycle.Snapshot());

        // ChampSelect -> InProgress -> Reconnect，同一局内往返保持 Epoch
        lifecycle.UpdatePhase("InProgress");
        var current = lifecycle.Snapshot();
        Assert.Equal(2, current.MatchEpoch);

        lifecycle.UpdatePhase("Reconnect");
        Assert.Equal(2, lifecycle.Snapshot().MatchEpoch);

        // EndOfGame -> None，对局结束
        lifecycle.UpdatePhase("EndOfGame");
        var tTerm = lifecycle.UpdatePhase("None");
        var terminated = Assert.IsType<PhaseTransition.MatchTerminated>(tTerm);
        Assert.Equal(GamePhase.EndOfGame, terminated.Previous);
        Assert.Equal(GamePhase.None, terminated.Current);
        Assert.Equal(2, terminated.FinishedEpoch);

        // 此时此前在 ChampSelect 签发的 guard 必须失效
        Assert.Throws<StaleGenerationException>(() => guard.EnsureValid(lifecycle.Snapshot()));
    }

    [Fact]
    public void ActionLease_VerifyTarget_ValidatesAllInvariants()
    {
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");
        var snap = lifecycle.Snapshot();
        var guard = lifecycle.CaptureGuard(requireSession: true);

        var session = new ChampSelectSession
        {
            LocalPlayerCellId = 2,
            Actions =
            [
                [
                    new ChampSelectAction
                    {
                        Id = 101,
                        ActorCellId = 2,
                        ActionType = "pick",
                        Completed = false,
                        IsInProgress = true,
                        ChampionId = 0,
                    }
                ]
            ]
        };

        var lease = new ActionLease(guard, ActionId: 101, ActorCellId: 2, ActionType: "pick", ExpectedChampionId: 157);

        // 1. 成功验证
        lease.VerifyTarget(snap, session);

        // 2. 席位不一致
        var wrongActorSession = new ChampSelectSession
        {
            LocalPlayerCellId = 3,
            Actions = session.Actions,
        };
        var exActor = Assert.Throws<ActionVerificationException>(() => lease.VerifyTarget(snap, wrongActorSession));
        Assert.Contains("席位不匹配", exActor.Message);

        // 3. Action 已完成
        session.Actions[0][0].Completed = true;
        var exComp = Assert.Throws<ActionVerificationException>(() => lease.VerifyTarget(snap, session));
        Assert.Contains("已完成", exComp.Message);

        // 4. Action 未找到
        session.Actions[0][0].Completed = false;
        session.Actions[0][0].Id = 999;
        var exNotFound = Assert.Throws<ActionVerificationException>(() => lease.VerifyTarget(snap, session));
        Assert.Contains("未找到目标 Action", exNotFound.Message);

        // 5. 代际失效
        lifecycle.UpdatePhase("None");
        var staleSnap = lifecycle.Snapshot();
        session.Actions[0][0].Id = 101;
        var exStale = Assert.Throws<ActionVerificationException>(() => lease.VerifyTarget(staleSnap, session));
        Assert.Contains("生命周期失效", exStale.Message);
    }

    private sealed class MockTransport : ILcuTransport
    {
        public List<LcuRequest> RecordedRequests { get; } = [];
        public Func<LcuRequest, LcuResponse> Handler { get; set; } = _ => new LcuResponse(200, "{}");

        public Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default)
        {
            RecordedRequests.Add(request);
            return Task.FromResult(Handler(request));
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AutoAcceptAgent_Accepts_When_Phase_Is_ReadyCheck()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-matchmaking/v1/ready-check")
                {
                    return new LcuResponse(200, """{"playerResponse":"None"}""");
                }
                return new LcuResponse(200, "{}");
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoAcceptMatching = true;
        config.Functions.AutoAcceptMatchingDelay = 0;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("Lobby");

        await using var agent = new AutoAcceptAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        lifecycle.UpdatePhase("ReadyCheck");
        agent.OnPhaseChanged(GamePhase.ReadyCheck);

        // 等待后台任务执行
        await Task.Delay(100);

        Assert.Contains(transport.RecordedRequests, r => r.Path == "/lol-matchmaking/v1/ready-check/accept" && r.Method == HttpMethod.Post);
    }

    /// <summary>
    /// 竞态回归：ready-check 事件先到、gameflow phase 还停在 Lobby（phase 由另一条事件流驱动，
    /// 到达顺序不保证）。旧实现延迟后拿 phase 做二次校验，会误判「确认已结束」而不接受。
    /// 判定应以 ready-check 接口为准。
    /// </summary>
    [Fact]
    public async Task AutoAcceptAgent_Accepts_When_Phase_Lags_Behind_ReadyCheck()
    {
        var transport = new MockTransport
        {
            Handler = req => req.Path == "/lol-matchmaking/v1/ready-check"
                ? new LcuResponse(200, """{"playerResponse":"None"}""")
                : new LcuResponse(200, "{}")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoAcceptMatching = true;
        config.Functions.AutoAcceptMatchingDelay = 0;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("Lobby");

        await using var agent = new AutoAcceptAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        // 只发 ready-check 事件，不推进 phase
        using var payload = JsonDocument.Parse("""{"playerResponse":"None"}""");
        agent.OnReadyCheckEvent(payload.RootElement);

        Assert.True(await WaitUntilAsync(() =>
            transport.RecordedRequests.Any(r => r.Path == "/lol-matchmaking/v1/ready-check/accept" && r.Method == HttpMethod.Post)));
    }

    /// <summary>确认已随他人拒绝而结束：ready-check 接口 404 时不能再发接受请求。</summary>
    [Fact]
    public async Task AutoAcceptAgent_Skips_When_ReadyCheck_Already_Ended()
    {
        var transport = new MockTransport
        {
            Handler = req => req.Path == "/lol-matchmaking/v1/ready-check"
                ? new LcuResponse(404, "")
                : new LcuResponse(200, "{}")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoAcceptMatching = true;
        config.Functions.AutoAcceptMatchingDelay = 0;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();

        await using var agent = new AutoAcceptAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        using var payload = JsonDocument.Parse("""{"playerResponse":"None"}""");
        agent.OnReadyCheckEvent(payload.RootElement);

        // 先等到查询发出，再确认没有 accept
        Assert.True(await WaitUntilAsync(() =>
            transport.RecordedRequests.Any(r => r.Path == "/lol-matchmaking/v1/ready-check" && r.Method == HttpMethod.Get)));
        await Task.Delay(100);

        Assert.DoesNotContain(transport.RecordedRequests, r => r.Path == "/lol-matchmaking/v1/ready-check/accept");
    }

    /// <summary>
    /// 确认结束后 LCU 下发 JSON null。旧实现直接 TryGetProperty 抛 InvalidOperationException，
    /// 还把「本次已处理」标志卡在 true —— 下一次确认若没有 phase 事件复位就整局不触发。
    /// </summary>
    [Fact]
    public async Task AutoAcceptAgent_ReArms_After_ReadyCheck_Cleared()
    {
        var transport = new MockTransport
        {
            Handler = req => req.Path == "/lol-matchmaking/v1/ready-check"
                ? new LcuResponse(200, """{"playerResponse":"None"}""")
                : new LcuResponse(200, "{}")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoAcceptMatching = true;
        config.Functions.AutoAcceptMatchingDelay = 0;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();

        await using var agent = new AutoAcceptAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        int AcceptCount() => transport.RecordedRequests.Count(r => r.Path == "/lol-matchmaking/v1/ready-check/accept");

        using (var first = JsonDocument.Parse("""{"playerResponse":"None"}"""))
        {
            agent.OnReadyCheckEvent(first.RootElement);
        }

        Assert.True(await WaitUntilAsync(() => AcceptCount() == 1));
        await Task.Delay(50);

        // 确认结束：清空载荷既不能抛异常，也必须把标志复位
        using (var cleared = JsonDocument.Parse("null"))
        {
            agent.OnReadyCheckEvent(cleared.RootElement);
        }

        using (var second = JsonDocument.Parse("""{"playerResponse":"None"}"""))
        {
            agent.OnReadyCheckEvent(second.RootElement);
        }

        Assert.True(await WaitUntilAsync(() => AcceptCount() >= 2));
    }

    [Fact]
    public async Task AutoReconnectAgent_Triggers_On_Reconnect_Phase()
    {
        var transport = new MockTransport
        {
            Handler = req => req.Path switch
            {
                "/lol-gameflow/v1/gameflow-phase" => new LcuResponse(200, "\"Reconnect\""),
                "/lol-gameflow/v1/session" => new LcuResponse(200, """{"phase":"Reconnect"}"""),
                _ => new LcuResponse(200, "{}"),
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoReconnect = true;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("InProgress");

        await using var agent = new AutoReconnectAgent(
            () => transport, () => config, supervisor, lifecycle, gameProcessAliveProbe: () => true);
        await agent.StartAsync();

        agent.OnPhaseChanged(GamePhase.InProgress);

        lifecycle.UpdatePhase("Reconnect");
        agent.OnPhaseChanged(GamePhase.Reconnect);

        Assert.True(await WaitUntilAsync(() =>
            transport.RecordedRequests.Any(r => r.Path == "/lol-gameflow/v1/reconnect" && r.Method == HttpMethod.Post)));
    }

    /// <summary>
    /// 对局结束后 Alt+F4：游戏进程已消失，LCU 仍残留 Reconnect 阶段，此时绝不能再拉起游戏。
    /// </summary>
    [Fact]
    public async Task AutoReconnectAgent_Skips_When_Game_Process_Is_Gone()
    {
        var transport = new MockTransport
        {
            Handler = req => req.Path switch
            {
                "/lol-gameflow/v1/gameflow-phase" => new LcuResponse(200, "\"Reconnect\""),
                "/lol-gameflow/v1/session" => new LcuResponse(200, """{"phase":"Reconnect"}"""),
                _ => new LcuResponse(200, "{}"),
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoReconnect = true;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("InProgress");

        await using var agent = new AutoReconnectAgent(
            () => transport, () => config, supervisor, lifecycle, gameProcessAliveProbe: () => false);
        await agent.StartAsync();

        agent.OnPhaseChanged(GamePhase.InProgress);

        lifecycle.UpdatePhase("Reconnect");
        agent.OnPhaseChanged(GamePhase.Reconnect);

        // 给后台任务足够的执行窗口后仍不得发出任何重连请求
        await Task.Delay(600);

        Assert.DoesNotContain(transport.RecordedRequests, r => r.Path == "/lol-gameflow/v1/reconnect");
    }

    /// <summary>
    /// 游戏进程仍在、但 gameflow session 的权威阶段已经不是 Reconnect（结算已写入）时不重连。
    /// </summary>
    [Fact]
    public async Task AutoReconnectAgent_Skips_When_Session_Phase_Left_Reconnect()
    {
        var transport = new MockTransport
        {
            Handler = req => req.Path switch
            {
                "/lol-gameflow/v1/gameflow-phase" => new LcuResponse(200, "\"Reconnect\""),
                "/lol-gameflow/v1/session" => new LcuResponse(200, """{"phase":"WaitingForStats"}"""),
                _ => new LcuResponse(200, "{}"),
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoReconnect = true;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("InProgress");

        await using var agent = new AutoReconnectAgent(
            () => transport, () => config, supervisor, lifecycle, gameProcessAliveProbe: () => true);
        await agent.StartAsync();

        agent.OnPhaseChanged(GamePhase.InProgress);

        lifecycle.UpdatePhase("Reconnect");
        agent.OnPhaseChanged(GamePhase.Reconnect);

        await Task.Delay(600);

        Assert.DoesNotContain(transport.RecordedRequests, r => r.Path == "/lol-gameflow/v1/reconnect");
    }

    /// <summary>轮询等待后台任务产生可观察结果，避免依赖固定 sleep 时长。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task AutoBanPickAgent_PretendBan_Excludes_Teammate_Intents()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-champ-select/v1/session")
                {
                    return new LcuResponse(200, """
                    {
                      "localPlayerCellId": 1,
                      "actions": [
                        [
                          { "id": 50, "actorCellId": 1, "type": "ban", "isInProgress": true, "completed": false }
                        ]
                      ],
                      "myTeam": [
                        { "cellId": 1, "assignedPosition": "mid", "championPickIntent": 0 },
                        { "cellId": 2, "assignedPosition": "top", "championPickIntent": 157 }
                      ],
                      "bans": { "myTeamBans": [], "theirTeamBans": [] }
                    }
                    """);
                }
                return new LcuResponse(200, "{}");
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoBanChampion = true;
        config.Functions.PretendBan = true;
        config.Functions.AutoBanDelay = 0;
        config.Functions.AutoBanChampion = [157, 238]; // 157 亚索被队友预选，应选择 238 劫

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");

        await using var agent = new AutoBanPickAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        var session = new ChampSelectSession
        {
            LocalPlayerCellId = 1,
            Actions =
            [
                [
                    new ChampSelectAction { Id = 50, ActorCellId = 1, ActionType = "ban", IsInProgress = true, Completed = false }
                ]
            ],
            MyTeam =
            [
                new ChampSelectPlayer { CellId = 1, AssignedPosition = "mid", ChampionPickIntent = 0 },
                new ChampSelectPlayer { CellId = 2, AssignedPosition = "top", ChampionPickIntent = 157 },
            ]
        };

        await agent.HandleSessionEventAsync(session);
        await Task.Delay(100);

        var patchReq = transport.RecordedRequests.FirstOrDefault(r => r.Path == "/lol-champ-select/v1/session/actions/50");
        Assert.NotNull(patchReq);
        Assert.Contains("238", patchReq.JsonBody); // 选了 238
        Assert.DoesNotContain("157", patchReq.JsonBody);
    }

    [Fact]
    public async Task AutoCreateLobbyAgent_CreatesLobby_On_None_Phase()
    {
        var transport = new MockTransport();
        transport.Handler = req =>
        {
            if (req.Path == "/lol-lobby/v2/lobby" && req.Method == HttpMethod.Get)
            {
                // 首次查询无大厅（404），创建后的校验查询返回有效大厅
                return transport.RecordedRequests.Any(r => r.Method == HttpMethod.Post)
                    ? new LcuResponse(200, """{"gameId":123}""")
                    : new LcuResponse(404, "");
            }
            return new LcuResponse(200, "{}");
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoCreateLobby = true;
        config.Functions.DefaultGameMode = 450;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();

        await using var agent = new AutoCreateLobbyAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        agent.OnPhaseChanged(GamePhase.None);
        await Task.Delay(300);

        var createReq = transport.RecordedRequests
            .FirstOrDefault(r => r.Path == "/lol-lobby/v2/lobby" && r.Method == HttpMethod.Post);
        Assert.NotNull(createReq);
        Assert.Contains("450", createReq.JsonBody);
    }

    [Fact]
    public async Task AutoCreateLobbyAgent_Skips_When_Lobby_Already_Exists()
    {
        var transport = new MockTransport
        {
            Handler = req => new LcuResponse(200, """{"gameId":123}""")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoCreateLobby = true;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();

        await using var agent = new AutoCreateLobbyAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        agent.OnPhaseChanged(GamePhase.None);
        await Task.Delay(300);

        Assert.DoesNotContain(transport.RecordedRequests,
            r => r.Path == "/lol-lobby/v2/lobby" && r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AutoCreateLobbyAgent_GivesUp_When_Phase_Leaves_Idle()
    {
        var transport = new MockTransport();

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoCreateLobby = true;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("Lobby"); // 生命周期已离开空闲阶段

        await using var agent = new AutoCreateLobbyAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        agent.OnPhaseChanged(GamePhase.None);
        await Task.Delay(200);

        Assert.Empty(transport.RecordedRequests);
    }

    [Fact]
    public async Task AramTeamSideAgent_Posts_Visible_Chat_Message()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-champ-select/v1/session")
                {
                    return new LcuResponse(200, """
                    {
                      "localPlayerCellId": 0,
                      "chatDetails": { "chatRoomName": "cs-room-hint" }
                    }
                    """);
                }
                if (req.Path == "/lol-champ-select/v1/pin-drop-notification")
                {
                    return new LcuResponse(200, """{"mapSide":"red"}""");
                }
                return new LcuResponse(200,
                    """[{"id":"cs-room-hint-conversation-1","type":"champ-select"}]""");
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoAramTeamSide = true;
        config.Functions.AramTeamSideVisibleToTeam = true;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");

        await using var agent = new AramTeamSideAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        lifecycle.UpdatePhase("ChampSelect");
        agent.OnPhaseChanged(GamePhase.ChampSelect);

        // 等待报边流程完成（包含 2 秒聊天室缓冲）
        await Task.Delay(3000);

        var sendReq = transport.RecordedRequests
            .FirstOrDefault(r => r.Path.StartsWith("/lol-chat/v1/conversations/") && r.Path.EndsWith("/messages"));
        Assert.NotNull(sendReq);
        using var sentDoc = JsonDocument.Parse(sendReq.JsonBody!);
        Assert.Contains("红色方", sentDoc.RootElement.GetProperty("body").GetString());
        Assert.Equal("chat", sentDoc.RootElement.GetProperty("type").GetString()); // 队友可见
    }

    [Fact]
    public async Task AramTeamSideAgent_FallsBack_To_CellId_And_Sends_Celebration()
    {
        var transport = new MockTransport
        {
            Handler = req =>
            {
                if (req.Path == "/lol-champ-select/v1/session")
                {
                    return new LcuResponse(200, """{"localPlayerCellId": 0, "chatDetails": {}}""");
                }
                if (req.Path == "/lol-champ-select/v1/pin-drop-notification")
                {
                    return new LcuResponse(404, ""); // 无 pin 通知，回退按格子位判定
                }
                return new LcuResponse(200, """[{"id":"unifiedchatselect-arena-1","type":"champ-select"}]""");
            }
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoAramTeamSide = true;
        config.Functions.AramTeamSideVisibleToTeam = false;

        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();
        lifecycle.UpdatePhase("ChampSelect");

        await using var agent = new AramTeamSideAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        lifecycle.UpdatePhase("ChampSelect");
        agent.OnPhaseChanged(GamePhase.ChampSelect);

        await Task.Delay(3000);

        var sendReq = transport.RecordedRequests
            .FirstOrDefault(r => r.Path.StartsWith("/lol-chat/v1/conversations/") && r.Path.EndsWith("/messages"));
        Assert.NotNull(sendReq);
        using var sentDoc = JsonDocument.Parse(sendReq.JsonBody!);
        Assert.Contains("蓝色方", sentDoc.RootElement.GetProperty("body").GetString()); // cellId=0 < 5 → 蓝色方
        Assert.Equal("celebration", sentDoc.RootElement.GetProperty("type").GetString()); // 队友不可见
    }

    [Fact]
    public async Task AutoInviteAgent_Accepts_Pending_Invitation()
    {
        var transport = new MockTransport
        {
            Handler = req => new LcuResponse(200, "{}")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoHandleInvite = true;
        var supervisor = new AgentSupervisor();

        await using var agent = new AutoInviteAgent(() => transport, () => config, supervisor);
        await agent.StartAsync();

        using var doc = JsonDocument.Parse("""
        [
            {
                "invitationId": "inv-12345",
                "state": "Pending",
                "canAcceptInvitation": true
            },
            {
                "invitationId": "inv-declined",
                "state": "Declined",
                "canAcceptInvitation": false
            }
        ]
        """);

        agent.OnInvitationsEvent(doc.RootElement);

        await Task.Delay(300);

        var acceptReq = transport.RecordedRequests.FirstOrDefault(r => r.Path == "/lol-lobby/v2/received-invitations/inv-12345/accept");
        Assert.NotNull(acceptReq);
        Assert.Equal(HttpMethod.Post, acceptReq.Method);

        var notAccepted = transport.RecordedRequests.FirstOrDefault(r => r.Path.Contains("inv-declined"));
        Assert.Null(notAccepted);
    }

    [Fact]
    public async Task AutoHonorAgent_Honors_Allies_And_Submits_Ballot()
    {
        var transport = new MockTransport
        {
            Handler = req => new LcuResponse(200, "{}")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoHonor = true;
        var supervisor = new AgentSupervisor();

        await using var agent = new AutoHonorAgent(() => transport, () => config, supervisor);
        await agent.StartAsync();

        using var doc = JsonDocument.Parse("""
        {
            "eligibleAllies": [
                { "puuid": "ally-puuid-1", "botPlayer": false },
                { "puuid": "ally-bot", "botPlayer": true }
            ],
            "votePool": { "votes": 1 }
        }
        """);

        agent.OnBallotEvent(doc.RootElement);

        await Task.Delay(300);

        var honorReq = transport.RecordedRequests.FirstOrDefault(r => r.Path == "/lol-honor-v2/v1/honor-player");
        Assert.NotNull(honorReq);
        using var honorDoc = JsonDocument.Parse(honorReq.JsonBody!);
        Assert.Equal("ally-puuid-1", honorDoc.RootElement.GetProperty("recipientPuuid").GetString());
        Assert.Equal("HEART", honorDoc.RootElement.GetProperty("honorType").GetString());

        var ballotReq = transport.RecordedRequests.FirstOrDefault(r => r.Path == "/lol-honor-v2/v1/ballot");
        Assert.NotNull(ballotReq);
    }

    [Fact]
    public async Task AutoPlayAgainAgent_Cancels_When_Phase_Changes()
    {
        var transport = new MockTransport
        {
            Handler = req => new LcuResponse(200, "{}")
        };

        var config = new Persistence.AppConfig();
        config.Functions.EnableAutoPlayAgain = true;
        var supervisor = new AgentSupervisor();
        var lifecycle = new AgentLifecycle();

        await using var agent = new AutoPlayAgainAgent(() => transport, () => config, supervisor, lifecycle);
        await agent.StartAsync();

        lifecycle.UpdatePhase("WaitingForStats");
        agent.OnPhaseChanged(GamePhase.WaitingForStats);

        // 立即切换到 Lobby（脱离结算阶段），应该触发任务取消
        await Task.Delay(100);
        lifecycle.UpdatePhase("Lobby");
        agent.OnPhaseChanged(GamePhase.Lobby);

        await Task.Delay(300);

        var playAgainReq = transport.RecordedRequests.FirstOrDefault(r => r.Path == "/lol-lobby/v2/play-again");
        Assert.Null(playAgainReq);
    }
}
