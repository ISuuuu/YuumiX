using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Diagnostics;

/// <summary>
/// 日志初始化。级别取值与原项目保持一致：0=Debug，1=Info（默认），2=Error。
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// 把配置中的 <c>LogLevel</c> 数值映射为 <see cref="LogLevel"/>。
    /// 未知值（含历史遗留的 10/20/30/40）统一回退 Info，避免静默放大日志量。
    /// </summary>
    public static LogLevel MapLogLevel(int logLevel) => logLevel switch
    {
        0 => LogLevel.Debug,
        2 => LogLevel.Error,
        _ => LogLevel.Information,
    };

    /// <summary>
    /// 构建只写文件的 <see cref="ILoggerFactory"/>。
    ///
    /// 刻意不挂控制台 provider：宿主是 WinExe，双击启动时没有控制台可写；
    /// 而从终端启动时文件日志同样可见，徒增一份输出并无收益。
    /// </summary>
    public static ILoggerFactory CreateFactory(RotatingFileLoggerProvider fileProvider)
    {
        ArgumentNullException.ThrowIfNull(fileProvider);

        return LoggerFactory.Create(builder =>
        {
            // 工厂层面放行全部级别，真正的过滤由 provider 的 MinLevel 负责，
            // 这样才能在配置加载完成后动态下调/上调级别。
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(fileProvider);
        });
    }
}
