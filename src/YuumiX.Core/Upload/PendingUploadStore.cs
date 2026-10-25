using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Upload;

/// <summary>
/// 上传失败后暂存在本地的对局任务（对应旧 upload.rs 的 <c>PendingUploadItem</c>）。
/// 载荷整份保存：重试时直接重发，不再依赖当时还能不能从 LCU 拉到详情。
/// </summary>
public sealed record PendingUploadItem(
    long GameId,
    UploadPayload Payload,
    int RetryCount,
    string LastError,
    string CreatedAt);

/// <summary>
/// 暂存上传任务的本地 SQLite 存储（对应旧 upload.rs 的 <c>pending_uploads</c> 表）。
///
/// 存在的理由：上传失败（断网、服务端 5xx）若不落盘，这一局战绩就永久丢失——
/// 客户端关掉后 LCU 里的对局详情随时可能查不到。旧实现用同一套规则验证了两年，
/// 这里按原样复刻：成功即删除、失败累加次数、超过次数上限不再自动重试、总量保留最新 100 条。
///
/// 与路人集共用 <c>saved_players.db</c>：旧版 <c>pending_uploads</c> 就在这张库里，
/// 沿用同一文件可以让老用户升级后直接接着传没传完的对局。
/// </summary>
public sealed class PendingUploadStore : IDisposable
{
    /// <summary>单条暂存的自动重试上限，超过后不再自动入队（旧 MAX_UPLOAD_RETRY_COUNT）。</summary>
    public const int MaxRetryCount = 10;

    /// <summary>本地保留的暂存档位数量（旧逻辑保留最新 100 条）。</summary>
    public const int RetentionLimit = 100;

    /// <summary>
    /// 载荷序列化选项必须与上传时一致：<see cref="System.Net.Http.Json.HttpClientJsonExtensions"/>
    /// 用的是 Web 默认值（camelCase），落盘再取出重发时字段命名不能漂移。
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly ILogger<PendingUploadStore>? _logger;
    private readonly Lock _gate = new();
    private bool _initialized;
    private bool _disposed;

    public PendingUploadStore(RuntimePaths paths, ILogger<PendingUploadStore>? logger = null)
        : this(Path.Combine(paths.DataDirectory, "saved_players.db"), logger)
    {
    }

    public PendingUploadStore(string dbPath, ILogger<PendingUploadStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _logger = logger;
    }

    /// <summary>读取全部暂存任务（旧 load_pending_uploads，按创建时间升序）。</summary>
    public IReadOnlyList<PendingUploadItem> Load()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        var items = new List<PendingUploadItem>();
        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT game_id, payload, retry_count, last_error, created_at
                FROM pending_uploads
                ORDER BY created_at ASC
                """;

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long gameId = reader.GetInt64(0);
                string payloadJson = reader.GetString(1);
                int retryCount = reader.GetInt32(2);
                string lastError = reader.GetString(3);
                string createdAt = reader.GetString(4);

                UploadPayload? payload = TryDeserialize(payloadJson, gameId);
                if (payload is null)
                {
                    continue;
                }

                items.Add(new PendingUploadItem(gameId, payload, retryCount, lastError, createdAt));
            }
        }

        return items;
    }

    /// <summary>写入或累加一条暂存记录（旧 add_or_update_pending_upload）。失败原因一并保存，便于排查。</summary>
    public void AddOrUpdate(UploadPayload payload, string error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        string payloadJson;
        try
        {
            payloadJson = JsonSerializer.Serialize(payload, PayloadJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            _logger?.LogError(ex, "序列化上传载荷失败，放弃暂存对局 {GameId}", payload.MatchInfo.MatchId);
            return;
        }

        long gameId = payload.MatchInfo.MatchId;
        string now = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss");

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var transaction = conn.BeginTransaction();

            int existingRetry = 0;
            bool exists = false;
            using (var select = conn.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT retry_count FROM pending_uploads WHERE game_id = $gameId";
                select.Parameters.AddWithValue("$gameId", gameId);
                object? value = select.ExecuteScalar();
                if (value is not null && value is not DBNull)
                {
                    exists = true;
                    existingRetry = Convert.ToInt32(value);
                }
            }

            using var write = conn.CreateCommand();
            write.Transaction = transaction;
            if (exists)
            {
                write.CommandText = """
                    UPDATE pending_uploads
                    SET retry_count = $retry, last_error = $error, payload = $payload
                    WHERE game_id = $gameId
                    """;
                write.Parameters.AddWithValue("$retry", existingRetry + 1);
            }
            else
            {
                write.CommandText = """
                    INSERT INTO pending_uploads (game_id, payload, retry_count, last_error, created_at)
                    VALUES ($gameId, $payload, 1, $error, $createdAt)
                    """;
                write.Parameters.AddWithValue("$createdAt", now);
            }

            write.Parameters.AddWithValue("$gameId", gameId);
            write.Parameters.AddWithValue("$payload", payloadJson);
            write.Parameters.AddWithValue("$error", error);
            write.ExecuteNonQuery();

            // 只保留最新的 RetentionLimit 条：旧版同样丢弃更早的记录，
            // 避免长期离线后堆积成千上万条拖慢启动时的重试入队。
            // 次级排序用 game_id：created_at 只到秒，同一秒内写入多条时
            // 单靠时间戳无法确定保留哪一批，而 gameId 单调递增，等价于"保留最近的对局"。
            using var trim = conn.CreateCommand();
            trim.Transaction = transaction;
            trim.CommandText = $"""
                DELETE FROM pending_uploads
                WHERE game_id NOT IN (
                    SELECT game_id FROM pending_uploads
                    ORDER BY created_at DESC, game_id DESC
                    LIMIT {RetentionLimit}
                )
                """;
            trim.ExecuteNonQuery();

            transaction.Commit();
        }
    }

    /// <summary>上传成功后擦除（旧 remove_pending_upload）。</summary>
    public void Remove(long gameId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM pending_uploads WHERE game_id = $gameId";
            cmd.Parameters.AddWithValue("$gameId", gameId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>当前暂存条数（设置页展示「待重试 N 场」用）。</summary>
    public int Count()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();

        lock (_gate)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending_uploads";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    private UploadPayload? TryDeserialize(string json, long gameId)
    {
        try
        {
            return JsonSerializer.Deserialize<UploadPayload>(json, PayloadJsonOptions);
        }
        catch (JsonException ex)
        {
            // 旧版本或字段漂移留下的脏数据：跳过而不是让整个重试队列卡死
            _logger?.LogWarning(ex, "暂存对局 {GameId} 的载荷无法解析，跳过重试", gameId);
            return null;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS pending_uploads (
                    game_id INTEGER PRIMARY KEY,
                    payload TEXT NOT NULL,
                    retry_count INTEGER NOT NULL DEFAULT 1,
                    last_error TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
