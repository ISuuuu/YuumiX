using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YuumiX.Core.Automation;
using YuumiX.Core.Common;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Realtime;
using YuumiX.Core.Runtime;
using YuumiX.Core.Updates;
using YuumiX.Core.Upload;

namespace YuumiX.Core.Tests;

/// <summary>
/// Core 层架构改进回归测试：
/// 1. AutomationHost 接口化解耦与事件/断连分派；
/// 2. AppServices 模块化 DI 注册与 ILcuTransportAccessor；
/// 3. AppBackgroundCoordinator 后台协调与取消语义；
/// 4. ProxyHttpClientFactory 代理构造收敛。
/// </summary>
public sealed class CoreArchitectureTests
{
    [Fact]
    public async Task AppServices_Create_RegistersAllModularServicesAndElevenAgents()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var services = AppServices.Create(RuntimePaths.Detect(exeDir.Path, appData.Path));

        try
        {
            var accessor = services.Services.GetRequiredService<ILcuTransportAccessor>();
            var host = services.Services.GetRequiredService<AutomationHost>();
            var coordinator = services.Services.GetRequiredService<AppBackgroundCoordinator>();
            var agents = services.Services.GetServices<IAutomationAgent>().ToList();

            Assert.NotNull(accessor);
            Assert.NotNull(host);
            Assert.NotNull(coordinator);
            Assert.Equal(11, agents.Count);

            // 具体类型与 IAutomationAgent 集合必须指向同一个单例
            var autoAccept = services.Services.GetRequiredService<AutoAcceptAgent>();
            Assert.Contains(agents, a => ReferenceEquals(a, autoAccept));
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    [Fact]
    public void AutomationHost_DispatchesPhaseDisconnectAndLcuEventsByObserverInterface()
    {
        var finder = new LcuProcessFinder(new EmptyProcessSource());
        using var connection = new LcuConnection(finder, 4, null);
        var lifecycle = new AgentLifecycle();

        var phaseAgent = new FakePhaseAgent("PhaseAgent");
        var throwingPhaseAgent = new ThrowingPhaseAgent("ThrowingPhaseAgent");
        var resetAgent = new FakeResetAgent("ResetAgent");
        var lcuEventAgent = new FakeLcuEventAgent("LcuEventAgent", "/lol-custom/v1/test");

        using var host = new AutomationHost(
            connection,
            lifecycle,
            [throwingPhaseAgent, phaseAgent, resetAgent, lcuEventAgent],
            NullLogger<AutomationHost>.Instance);

        var publishedPhases = new List<GamePhase>();
        using var phaseSub = host.Phases.Subscribe(publishedPhases.Add);

        host.Start();
        Assert.True(phaseAgent.IsRunning);
        Assert.True(resetAgent.IsRunning);
        Assert.True(lcuEventAgent.IsRunning);

        // 1. 阶段事件：即使前面的代理抛异常，后续代理与 Phases 流仍正常收到
        using (var phaseDoc = JsonDocument.Parse("\"ChampSelect\""))
        {
            connection.Events.Publish(new LcuEvent(
                "/lol-gameflow/v1/gameflow-phase",
                "Update",
                phaseDoc.RootElement.Clone()));
        }

        Assert.Equal([GamePhase.ChampSelect], phaseAgent.ReceivedPhases);
        Assert.Equal([GamePhase.ChampSelect], resetAgent.ReceivedPhases);
        Assert.Equal([GamePhase.ChampSelect], publishedPhases);
        Assert.Equal(GamePhase.ChampSelect, host.CurrentPhase);

        // 2. 断连事件：实现 IConnectionResetObserver 的只收 OnConnectionReset，其余 IGamePhaseObserver 收 GamePhase.None
        connection.States.Publish(new LcuConnectionState(LcuConnectionStatus.Disconnected, null, null));

        Assert.Equal([GamePhase.ChampSelect, GamePhase.None], phaseAgent.ReceivedPhases);
        Assert.Equal([GamePhase.ChampSelect], resetAgent.ReceivedPhases);
        Assert.Equal(1, resetAgent.ResetCount);
        Assert.Equal(GamePhase.None, host.CurrentPhase);

        // 3. 特定 URI 事件：仅分发给 CanHandle 返回 true 的 ILcuEventObserver
        using (var customDoc = JsonDocument.Parse("{\"ok\":true}"))
        {
            connection.Events.Publish(new LcuEvent(
                "/lol-custom/v1/test",
                "Update",
                customDoc.RootElement.Clone()));
            connection.Events.Publish(new LcuEvent(
                "/lol-other/v1/ignored",
                "Update",
                customDoc.RootElement.Clone()));
        }

        Assert.Single(lcuEventAgent.ReceivedUris);
        Assert.Equal("/lol-custom/v1/test", lcuEventAgent.ReceivedUris[0]);
    }

    [Fact]
    public async Task AppBackgroundCoordinator_GameEnded_InvalidatesCacheImmediatelyAndAfterDelay()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);
        var config = new AppConfig();

