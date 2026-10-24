using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Features.SavedPlayers;

/// <summary>
/// 路人集本地 SQLite 存储（对应原项目 saved_players.rs）。
///
/// 数据库文件位于 &lt;DataDirectory&gt;/saved_players.db，与原版完全共享同一份数据。
/// </summary>
public sealed class SavedPlayersStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SavedPlayersStore>? _logger;
    private readonly Lock _gate = new();
    private bool _initialized;

    public SavedPlayersStore(RuntimePaths paths, ILogger<SavedPlayersStore>? logger = null)
        : this(Path.Combine(paths.DataDirectory, "saved_players.db"), logger)
    {
    }

    public SavedPlayersStore(string dbPath, ILogger<SavedPlayersStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _connectionString = builder.ToString();
        _logger = logger;
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
            cmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS saved_players (
                    puuid TEXT NOT NULL,
                    self_puuid TEXT NOT NULL,
                    region TEXT NOT NULL DEFAULT '',
                    rso_platform_id TEXT NOT NULL DEFAULT '',
                    tag TEXT,
                    summoner_name TEXT NOT NULL DEFAULT '',
                    profile_icon_id INTEGER NOT NULL DEFAULT 0,
                    update_at INTEGER NOT NULL,
                    last_met_at INTEGER,
                    tag_line TEXT,
                    champion_id INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (puuid, self_puuid, region, rso_platform_id)
                );
                CREATE TABLE IF NOT EXISTS encountered_games (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id INTEGER NOT NULL,
                    puuid TEXT NOT NULL,
                    self_puuid TEXT NOT NULL,
                    region TEXT NOT NULL DEFAULT '',
                    rso_platform_id TEXT NOT NULL DEFAULT '',
                    queue_type TEXT NOT NULL DEFAULT '',
                    update_at INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_encountered_puuid
                    ON encountered_games (puuid, self_puuid, queue_type);
            ";
            cmd.ExecuteNonQuery();

            _initialized = true;
        }
    }

    /// <summary>
    /// 查询全部保存玩家（支持分页、筛选、搜索）。
    /// </summary>
    public async Task<PagedResult<SavedPlayerRecord>> QueryPlayersAsync(
        string? selfPuuid = null,
        int page = 1,
        int pageSize = 50,
        string? filter = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var whereParts = new List<string> { "1=1" };
        var hasSelfPuuid = !string.IsNullOrWhiteSpace(selfPuuid);
        if (hasSelfPuuid)
        {
            whereParts.Add("saved_players.self_puuid = @selfPuuid");
        }

        if (string.Equals(filter, "tagged", StringComparison.OrdinalIgnoreCase))
        {
            whereParts.Add("saved_players.tag IS NOT NULL AND saved_players.tag != ''");
        }
        else if (string.Equals(filter, "multiple", StringComparison.OrdinalIgnoreCase))
        {
            whereParts.Add("(SELECT COUNT(*) FROM encountered_games eg WHERE eg.puuid = saved_players.puuid AND eg.self_puuid = saved_players.self_puuid) >= 2");
        }

        var searchKw = search?.Trim();
        var hasSearch = !string.IsNullOrEmpty(searchKw);
        if (hasSearch)
        {
            whereParts.Add("(saved_players.summoner_name LIKE @kw OR saved_players.tag_line LIKE @kw OR saved_players.tag LIKE @kw)");
        }

        var whereClause = string.Join(" AND ", whereParts);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 1. 总数计数
        long totalCount = 0;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM saved_players WHERE {whereClause}";
            if (hasSelfPuuid) countCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid!);
            if (hasSearch) countCmd.Parameters.AddWithValue("@kw", $"%{searchKw}%");
            var result = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is long count) totalCount = count;
        }

        if (totalCount == 0)
        {
            return new PagedResult<SavedPlayerRecord>(Array.Empty<SavedPlayerRecord>(), 0);
        }

        // 2. 分页查列表
        var items = new List<SavedPlayerRecord>();
        await using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText = $@"
                SELECT saved_players.puuid, saved_players.self_puuid, saved_players.region, saved_players.rso_platform_id,
                       saved_players.tag, saved_players.summoner_name, saved_players.profile_icon_id,
                       saved_players.tag_line, saved_players.champion_id, saved_players.update_at, saved_players.last_met_at,
                       (SELECT eg.queue_type FROM encountered_games eg WHERE eg.puuid = saved_players.puuid AND eg.self_puuid = saved_players.self_puuid ORDER BY eg.update_at DESC LIMIT 1) AS last_queue_type,
                       (SELECT COUNT(*) FROM encountered_games eg WHERE eg.puuid = saved_players.puuid AND eg.self_puuid = saved_players.self_puuid) AS encounter_cnt
                FROM saved_players
                WHERE {whereClause}
                ORDER BY saved_players.last_met_at DESC, saved_players.update_at DESC
                LIMIT @limit OFFSET @offset";

            if (hasSelfPuuid) listCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid!);
            if (hasSearch) listCmd.Parameters.AddWithValue("@kw", $"%{searchKw}%");
            listCmd.Parameters.AddWithValue("@limit", pageSize);
            listCmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);

            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new SavedPlayerRecord
                {
                    Puuid = reader.GetString(0),
                    SelfPuuid = reader.GetString(1),
                    Region = reader.GetString(2),
                    RsoPlatformId = reader.GetString(3),
                    Tag = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SummonerName = reader.GetString(5),
                    ProfileIconId = reader.GetInt32(6),
                    TagLine = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ChampionId = reader.GetInt32(8),
                    UpdateAt = reader.GetInt64(9),
                    LastMetAt = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    LastQueueType = reader.IsDBNull(11) ? null : reader.GetString(11),
                    EncounterCount = reader.IsDBNull(12) ? 1 : Math.Max(1, reader.GetInt32(12)),
                });
            }
        }

        return new PagedResult<SavedPlayerRecord>(items, totalCount);
    }

    /// <summary>
    /// 查询某位玩家的相遇对局记录。
    /// </summary>
    public async Task<PagedResult<EncounteredGameRecord>> QueryEncounteredGamesAsync(
        string puuid,
        string? selfPuuid = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var where = "puuid = @puuid";
        var hasSelf = !string.IsNullOrWhiteSpace(selfPuuid);
        if (hasSelf) where += " AND self_puuid = @selfPuuid";

        long totalCount = 0;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM encountered_games WHERE {where}";
            countCmd.Parameters.AddWithValue("@puuid", puuid);
            if (hasSelf) countCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid!);
            var res = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (res is long count) totalCount = count;
        }

        var items = new List<EncounteredGameRecord>();
        await using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText = $@"
                SELECT id, game_id, puuid, self_puuid, region, rso_platform_id, queue_type, update_at
                FROM encountered_games
                WHERE {where}
                ORDER BY update_at DESC
                LIMIT @limit OFFSET @offset";

            listCmd.Parameters.AddWithValue("@puuid", puuid);
            if (hasSelf) listCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid!);
            listCmd.Parameters.AddWithValue("@limit", pageSize);
            listCmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);

            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new EncounteredGameRecord
                {
                    Id = reader.GetInt64(0),
                    GameId = reader.GetInt64(1),
                    Puuid = reader.GetString(2),
                    SelfPuuid = reader.GetString(3),
                    Region = reader.GetString(4),
                    RsoPlatformId = reader.GetString(5),
                    QueueType = reader.GetString(6),
                    UpdateAt = reader.GetInt64(7),
                });
            }
        }

        return new PagedResult<EncounteredGameRecord>(items, totalCount);
    }

    /// <summary>
    /// 查询已标记的玩家列表（puuid -> tag），用于最近队友和战局提醒优先级展示。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetTaggedPlayersAsync(
        string selfPuuid,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(selfPuuid))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT puuid, tag
            FROM saved_players
            WHERE self_puuid = @selfPuuid AND tag IS NOT NULL AND tag != ''";
        cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var puuid = reader.GetString(0);
            var tag = reader.GetString(1);
            map[puuid] = tag;
        }

        return map;
    }

    /// <summary>
    /// 获取全部保存玩家的精简映射：puuid → 标记信息（tag + 相遇次数 + 最近相遇对局 + 最近相遇时间 + 相遇对局 ID 列表）。
    /// 对应旧版 Rust 命令 <c>get_saved_players_map</c>，对局信息页徽章与宿命检测候选共用。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SavedPlayerMarker>> GetSavedPlayersMapAsync(
        string selfPuuid,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(selfPuuid))
        {
            return new Dictionary<string, SavedPlayerMarker>();
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<string, SavedPlayerMarker>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT sp.puuid,
                   sp.tag,
                   (SELECT COUNT(*) FROM encountered_games eg
                    WHERE eg.puuid = sp.puuid AND eg.self_puuid = sp.self_puuid),
                   (SELECT eg.game_id FROM encountered_games eg
                    WHERE eg.puuid = sp.puuid AND eg.self_puuid = sp.self_puuid
                    ORDER BY eg.update_at DESC LIMIT 1),
                   sp.last_met_at,
                   (SELECT GROUP_CONCAT(DISTINCT eg.game_id) FROM encountered_games eg
                    WHERE eg.puuid = sp.puuid AND eg.self_puuid = sp.self_puuid)
            FROM saved_players sp WHERE sp.self_puuid = @selfPuuid";
        cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long encounterCount = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            var gameIds = new List<long>();
            if (!reader.IsDBNull(5))
            {
                foreach (string part in reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(part.Trim(), out long gid))
                    {
                        gameIds.Add(gid);
                    }
                }
            }

            map[reader.GetString(0)] = new SavedPlayerMarker
            {
                Tag = reader.IsDBNull(1) ? null : reader.GetString(1),
                EncounterCount = (int)Math.Clamp(encounterCount, 0, int.MaxValue),
                LastEncounteredGameId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                LastMetAt = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                EncounteredGameIds = gameIds,
            };
        }

        return map;
    }

    /// <summary>
    /// 保存或修改玩家标记。
    /// </summary>
    public async Task SaveTagAsync(string puuid, string selfPuuid, string? tag, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE saved_players
            SET tag = @tag, update_at = @now
            WHERE puuid = @puuid AND self_puuid = @selfPuuid";
        cmd.Parameters.AddWithValue("@puuid", puuid);
        cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
        cmd.Parameters.AddWithValue("@tag", (object?)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除某位保存的玩家记录及其相遇历史。
    /// </summary>
    public async Task DeletePlayerAsync(string puuid, string selfPuuid, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var tx = conn.BeginTransaction();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM saved_players WHERE puuid = @puuid AND self_puuid = @selfPuuid";
            cmd.Parameters.AddWithValue("@puuid", puuid);
            cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM encountered_games WHERE puuid = @puuid AND self_puuid = @selfPuuid";
            cmd.Parameters.AddWithValue("@puuid", puuid);
            cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }
}
