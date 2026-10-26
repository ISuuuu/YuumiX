using Xunit;
using YuumiX.App.Composition;

namespace YuumiX.App.Tests;

public sealed class AppLifetimeTests
{
    [Fact]
    public void RequestExit_SetsIsExitRequestedToTrue()
    {
        AppLifetime.IsExitRequested = false;
        try
        {
            AppLifetime.RequestExit();
            Assert.True(AppLifetime.IsExitRequested);
        }
        finally
        {
            AppLifetime.IsExitRequested = false;
        }
    }

    [Fact]
    public void RegisterAndUnregisterExitAction_CleansUpProperly()
    {
        bool actionInvoked = false;
        Action testAction = () => actionInvoked = true;

        // 无调度器注册直接调用
        AppLifetime.UnregisterExitAction();
        AppLifetime.IsExitRequested = false;

        AppLifetime.RequestExit();
        Assert.False(actionInvoked);
        Assert.True(AppLifetime.IsExitRequested);

        AppLifetime.Shutdown();
        Assert.False(AppLifetime.HasConsoleAttached);
    }
}