        var finder = new LcuProcessFinder(new EmptyProcessSource());
        using var connection = new LcuConnection(finder, 4, null);
        using var lifecycle = new GameLifecycleService(connection);
        var matchService = new FakeMatchService();
        var summonerService = new FakeSummonerService();

        var uploadService = new UploadService(() => null, () => config);
        var pendingStore = new PendingUploadStore(paths);
        using var uploadQueue = new UploadQueueService(
            uploadService,
            pendingStore,
            () => config,
            () => Task.FromResult<string?>("puuid-1"));
        var updateService = new UpdateService(paths, () => config);
        using var signalrService = new SignalrService(() => null, () => config);

        using var cts = new CancellationTokenSource();
        using var coordinator = new AppBackgroundCoordinator(
            lifecycle,
            matchService,
            summonerService,
            uploadQueue,
            updateService,
            signalrService,
            gameEndedUploadDelay: TimeSpan.FromMilliseconds(30),
            startupPendingRetryDelay: TimeSpan.FromMilliseconds(30),
            logger: NullLogger<AppBackgroundCoordinator>.Instance);

        // Start 幂等：重复调用不会重复订阅
        coordinator.Start(cts.Token);
        coordinator.Start(cts.Token);

        lifecycle.LastEndedGameId = 987654321L;
        lifecycle.GameEnded.Publish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // 同步立即清一次缓存
        Assert.Equal(1, matchService.InvalidateCallCount);

        // 等待延迟后的二次清缓存完成
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (matchService.InvalidateCallCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, matchService.InvalidateCallCount);

