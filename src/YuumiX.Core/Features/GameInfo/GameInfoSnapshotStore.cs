using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 保留对局快照的磁盘存取。旧版（Tauri）走 WebView localStorage 天然跨启动，
/// 这里落一个 JSON 文件补齐同一语义。文件定位为可丢弃缓存：
/// 读取遇到缺失/损坏/模型演进不兼容一律降级为「无快照」，不阻断启动；
/// 写入异常原样抛出，由调用方（GameInfoService 的后台刷盘）记录日志。
/// </summary>
public sealed class GameInfoSnapshotStore
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 与 ConfigSchema.JsonOptions 同理：中文原文输出，不做 \uXXXX 转义
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly ILogger<GameInfoSnapshotStore>? _logger;

    public string FilePath { get; }

    public GameInfoSnapshotStore(string filePath, ILogger<GameInfoSnapshotStore>? logger = null)
    {
        FilePath = filePath;
        _logger = logger;
    }

    /// <summary>读取磁盘快照；任何失败（缺失 / 损坏 / 版本不识别）返回 <c>null</c>。</summary>
    public ReservedSnapshot? Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                var payload = JsonSerializer.Deserialize<SnapshotPayload>(File.ReadAllText(FilePath, Utf8NoBom), JsonOptions);
                if (payload is not { Version: SchemaVersion, Snapshot: not null })
                {
                    _logger?.LogWarning("保留对局快照版本不识别，忽略: {Path}", FilePath);
                    return null;
                }

                return payload.Snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger?.LogWarning(ex, "读取保留对局快照失败，按无快照处理: {Path}", FilePath);
                return null;
            }
        }
    }

    /// <summary>写入快照（.tmp 原子替换）；传 <c>null</c> 表示清空（删除文件）。</summary>
    public void Write(ReservedSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (snapshot is null)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                return;
            }

            var payload = new SnapshotPayload(SchemaVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), snapshot);
            string tmpPath = FilePath + ".tmp";
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tmpPath, JsonSerializer.Serialize(payload, JsonOptions), Utf8NoBom);
                File.Move(tmpPath, FilePath, overwrite: true);
            }
            finally
            {
                // Move 成功后 tmp 已不存在；写失败残留时清掉，避免下次写入被旧 tmp 干扰。
                // 清理失败不影响主流程（下次写入会 overwrite 同名 tmp）。
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch (IOException)
                {
                    // 清理失败不影响主流程
                }
            }
        }
    }

    private sealed record SnapshotPayload(int Version, long SavedAtMs, ReservedSnapshot Snapshot);
}
