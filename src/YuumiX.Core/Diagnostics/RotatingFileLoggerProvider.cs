using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Diagnostics;

/// <summary>
/// 按天轮转的文件日志 provider，对标原项目 <c>logging.rs</c> 的 <c>RotatingFileWriter</c>。
///
/// 行为约定：
/// <list type="bullet">
///   <item>文件名 <c>yuumi_yyyy-MM-dd.log</c>，单文件超过 <see cref="MaxBytes"/> 后递增后缀
///         <c>yuumi_yyyy-MM-dd-1.log</c>；</item>
///   <item>保留最近 <see cref="RetainDays"/> 天，超期文件在切换日期时清理；</item>
///   <item>行格式 <c>yyyy-MM-dd HH:mm:ss.fff [INFO ] 类别 - 消息</c>。
///         原实现还会打印 Rust 的 <c>file:line</c>，C# 没有等价物（IL 中虽有 sequence point，
///         但取用成本远高于收益），故以日志类别替代，其余格式保持一致。</item>
/// </list>
/// </summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    /// <summary>单文件大小上限（2 MB），与原实现一致。</summary>
    public const long MaxBytes = 2_000_000;

    /// <summary>日志保留天数，与原实现一致。</summary>
    public const int RetainDays = 30;

    private const string FilePrefix = "yuumi";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _retainDays;

    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private int _currentIndex;
    private long _currentSize;
    private bool _purged;

    /// <summary>
    /// 最小输出级别。可在配置加载完成后动态调整（日志级别本身来自配置，
    /// 存在「先有日志才能记配置加载，先有配置才知道日志级别」的循环）。
    /// </summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Information;

    public RotatingFileLoggerProvider(string directory, long maxBytes = MaxBytes, int retainDays = RetainDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _maxBytes = maxBytes;
        _retainDays = retainDays;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        lock (_gate)
        {
            try
            {
                EnsureWriter(today);

                var line = Format(DateTime.Now, level, category, message);
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                _writer!.WriteLine(line);
                _currentSize += Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志写入失败不能让业务崩溃：放弃本次写入，下一次尝试重新打开。
                CloseWriter();
            }
        }
    }

    private void EnsureWriter(DateOnly today)
    {
        if (_writer is not null && today == _currentDate && _currentSize < _maxBytes)
        {
            return;
        }

        if (_writer is not null && today == _currentDate)
        {
            // 同一天但已达大小上限：切换分片。
            CloseWriter();
            _currentIndex++;
            OpenWriter(today, _currentIndex);
            return;
        }

        CloseWriter();
        OpenWriter(today, FindLastIndex(today));
        PurgeExpired(today);
    }

    private void OpenWriter(DateOnly date, int index)
    {
        Directory.CreateDirectory(_directory);

        var path = PathFor(date, index);
        var existingLength = File.Exists(path) ? new FileInfo(path).Length : 0;

        if (existingLength >= _maxBytes)
        {
            index++;
            path = PathFor(date, index);
            existingLength = 0;
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
        _currentDate = date;
        _currentIndex = index;
        _currentSize = existingLength;
    }

    private void CloseWriter()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private string PathFor(DateOnly date, int index)
    {
        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = index == 0
            ? $"{FilePrefix}_{day}.log"
            : $"{FilePrefix}_{day}-{index}.log";
        return Path.Combine(_directory, name);
    }

    /// <summary>找到指定日期最后一个已存在的分片序号（无分片时为 0）。</summary>
    private int FindLastIndex(DateOnly date)
    {
        var index = 0;
        while (File.Exists(PathFor(date, index + 1)))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// 清理超期日志。原实现按文件名中的日期判定，此处按文件最后写入时间判定：
    /// 两者对按天命名的文件等价，而后者在文件被外部改名/复制后仍然正确。
    /// </summary>
    private void PurgeExpired(DateOnly today)
    {
        if (_purged)
        {
            return;
        }

        _purged = true;

        try
        {
            var cutoff = today.ToDateTime(TimeOnly.MinValue).AddDays(-_retainDays);
            foreach (var file in Directory.EnumerateFiles(_directory, $"{FilePrefix}_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响写入
        }
    }

    private static string Format(DateTime timestamp, LogLevel level, string category, string message)
    {
        var text = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var badge = level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => "INFO",
        };

        return $"{text} [{badge,-5}] {category} - {message}";
    }

    private sealed class FileLogger(RotatingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && logLevel >= owner.MinLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
