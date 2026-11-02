using Microsoft.Extensions.Logging;
using Xunit;
using YuumiX.App.Diagnostics;
using YuumiX.Core.Diagnostics;

namespace YuumiX.App.Tests;

public sealed class CrashLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yuumix-crash-tests-" + Guid.NewGuid().ToString("N"));

    public CrashLoggerTests() => Directory.CreateDirectory(_root);

    private string PrimaryDirectory => Path.Combine(_root, "exe");
    private string FallbackDirectory => Path.Combine(_root, "user");
    private string PrimaryLog => Path.Combine(PrimaryDirectory, "crash.log");
    private string FallbackLog => Path.Combine(FallbackDirectory, "crash.log");

    [Fact]
    public void BeforeLoggerInitialization_WritesExceptionAndKeepsUiFailureUnhandled()
    {
        var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory);
        Action failStartup = () => throw new InvalidOperationException("启动失败", new IOException("内部错误"));
        var exception = Assert.Throws<InvalidOperationException>(failStartup);

        Assert.False(logger.OnUiUnhandledException(exception));

        var text = File.ReadAllText(PrimaryLog);
        Assert.Contains("ReactorApplication.OnUnhandledException", text);
        Assert.Contains(exception.ToString(), text);
        Assert.Contains(nameof(BeforeLoggerInitialization_WritesExceptionAndKeepsUiFailureUnhandled), text);
        Assert.False(File.Exists(FallbackLog));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void InitializedLogger_AlsoWritesDailyLogAtInfoOrErrorLevel(int level)
    {
        using (var provider = new RotatingFileLoggerProvider(Path.Combine(PrimaryDirectory, "log")))
        using (var factory = LoggingSetup.CreateFactory(provider))
        {
            provider.MinLevel = LoggingSetup.MapLogLevel(level);
            var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory)
            {
                Logger = factory.CreateLogger("YuumiX.Crash"),
            };

            logger.OnUiUnhandledException(new InvalidOperationException("UI 故障"));
        }

        var dailyLog = Assert.Single(Directory.GetFiles(Path.Combine(PrimaryDirectory, "log"), "yuumi_*.log"));
        var text = File.ReadAllText(dailyLog);
        Assert.Contains("UI 故障", text);
        Assert.Contains("[ERROR]", text);
        Assert.Contains(PrimaryLog, text);
        Assert.Contains("UI 故障", File.ReadAllText(PrimaryLog));
    }

    [Fact]
    public void ProcessAndTaskFailures_AppendWithoutChangingTaskObservation()
    {
        var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory);
        logger.OnUnhandledException(null, new UnhandledExceptionEventArgs(new Exception("进程错误"), true));
        var taskArgs = new UnobservedTaskExceptionEventArgs(new AggregateException(new Exception("任务错误")));

        logger.OnUnobservedTaskException(null, taskArgs);

        var text = File.ReadAllText(PrimaryLog);
        Assert.Contains("AppDomain.UnhandledException", text);
        Assert.Contains("进程错误", text);
        Assert.Contains("TaskScheduler.UnobservedTaskException", text);
        Assert.Contains("任务错误", text);
        Assert.False(taskArgs.Observed);
    }

    [Fact]
    public void UnwritableExecutableDirectory_FallsBackToUserDirectory()
    {
        File.WriteAllText(PrimaryDirectory, "占用目录路径");
        var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory);

        Assert.False(logger.OnUiUnhandledException(new IOException("主目录不可写")));

        Assert.Contains("主目录不可写", File.ReadAllText(FallbackLog));
    }

    [Fact]
    public void BothDirectoriesUnavailable_DoesNotReplaceOriginalFailure()
    {
        File.WriteAllText(PrimaryDirectory, "占用目录路径");
        File.WriteAllText(FallbackDirectory, "占用目录路径");
        var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory);

        Assert.False(logger.OnUiUnhandledException(new Exception("原始异常")));
    }

    [Fact]
    public void BrokenDailyLogger_DoesNotPreventCrashFileOrReplaceOriginalFailure()
    {
        var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory) { Logger = new ThrowingLogger() };

        Assert.False(logger.OnUiUnhandledException(new Exception("原始异常")));

        Assert.Contains("原始异常", File.ReadAllText(PrimaryLog));
    }

    [Fact]
    public void ConcurrentFailures_KeepEveryEntry()
    {
        var logger = new CrashLogger(PrimaryDirectory, FallbackDirectory);

        Parallel.For(0, 16, index => logger.OnUiUnhandledException(new Exception($"并发错误 {index:D2}")));

        var text = File.ReadAllText(PrimaryLog);
        for (var index = 0; index < 16; index++)
        {
            Assert.Contains($"并发错误 {index:D2}", text);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("日志服务不可用");
    }
}
