using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Encodings.Web;
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
                -- 同一局同一玩家只允许一条相遇记录。历史库里已存在重复行（断线重连会让
                -- 同一 gameId 被缓存两次，见 AutoEncounterRecorderAgent），必须先去重再建
                -- 唯一索引，否则 CREATE UNIQUE INDEX 会因现存重复行直接失败。
                DELETE FROM encountered_games
                WHERE id NOT IN (
                    SELECT MIN(id) FROM encountered_games GROUP BY game_id, puuid, self_puuid);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_encountered_game
                    ON encountered_games (game_id, puuid, self_puuid);
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
            whereParts.Add("(saved_players.summoner_name LIKE @kw OR saved_players.tag_line LIKE @kw OR saved_players.tag LIKE @kw OR (saved_players.summoner_name || '#' || COALESCE(saved_players.tag_line, '')) LIKE @kw)");
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
    public async Task DeletePlayerAsync(string puuid, string? selfPuuid = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var hasSelf = !string.IsNullOrWhiteSpace(selfPuuid);

        await using var tx = conn.BeginTransaction();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = hasSelf
                ? "DELETE FROM saved_players WHERE puuid = @puuid AND self_puuid = @selfPuuid"
                : "DELETE FROM saved_players WHERE puuid = @puuid";
            cmd.Parameters.AddWithValue("@puuid", puuid);
            if (hasSelf) cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid!);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = hasSelf
                ? "DELETE FROM encountered_games WHERE puuid = @puuid AND self_puuid = @selfPuuid"
                : "DELETE FROM encountered_games WHERE puuid = @puuid";
            cmd.Parameters.AddWithValue("@puuid", puuid);
            if (hasSelf) cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid!);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 对局结束批量记录相遇玩家（对应原系统 record_encounters）。
    /// </summary>
    public async Task<int> RecordEncountersAsync(
        IReadOnlyList<EncounterPlayerEntry> players,
        string selfPuuid,
        long gameId,
        string queueType,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(selfPuuid) || players.Count == 0)
        {
            return 0;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = conn.BeginTransaction();

        int recorded = 0;
        foreach (var player in players)
        {
            if (string.IsNullOrWhiteSpace(player.Puuid) || string.Equals(player.Puuid, selfPuuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 查询复用已存在的 region 与 rso_platform_id，避免同一玩家因 region 不一致产生重复行
            string region = string.Empty;
            string rsoPlatformId = string.Empty;
            await using (var selCmd = conn.CreateCommand())
            {
                selCmd.Transaction = tx;
                selCmd.CommandText = "SELECT region, rso_platform_id FROM saved_players WHERE puuid = @puuid AND self_puuid = @selfPuuid LIMIT 1";
                selCmd.Parameters.AddWithValue("@puuid", player.Puuid);
                selCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
                await using var reader = await selCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    region = reader.GetString(0);
                    rsoPlatformId = reader.GetString(1);
                }
            }

            // Upsert saved_players
            await using (var upsertCmd = conn.CreateCommand())
            {
                upsertCmd.Transaction = tx;
                upsertCmd.CommandText = @"
                    INSERT INTO saved_players (
                        puuid, self_puuid, region, rso_platform_id, tag, summoner_name,
                        profile_icon_id, tag_line, champion_id, update_at, last_met_at
                    )
                    VALUES (@puuid, @selfPuuid, @region, @rsoPlatformId, NULL, @name, @icon, @tagLine, @champId, @now, @now)
                    ON CONFLICT(puuid, self_puuid, region, rso_platform_id) DO UPDATE SET
                        summoner_name = CASE WHEN excluded.summoner_name = '' THEN saved_players.summoner_name ELSE excluded.summoner_name END,
                        profile_icon_id = CASE WHEN excluded.profile_icon_id = 0 THEN saved_players.profile_icon_id ELSE excluded.profile_icon_id END,
                        tag_line = CASE WHEN excluded.tag_line IS NULL OR excluded.tag_line = '' THEN saved_players.tag_line ELSE excluded.tag_line END,
                        champion_id = CASE WHEN excluded.champion_id = 0 THEN saved_players.champion_id ELSE excluded.champion_id END,
                        update_at = excluded.update_at,
                        last_met_at = excluded.last_met_at";

                upsertCmd.Parameters.AddWithValue("@puuid", player.Puuid);
                upsertCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
                upsertCmd.Parameters.AddWithValue("@region", region);
                upsertCmd.Parameters.AddWithValue("@rsoPlatformId", rsoPlatformId);
                upsertCmd.Parameters.AddWithValue("@name", player.SummonerName ?? string.Empty);
                upsertCmd.Parameters.AddWithValue("@icon", player.ProfileIconId);
                upsertCmd.Parameters.AddWithValue("@tagLine", (object?)player.TagLine ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@champId", player.ChampionId);
                upsertCmd.Parameters.AddWithValue("@now", now);

                await upsertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Insert encountered_games —— 同一局同一玩家只保留一条。
            // 断线重连会让同一 gameId 走两遍记录流程，且结束阶段有三个 phase 都会触发，
            // 靠唯一索引兜底：重复写入静默丢弃，否则 COUNT(*) 得到的"相遇次数"会翻倍。
            await using (var encCmd = conn.CreateCommand())
            {
                encCmd.Transaction = tx;
                encCmd.CommandText = @"
                    INSERT INTO encountered_games (game_id, puuid, self_puuid, region, rso_platform_id, queue_type, update_at)
                    VALUES (@gameId, @puuid, @selfPuuid, @region, @rsoPlatformId, @queueType, @now)
                    ON CONFLICT(game_id, puuid, self_puuid) DO NOTHING";

                encCmd.Parameters.AddWithValue("@gameId", gameId);
                encCmd.Parameters.AddWithValue("@puuid", player.Puuid);
                encCmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
                encCmd.Parameters.AddWithValue("@region", region);
                encCmd.Parameters.AddWithValue("@rsoPlatformId", rsoPlatformId);
                encCmd.Parameters.AddWithValue("@queueType", queueType ?? string.Empty);
                encCmd.Parameters.AddWithValue("@now", now);

                int inserted = await encCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (inserted > 0)
                {
                    recorded++;
                }
            }
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recorded;
    }

    /// <summary>
    /// 导出已标记的玩家记录为 JSON 字符串（对应原系统 export_tagged_players_to_json_file）。
    /// </summary>
    public async Task<string> ExportTaggedPlayersJsonAsync(
        string? selfPuuid = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = @"
            SELECT puuid, self_puuid, region, rso_platform_id, tag, summoner_name,
                   profile_icon_id, tag_line, champion_id, update_at, last_met_at
            FROM saved_players
            WHERE tag IS NOT NULL AND tag != ''";

        if (!string.IsNullOrWhiteSpace(selfPuuid))
        {
            sql += " AND self_puuid = @selfPuuid";
        }

        sql += " ORDER BY last_met_at DESC, update_at DESC";

        var list = new List<SavedPlayerRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (!string.IsNullOrWhiteSpace(selfPuuid))
        {
            cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);
        }

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new SavedPlayerRecord
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
            });
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.Serialize(list, options);
    }

    /// <summary>
    /// 从 JSON 字符串批量导入标记玩家（对应原系统 import_tagged_players_from_json_file）。
    /// </summary>
    public async Task<int> ImportTaggedPlayersJsonAsync(
        string json,
        string? defaultSelfPuuid = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        List<SavedPlayerRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<SavedPlayerRecord>>(json, options);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析标记玩家 JSON 失败");
            return 0;
        }

        if (records is null || records.Count == 0)
        {
            return 0;
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = conn.BeginTransaction();

        int imported = 0;
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.Puuid))
            {
                continue;
            }

            var selfId = !string.IsNullOrWhiteSpace(r.SelfPuuid) ? r.SelfPuuid : (defaultSelfPuuid ?? string.Empty);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long updateAt = r.UpdateAt > 0 ? r.UpdateAt : now;
            long? lastMetAt = r.LastMetAt;

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO saved_players (
                    puuid, self_puuid, region, rso_platform_id, tag, summoner_name,
                    profile_icon_id, tag_line, champion_id, update_at, last_met_at
                )
                VALUES (@puuid, @selfPuuid, @region, @rsoPlatformId, @tag, @name, @icon, @tagLine, @champId, @updateAt, @lastMetAt)
                ON CONFLICT(puuid, self_puuid, region, rso_platform_id) DO UPDATE SET
                    tag = excluded.tag,
                    summoner_name = CASE WHEN excluded.summoner_name = '' THEN saved_players.summoner_name ELSE excluded.summoner_name END,
                    profile_icon_id = CASE WHEN excluded.profile_icon_id = 0 THEN saved_players.profile_icon_id ELSE excluded.profile_icon_id END,
                    tag_line = CASE WHEN excluded.tag_line IS NULL OR excluded.tag_line = '' THEN saved_players.tag_line ELSE excluded.tag_line END,
                    champion_id = CASE WHEN excluded.champion_id = 0 THEN saved_players.champion_id ELSE excluded.champion_id END,
                    update_at = excluded.update_at,
                    last_met_at = COALESCE(excluded.last_met_at, saved_players.last_met_at)";

            cmd.Parameters.AddWithValue("@puuid", r.Puuid);
            cmd.Parameters.AddWithValue("@selfPuuid", selfId);
            cmd.Parameters.AddWithValue("@region", r.Region ?? string.Empty);
            cmd.Parameters.AddWithValue("@rsoPlatformId", r.RsoPlatformId ?? string.Empty);
            cmd.Parameters.AddWithValue("@tag", (object?)r.Tag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@name", r.SummonerName ?? string.Empty);
            cmd.Parameters.AddWithValue("@icon", r.ProfileIconId);
            cmd.Parameters.AddWithValue("@tagLine", (object?)r.TagLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@champId", r.ChampionId);
            cmd.Parameters.AddWithValue("@updateAt", updateAt);
            cmd.Parameters.AddWithValue("@lastMetAt", (object?)lastMetAt ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            imported++;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return imported;
    }

    /// <summary>
    /// 获取需要回填身份信息的玩家（缺少 tag_line、召唤师名称为空或头像为 0）。
    /// </summary>
    public async Task<IReadOnlyList<(string Puuid, string SelfPuuid)>> GetPendingBackfillPlayersAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<(string Puuid, string SelfPuuid)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT puuid, self_puuid
            FROM saved_players
            WHERE tag_line IS NULL OR tag_line = '' OR summoner_name IS NULL OR summoner_name = '' OR profile_icon_id = 0
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    /// <summary>
    /// 批量保存回填的身份信息。
    /// </summary>
    public async Task<int> SaveBackfilledIdentitiesAsync(
        IEnumerable<(string Puuid, string SelfPuuid, string TagLine, string SummonerName, int ProfileIconId)> records,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = conn.BeginTransaction();

        int count = 0;
        foreach (var (puuid, selfPuuid, tagLine, summonerName, profileIconId) in records)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                UPDATE saved_players
                SET tag_line = CASE WHEN tag_line IS NULL OR tag_line = '' THEN @tagLine ELSE tag_line END,
                    summoner_name = CASE WHEN summoner_name IS NULL OR summoner_name = '' THEN @name ELSE summoner_name END,
                    profile_icon_id = CASE WHEN profile_icon_id = 0 THEN @icon ELSE profile_icon_id END
                WHERE puuid = @puuid AND self_puuid = @selfPuuid";

            cmd.Parameters.AddWithValue("@tagLine", (object?)tagLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@name", (object?)summonerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@icon", profileIconId);
            cmd.Parameters.AddWithValue("@puuid", puuid);
            cmd.Parameters.AddWithValue("@selfPuuid", selfPuuid);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            count++;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public void Dispose()
    {
    }
}
