using Xunit;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

public sealed class ClientLaunchCoordinatorTests
{
    [Fact]
    public async Task StartLaunch_DeduplicatesConcurrentRequests_AndResetsOnConnect()
    {
        int launchCalls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool connected = false;

        using var coordinator = new ClientLaunchCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref launchCalls);
                entered.TrySetResult();
                return tcs.Task.GetAwaiter().GetResult();
            },
            () => connected);

        var first = coordinator.StartLaunchAsync(["C:\\LoL"], null, TimeSpan.FromSeconds(10));
        await entered.Task;
        var second = coordinator.StartLaunchAsync(["C:\\LoL"], null, TimeSpan.FromSeconds(10));

        Assert.Equal(ClientLaunchPhase.Launching, coordinator.CurrentStatus.Phase);
        await second;
        Assert.Equal(1, launchCalls);

        tcs.SetResult(null);
        await first;
        Assert.Equal(ClientLaunchPhase.WaitingForConnect, coordinator.CurrentStatus.Phase);

        connected = true;
        coordinator.NotifyConnectionStatus(LcuConnectionStatus.Connected);
        Assert.Equal(ClientLaunchPhase.Idle, coordinator.CurrentStatus.Phase);
    }

    [Fact]
    public async Task StartLaunch_FailureRestoresIdleWithError_AllowingRetry()
    {
        int attempt = 0;
        using var coordinator = new ClientLaunchCoordinator(
            (_, _) => ++attempt == 1 ? "未找到客户端" : null,
            () => false);

        await coordinator.StartLaunchAsync(["C:\\LoL"], null, TimeSpan.FromSeconds(10));
        Assert.Equal(ClientLaunchPhase.Idle, coordinator.CurrentStatus.Phase);
        Assert.Equal("未找到客户端", coordinator.CurrentStatus.LastError);
        Assert.Equal(1, coordinator.CurrentStatus.ErrorSequence);

        await coordinator.StartLaunchAsync(["C:\\LoL"], null, TimeSpan.FromSeconds(10));
        Assert.Equal(ClientLaunchPhase.WaitingForConnect, coordinator.CurrentStatus.Phase);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task StartLaunch_TimeoutRestoresIdleWithError()
    {
        using var coordinator = new ClientLaunchCoordinator(
            (_, _) => null,
            () => false);

        await coordinator.StartLaunchAsync(["C:\\LoL"], null, TimeSpan.FromMilliseconds(30));
        Assert.Equal(ClientLaunchPhase.WaitingForConnect, coordinator.CurrentStatus.Phase);

        await Task.Delay(120);
        Assert.Equal(ClientLaunchPhase.Idle, coordinator.CurrentStatus.Phase);
        Assert.NotNull(coordinator.CurrentStatus.LastError);
        Assert.Equal(1, coordinator.CurrentStatus.ErrorSequence);
    }
}