        cts.Cancel();
    }

    [Fact]
    public async Task AppBackgroundCoordinator_RespectsShutdownTokenCancellation()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var paths = RuntimePaths.Detect(exeDir.Path, appData.Path);
        var config = new AppConfig();

        var finder = new LcuProcessFinder(new EmptyProcessSource());
        using var connection = new LcuConnection(finder, 4, null);
        using var lifecycle = new GameLifecycleService(connection);
        var matchService = new FakeMatchService();
        var summonerService = new FakeSummonerService();

        var uploadService = new UploadService(() => null, () => config);
        var pendingStore = new PendingUploadStore(paths);
        using var uploadQueue = new UploadQueueService(
            uploadService,
            pendingStore,
            () => config,
            () => Task.FromResult<string?>("puuid-1"));
        var updateService = new UpdateService(paths, () => config);
        using var signalrService = new SignalrService(() => null, () => config);

        using var cts = new CancellationTokenSource();
        using var coordinator = new AppBackgroundCoordinator(
            lifecycle,
            matchService,
            summonerService,
            uploadQueue,
            updateService,
            signalrService,
            gameEndedUploadDelay: TimeSpan.FromSeconds(5),
            startupPendingRetryDelay: TimeSpan.FromSeconds(5));

        coordinator.Start(cts.Token);
        cts.Cancel();

        lifecycle.GameEnded.Publish(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await Task.Delay(50);

        // 取消后只执行了同步清缓存，未继续执行后台延迟任务
        Assert.Equal(1, matchService.InvalidateCallCount);
    }

    [Fact]
    public void ProxyHttpClientFactory_NormalizesAndConfiguresHandlerCorrectly()
    {
        Assert.Equal("http://127.0.0.1:7890", ProxyHttpClientFactory.NormalizeProxyUrl("127.0.0.1:7890"));
        Assert.Equal("socks5://127.0.0.1:1080", ProxyHttpClientFactory.NormalizeProxyUrl("  socks5://127.0.0.1:1080  "));

        Assert.True(ProxyHttpClientFactory.TryParseProxyUri("127.0.0.1:7890", out var parsed));
        Assert.Equal(new Uri("http://127.0.0.1:7890"), parsed);
        Assert.False(ProxyHttpClientFactory.TryParseProxyUri("   ", out _));

        using var enabledHandler = ProxyHttpClientFactory.CreateHandler(
            enableProxy: true,
            proxyAddr: "127.0.0.1:7890",
            pooledConnectionLifetime: TimeSpan.FromMinutes(5),
            automaticDecompression: DecompressionMethods.All);
        Assert.True(enabledHandler.UseProxy);
        Assert.NotNull(enabledHandler.Proxy);
        Assert.Equal(TimeSpan.FromMinutes(5), enabledHandler.PooledConnectionLifetime);
        Assert.Equal(DecompressionMethods.All, enabledHandler.AutomaticDecompression);

        using var disabledNoSystemProxy = ProxyHttpClientFactory.CreateHandler(
            enableProxy: false,
            proxyAddr: "127.0.0.1:7890",
            disableSystemProxyWhenDisabled: true);
        Assert.False(disabledNoSystemProxy.UseProxy);
        Assert.Null(disabledNoSystemProxy.Proxy);

        using var disabledKeepDefault = ProxyHttpClientFactory.CreateHandler(
            enableProxy: false,
            proxyAddr: "",
            disableSystemProxyWhenDisabled: false);
        Assert.True(disabledKeepDefault.UseProxy);
        Assert.Null(disabledKeepDefault.Proxy);
    }

    private sealed class EmptyProcessSource : ILcuProcessSource
    {
        public IReadOnlyList<LcuProcessInfo> FindClientProcesses() => [];
        public bool IsProcessAlive(int processId) => false;
    }

    private sealed class FakePhaseAgent(string name) : IAutomationAgent, IGamePhaseObserver
    {
        public string Name { get; } = name;
        public bool IsRunning { get; private set; }
        public List<GamePhase> ReceivedPhases { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void OnPhaseChanged(GamePhase phase) => ReceivedPhases.Add(phase);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingPhaseAgent(string name) : IAutomationAgent, IGamePhaseObserver
    {
        public string Name { get; } = name;
        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void OnPhaseChanged(GamePhase phase) => throw new InvalidOperationException("boom");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeResetAgent(string name) : IAutomationAgent, IPhaseReconcileObserver, IConnectionResetObserver
    {
        public string Name { get; } = name;
        public bool IsRunning { get; private set; }
        public List<GamePhase> ReceivedPhases { get; } = [];
        public int ResetCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public void OnPhaseChanged(GamePhase phase) => ReceivedPhases.Add(phase);
        public void OnConnectionReset() => ResetCount++;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLcuEventAgent(string name, string prefix) : IAutomationAgent, ILcuEventObserver
    {
        public string Name { get; } = name;
        public bool IsRunning { get; private set; }
        public List<string> ReceivedUris { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public bool CanHandle(string uri) => uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        public ValueTask OnLcuEventAsync(LcuEvent evt)
        {
            ReceivedUris.Add(evt.Uri);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMatchService : IMatchService
    {
        private int _invalidateCallCount;
        public int InvalidateCallCount => Volatile.Read(ref _invalidateCallCount);
        public EventStream<MatchServiceStatus> Status { get; } = new();

        public Task<MatchHistoryPage> GetMatchHistoryAsync(
            string puuid,
            int beginIndex,
            int endIndex,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MatchHistoryPage.Empty);

        public Task<MatchDetail?> GetMatchDetailAsync(
            long gameId,
            string? queriedPuuid = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MatchDetail?>(null);

        public void InvalidateCache() { }
        public void InvalidateHistoryCache() => Interlocked.Increment(ref _invalidateCallCount);
    }

    private sealed class FakeSummonerService : ISummonerService
    {
        public EventStream<SummonerProfile?> CurrentChanged { get; } = new();
        public SummonerProfile? CachedCurrent => null;

        public Task<SummonerProfile?> GetCurrentSummonerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SummonerProfile?>(null);

        public Task<SummonerProfile?> FindByNameAsync(string riotId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SummonerProfile?>(null);

        public Task<SummonerProfile?> FindByPuuidAsync(string puuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<SummonerProfile?>(null);

        public Task<SummonerProfile?> FindBySummonerIdAsync(long summonerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SummonerProfile?>(null);

        public void InvalidateCache()
        {
        }
    }
}
