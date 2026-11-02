using Microsoft.Extensions.Logging;

namespace YuumiX.App.Diagnostics;

/// <summary>独立于服务容器的异常记录，覆盖日志初始化之前与容器释放之后的故障。</summary>
internal sealed class CrashLogger(string executableDirectory, string fallbackDirectory)
{
    private readonly object _gate = new();

    internal ILogger? Logger { get; set; }

    internal bool OnUiUnhandledException(Exception exception)
    {
        Write(exception, "ReactorApplication.OnUnhandledException");
        return false;
    }

    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
        => Write(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()),
            "AppDomain.UnhandledException");

    internal void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        // 此事件在任务被回收时触发，只作兜底记录，不改变运行时原有的异常处理策略。
        Write(args.Exception, "TaskScheduler.UnobservedTaskException");
    }

    private void Write(Exception exception, string source)
    {
        lock (_gate)
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}{exception}{Environment.NewLine}";
            var directory = TryAppend(executableDirectory, entry) ? executableDirectory
                : TryAppend(fallbackDirectory, entry) ? fallbackDirectory : null;

            try
            {
                Logger?.LogError(exception, "YuumiX 未处理异常（来源：{Source}，崩溃日志：{Path}）",
                    source, directory is null ? "写入失败" : Path.Combine(directory, "crash.log"));
            }
            catch (Exception)
            {
                // 已独立尝试写入 crash.log；容器释放或日志设施故障不能覆盖原始异常。
            }
        }
    }

    private static bool TryAppend(string directory, string entry)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"), entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 主目录不可写时由调用方尝试用户目录；两处均不可写时保留原有异常终止行为。
            return false;
        }
    }
}
