using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using YuumiX.Core;
using YuumiX.Core.Assets;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Tests;

/// <summary>
/// 组合根生命周期：关闭令牌必须在 <c>DisposeAsync</c> 时被取消，且 <c>DisposeAsync</c> 幂等。
/// 阶段 3 起所有后台循环都依赖这个信号退出，回归会直接表现为「窗口关了进程还在」。
///
/// 注意：<see cref="AppServices"/> 实现的是 <see cref="IAsyncDisposable"/> 而非 <see cref="IDisposable"/>——
/// 容器内有只实现 IAsyncDisposable 的单例（IAutomationAgent 等），同步关容器会抛 InvalidOperationException。
/// </summary>
public sealed class AppServicesLifecycleTests
{
    [Fact]
    public async Task Create_RegistersAssetPipelineInOrder()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var services = AppServices.Create(RuntimePaths.Detect(exeDir.Path, appData.Path));

        try
        {
            var assets = services.Services.GetRequiredService<AssetService>();

            // 顺序即优先级：LCU 先，CDragon 兜底
            Assert.Equal(["LCU", "CDragon"], assets.SourceNames);
            Assert.NotNull(services.Services.GetRequiredService<LcuConnection>());
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task Create_ProvidesUncancelledShutdownToken()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var services = AppServices.Create(RuntimePaths.Detect(exeDir.Path, appData.Path));

        try
        {
            Assert.False(services.ShutdownToken.IsCancellationRequested);
            Assert.NotNull(services.Config);
            Assert.Equal(AppConfig.CurrentVersion, services.Config.Version);
            Assert.NotNull(services.LoggerFactory.CreateLogger("test"));
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsShutdownTokenAndIsIdempotent()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var services = AppServices.Create(RuntimePaths.Detect(exeDir.Path, appData.Path));
        var token = services.ShutdownToken;

        await services.DisposeAsync();

        Assert.True(token.IsCancellationRequested);

        // 第二次调用不应抛 ObjectDisposedException（退出路径上可能被重复触发）
        await services.DisposeAsync();
    }

    [Fact]
    public async Task Create_WritesLogFileUnderExecutableDirectory()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var services = AppServices.Create(RuntimePaths.Detect(exeDir.Path, appData.Path));

        try
        {
            Assert.True(Directory.Exists(Path.Combine(exeDir.Path, "log")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(exeDir.Path, "log"), "yuumi_*.log"));
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task Create_RegistersLoggerForGenericCategory()
    {
        using var exeDir = new TempDirectory();
        using var appData = new TempDirectory();
        var services = AppServices.Create(RuntimePaths.Detect(exeDir.Path, appData.Path));

        try
        {
            var logger = services.Services.GetService(typeof(ILogger<AppServicesLifecycleTests>));
            Assert.NotNull(logger);
        }
        finally
        {
            await services.DisposeAsync();
        }
    }
}
