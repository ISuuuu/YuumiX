using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Features.AramData;

public sealed record AramChampionSummary(
    int Id,
    string Name,
    string Title,
    double WinRate,
    double PickRate,
    int? Tier);

public sealed record AramItemInfo(int Id, string Name, string Description, string? IconUrl = null);

public sealed record AramAugmentInfo(int Id, string Name, string Rarity, string Description, string? IconUrl = null);

public sealed record AramItemGroup(
    IReadOnlyList<int> ItemIds,
    int Games,
    double WinRate,
    double PickRate);

public sealed record AramChampionBuild(
    string Role,
    string Tier,
    int Games,
    double WinRate,
    double PickRate,
    IReadOnlyList<AramItemGroup> StartingItems,
    IReadOnlyList<AramItemGroup> CoreItems,
    IReadOnlyList<AramItemGroup> FullItems,
    IReadOnlyList<int> SituationalItemIds,
    string? BuildLabel = null);

public sealed record AramChampionAugmentStat(
    int AugmentId,
    string Name,
    string Rarity,
    int? Tier,
    double WinRate,
    double PickRate,
    int Games,
    string? IconUrl = null);

public sealed record AramChampionDetail(
    AramChampionSummary Champion,
    IReadOnlyList<AramChampionBuild> Builds,
    IReadOnlyList<AramChampionAugmentStat> Augments);

public sealed record AramLoadedDataset(
    string BaseUrl,
    string DataVersion,
    string? GamePatch,
    IReadOnlyList<AramChampionSummary> Champions,
    IReadOnlyDictionary<int, AramItemInfo> Items,
    IReadOnlyDictionary<int, AramAugmentInfo> Augments,
    IReadOnlyDictionary<int, string> ChampionShardMap,
    bool FromLocalCache = false);

/// <summary>
/// 从 myWebAPI（/api/lol/aram-data/files）拉取并本地持久化缓存 ARAM 原始 JSON 数据（基于 JsonDocument，兼容 NativeAOT）。
/// </summary>
public sealed class AramDataClient : IDisposable
{
    private static readonly string[] RequiredBaseFiles =
    [
        "champions.json",
        "items.json",
        "augments.json",
        "champion-shards/index.json"
    ];

    private readonly HttpClient _httpClient;
    private readonly string _cacheRootDir;
    private readonly ILogger<AramDataClient>? _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _shardJsonCache = new(StringComparer.OrdinalIgnoreCase);

    public AramDataClient(string? cacheRootDir = null, ILogger<AramDataClient>? logger = null)
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _cacheRootDir = string.IsNullOrWhiteSpace(cacheRootDir)
            ? Path.Combine(AppContext.BaseDirectory, "Data", "aram-cache")
            : Path.GetFullPath(cacheRootDir);
        _logger = logger;
    }

    /// <summary>
    /// 尝试直接从本地磁盘缓存加载已激活版本（秒开，不走网络）。
    /// </summary>
    public async Task<AramLoadedDataset?> TryLoadLocalDatasetAsync(
        string? preferredBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var pointer = await ReadPointerAsync(cancellationToken).ConfigureAwait(false);
        if (pointer == null || string.IsNullOrWhiteSpace(pointer.DataVersion))
        {
            return null;
        }

        var versionDir = GetVersionDir(pointer.DataVersion);
        foreach (var file in RequiredBaseFiles)
        {
            if (!File.Exists(ResolveSafeFilePath(versionDir, file)))
            {
                return null;
            }
        }

        var effectiveUrl = !string.IsNullOrWhiteSpace(preferredBaseUrl)
            ? preferredBaseUrl.Trim().TrimEnd('/')
            : pointer.BaseUrl;

        return await LoadDatasetFromDiskAsync(
            effectiveUrl,
            pointer.DataVersion,
            pointer.GamePatch,
            fromLocalCache: true,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查远端版本：若与本地缓存一致且文件完整则直接复用本地缓存；若有新版本则下载全量 JSON 并原子更新本地缓存。
    /// </summary>
    public async Task<AramLoadedDataset> CheckAndSyncAsync(
        string baseUrl,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filesMetaJson = await GetStringAsync($"{normalizedBaseUrl}/api/lol/aram-data/files", cancellationToken).ConfigureAwait(false);
            var (dataVersion, gamePatch, remoteFiles) = ParseFilesMeta(filesMetaJson);
            if (string.IsNullOrWhiteSpace(dataVersion))
            {
                throw new InvalidOperationException("服务端未返回有效的 dataVersion。");
            }

            var safeVersion = SanitizeVersionPart(dataVersion);
            var versionDir = GetVersionDir(safeVersion);
            var pointer = await ReadPointerAsync(cancellationToken).ConfigureAwait(false);

            var filesToSync = remoteFiles.Count > 0
                ? remoteFiles
                : RequiredBaseFiles.Select(p => (Path: p, ByteSize: 0L)).ToList();

            if (!force &&
                pointer != null &&
                string.Equals(pointer.DataVersion, safeVersion, StringComparison.Ordinal) &&
                AreLocalFilesComplete(versionDir, filesToSync))
            {
                _logger?.LogInformation("[AramData] 本地缓存已是最新版本 {DataVersion}，跳过下载", safeVersion);
                return await LoadDatasetFromDiskAsync(
                    normalizedBaseUrl,
                    safeVersion,
                    gamePatch,
                    fromLocalCache: true,
                    cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(versionDir);
            using var gate = new SemaphoreSlim(3, 3);
            var downloadTasks = filesToSync.Select(async file =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var localPath = ResolveSafeFilePath(versionDir, file.Path);
                    if (!force && IsLocalFileComplete(localPath, file.ByteSize))
                    {
                        return;
                    }

                    var content = await GetStringAsync(
                        $"{normalizedBaseUrl}/api/lol/aram-data/files/{file.Path.TrimStart('/')}",
                        cancellationToken).ConfigureAwait(false);

                    await WriteTextAtomicAsync(localPath, content, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(downloadTasks).ConfigureAwait(false);
            _shardJsonCache.Clear();

            await WritePointerAsync(new LocalPointer(normalizedBaseUrl, safeVersion, gamePatch), cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("[AramData] 本地缓存已更新到版本 {DataVersion}（{Count} 个文件）", safeVersion, filesToSync.Count);

            return await LoadDatasetFromDiskAsync(
                normalizedBaseUrl,
                safeVersion,
                gamePatch,
                fromLocalCache: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public Task<AramLoadedDataset> LoadDatasetAsync(
        string baseUrl,
        CancellationToken cancellationToken = default) =>
        CheckAndSyncAsync(baseUrl, force: false, cancellationToken);

    public async Task<AramChampionDetail> LoadChampionDetailAsync(
        AramLoadedDataset dataset,
        int championId,
        CancellationToken cancellationToken = default)
    {
        var champion = dataset.Champions.FirstOrDefault(c => c.Id == championId)
            ?? new AramChampionSummary(championId, $"英雄 {championId}", string.Empty, 0, 0, null);

        if (!dataset.ChampionShardMap.TryGetValue(championId, out var shardPath))
        {
            return new AramChampionDetail(champion, [], []);
        }

        var cacheKey = $"{dataset.DataVersion}|{shardPath}";
        if (!_shardJsonCache.TryGetValue(cacheKey, out var shardJson))
        {
            var versionDir = GetVersionDir(SanitizeVersionPart(dataset.DataVersion));
            var localShardPath = ResolveSafeFilePath(versionDir, shardPath);
            if (File.Exists(localShardPath))
            {
                shardJson = await File.ReadAllTextAsync(localShardPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                shardJson = await GetStringAsync(
                    $"{dataset.BaseUrl}/api/lol/aram-data/files/{shardPath.TrimStart('/')}",
                    cancellationToken).ConfigureAwait(false);
                await WriteTextAtomicAsync(localShardPath, shardJson, cancellationToken).ConfigureAwait(false);
            }

            _shardJsonCache[cacheKey] = shardJson;
        }

        using var doc = JsonDocument.Parse(shardJson);
        if (!doc.RootElement.TryGetProperty("champions", out var championsObj) ||
            championsObj.ValueKind != JsonValueKind.Object ||
            !championsObj.TryGetProperty(championId.ToString(), out var champElem))
        {
            return new AramChampionDetail(champion, [], []);
        }

        var builds = ParseBuilds(champElem);
        var augmentStats = ParseChampionAugments(champElem, dataset.Augments);
        return new AramChampionDetail(champion, builds, augmentStats);
    }

    private async Task<AramLoadedDataset> LoadDatasetFromDiskAsync(
        string baseUrl,
        string dataVersion,
        string? gamePatch,
        bool fromLocalCache,
        CancellationToken cancellationToken)
    {
        var versionDir = GetVersionDir(dataVersion);
        var championsJson = await File.ReadAllTextAsync(ResolveSafeFilePath(versionDir, "champions.json"), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var itemsJson = await File.ReadAllTextAsync(ResolveSafeFilePath(versionDir, "items.json"), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var augmentsJson = await File.ReadAllTextAsync(ResolveSafeFilePath(versionDir, "augments.json"), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var shardIndexJson = await File.ReadAllTextAsync(ResolveSafeFilePath(versionDir, "champion-shards/index.json"), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        return new AramLoadedDataset(
            baseUrl,
            dataVersion,
            gamePatch,
            ParseChampions(championsJson),
            ParseItems(itemsJson),
            ParseAugments(augmentsJson),
            ParseShardIndex(shardIndexJson),
            fromLocalCache);
    }

    private string GetPointerFilePath() => Path.Combine(_cacheRootDir, "current.json");

    private string GetVersionDir(string dataVersion) => Path.Combine(_cacheRootDir, "versions", dataVersion);

    private async Task<LocalPointer?> ReadPointerAsync(CancellationToken cancellationToken)
    {
        var path = GetPointerFilePath();
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = GetString(root, "dataVersion");
            if (string.IsNullOrWhiteSpace(version)) return null;

            return new LocalPointer(
                GetString(root, "baseUrl") ?? "http://localhost:3456",
                version,
                GetString(root, "gamePatch"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[AramData] 读取本地缓存指针失败");
            return null;
        }
    }

    private async Task WritePointerAsync(LocalPointer pointer, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("baseUrl", pointer.BaseUrl);
            writer.WriteString("dataVersion", pointer.DataVersion);
            if (!string.IsNullOrEmpty(pointer.GamePatch))
            {
                writer.WriteString("gamePatch", pointer.GamePatch);
            }
            writer.WriteString("updatedAt", DateTimeOffset.Now.ToString("O"));
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        await WriteTextAtomicAsync(GetPointerFilePath(), json, cancellationToken).ConfigureAwait(false);
    }

    private static bool AreLocalFilesComplete(string versionDir, List<(string Path, long ByteSize)> files)
    {
        foreach (var (filePath, byteSize) in files)
        {
            if (!IsLocalFileComplete(ResolveSafeFilePath(versionDir, filePath), byteSize))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLocalFileComplete(string fullPath, long expectedBytes)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists) return false;
        return expectedBytes > 0 ? info.Length == expectedBytes : info.Length > 0;
    }

    private static async Task WriteTextAtomicAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static string ResolveSafeFilePath(string versionDir, string logicalPath)
    {
        var normalized = logicalPath.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new InvalidOperationException($"非法文件路径：{logicalPath}");

        var segments = normalized.Split('/');
        if (segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".."))
            throw new InvalidOperationException($"检测到非法路径段：{logicalPath}");

        var rootFull = Path.GetFullPath(versionDir);
        var combined = Path.GetFullPath(Path.Combine([rootFull, .. segments]));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"路径越界：{logicalPath}");

        return combined;
    }

    private static string SanitizeVersionPart(string version)
    {
        var cleaned = version.Trim();
        if (cleaned.Contains('/') || cleaned.Contains('\\') || cleaned.Contains(".."))
            throw new InvalidOperationException($"非法 dataVersion：{version}");
        return cleaned;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({url}): {body}");
        }

        return body;
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("请输入有效的 http:// 或 https:// 服务器地址。", nameof(baseUrl));
        }

        return trimmed;
    }

    private static (string DataVersion, string? GamePatch, List<(string Path, long ByteSize)> Files) ParseFilesMeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
            ? dataProp
            : root;

        var version = GetString(data, "dataVersion") ?? string.Empty;
        var patch = GetString(data, "gamePatch");
        var files = new List<(string Path, long ByteSize)>();

        if (data.TryGetProperty("files", out var filesProp) && filesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesProp.EnumerateArray())
            {
                var path = GetString(item, "filePath") ?? GetString(item, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var byteSize = item.TryGetProperty("byteSize", out var bs) && bs.TryGetInt64(out var parsed)
                    ? parsed
                    : 0L;
                files.Add((path, byteSize));
            }
        }

        return (version, patch, files);
    }

    private static List<AramChampionSummary> ParseChampions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = ExtractArray(doc.RootElement, "champions");
        var result = new List<AramChampionSummary>();

        foreach (var item in list)
        {
            var id = GetInt(item, "id") ?? GetInt(item, "championId") ?? 0;
            if (id <= 0) continue;

            var name = GetString(item, "name")
                ?? GetString(item, "nameCN")
                ?? GetString(item, "displayName")
                ?? $"英雄 {id}";
            var title = GetString(item, "title") ?? string.Empty;

            var stats = item.TryGetProperty("stats", out var statsProp) && statsProp.ValueKind == JsonValueKind.Object
                ? statsProp
                : item;

            var winRate = GetDouble(stats, "winRate") ?? GetDouble(stats, "win_rate") ?? 0;
            var pickRate = GetDouble(stats, "pickRate") ?? GetDouble(stats, "pick_rate") ?? 0;
            var tier = GetInt(stats, "tier");

            result.Add(new AramChampionSummary(id, name, title, winRate, pickRate, tier));
        }

        return result.OrderBy(c => c.Tier ?? int.MaxValue).ThenByDescending(c => c.WinRate).ToList();
    }

    private static Dictionary<int, AramItemInfo> ParseItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = ExtractArray(doc.RootElement, "items");
        var result = new Dictionary<int, AramItemInfo>();

        foreach (var item in list)
        {
            var id = GetInt(item, "id") ?? 0;
            if (id <= 0) continue;

            var name = GetString(item, "name") ?? $"装备 {id}";
            var desc = GetString(item, "description") ?? string.Empty;
            var iconUrl = GetString(item, "iconUrl") ?? GetString(item, "iconPath") ?? GetString(item, "icon");
            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                iconUrl = $"https://cdn.dtodo.cn/hextech/item-icons/{id}.png";
            }
            result[id] = new AramItemInfo(id, name, desc, iconUrl);
        }

        return result;
    }

    private static Dictionary<int, AramAugmentInfo> ParseAugments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = ExtractArray(doc.RootElement, "augments");
        var result = new Dictionary<int, AramAugmentInfo>();

        foreach (var item in list)
        {
            var id = GetInt(item, "id") ?? 0;
            if (id <= 0) continue;

            var name = GetString(item, "name") ?? $"强化 {id}";
            var rarity = NormalizeRarity(item);
            var desc = GetString(item, "description") ?? string.Empty;
            var iconUrl = GetString(item, "iconUrl") ?? GetString(item, "iconPath") ?? GetString(item, "icon");
            result[id] = new AramAugmentInfo(id, name, rarity, desc, iconUrl);
        }

        return result;
    }

    private static Dictionary<int, string> ParseShardIndex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, string>();
        if (!doc.RootElement.TryGetProperty("shards", out var shards) || shards.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var shard in shards.EnumerateArray())
        {
            var path = GetString(shard, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (shard.TryGetProperty("championIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var idElem in ids.EnumerateArray())
                {
                    if (TryReadInt(idElem, out var id) && id > 0)
                    {
                        result[id] = path;
                    }
                }
            }
        }

        return result;
    }

    private static List<AramChampionBuild> ParseBuilds(JsonElement champElem)
    {
        var result = new List<AramChampionBuild>();
        if (!champElem.TryGetProperty("builds", out var buildsElem) || buildsElem.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var b in buildsElem.EnumerateArray())
        {
            var role = GetString(b, "role") ?? "ALL";
            var tier = GetString(b, "tier") ?? "-";
            var stats = b.TryGetProperty("stats", out var statsProp) && statsProp.ValueKind == JsonValueKind.Object
                ? statsProp
                : b;

            var games = GetInt(stats, "games") ?? 0;
            var winRate = GetDouble(stats, "winRate") ?? 0;
            var pickRate = GetDouble(stats, "pickRate") ?? 0;
            var buildLabel = ExtractBuildLabel(b);

            var starting = ParseItemGroups(b, "startingItems");
            var core = ParseItemGroups(b, "coreItems");
            var full = ParseItemGroups(b, "fullItems");
            var situational = ParseSituationalItems(b);

            result.Add(new AramChampionBuild(role, tier, games, winRate, pickRate, starting, core, full, situational, buildLabel));
        }

        return result;
    }

    private static string? ExtractBuildLabel(JsonElement buildElem)
    {
        var direct = GetString(buildElem, "name") ?? GetString(buildElem, "title") ?? GetString(buildElem, "label");
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();

        if (buildElem.TryGetProperty("tags", out var tagsProp))
        {
            var tagParts = new List<string>();
            if (tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsProp.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                        tagParts.Add(t.GetString()!.Trim());
                }
            }
            else if (tagsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in tagsProp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                        tagParts.Add(prop.Value.GetString()!.Trim());
                }
            }

            if (tagParts.Count > 0) return string.Join(" / ", tagParts);
        }

        return null;
    }

    private static List<AramItemGroup> ParseItemGroups(JsonElement buildElem, string propertyName)
    {
        var groups = new List<AramItemGroup>();
        if (!buildElem.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return groups;
        }

        foreach (var g in arr.EnumerateArray())
        {
            var ids = ReadIntList(g, "itemIds");
            if (ids.Count == 0) ids = ReadIntList(g, "items");
            if (ids.Count == 0) continue;

            var stats = g.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : g;
            var games = GetInt(stats, "games") ?? 0;
            var winRate = GetDouble(stats, "winRate") ?? GetDouble(stats, "win_rate") ?? 0;
            var pickRate = GetDouble(stats, "pickRate") ?? GetDouble(stats, "pick_rate") ?? 0;

            groups.Add(new AramItemGroup(ids, games, winRate, pickRate));
        }

        return groups.OrderByDescending(g => g.Games).ThenByDescending(g => g.WinRate).ToList();
    }

    private static List<int> ParseSituationalItems(JsonElement buildElem)
    {
        var ids = new List<int>();
        if (!buildElem.TryGetProperty("situationalItems", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in arr.EnumerateArray())
        {
            var id = GetInt(item, "itemId") ?? GetInt(item, "id");
            if (id is > 0 && !ids.Contains(id.Value))
            {
                ids.Add(id.Value);
                continue;
            }

            foreach (var subId in ReadIntList(item, "itemIds"))
            {
                if (!ids.Contains(subId)) ids.Add(subId);
            }
        }

        return ids;
    }

    private static List<AramChampionAugmentStat> ParseChampionAugments(
        JsonElement champElem,
        IReadOnlyDictionary<int, AramAugmentInfo> augmentDict)
    {
        var list = new List<AramChampionAugmentStat>();
        if (!champElem.TryGetProperty("augments", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var a in arr.EnumerateArray())
        {
            var id = GetInt(a, "id") ?? GetInt(a, "augmentId") ?? 0;
            if (id <= 0) continue;

            var info = augmentDict.GetValueOrDefault(id);
            var name = info?.Name ?? GetString(a, "name") ?? $"强化 {id}";
            var rarity = info?.Rarity ?? NormalizeRarity(a);
            var iconUrl = info?.IconUrl ?? GetString(a, "iconUrl") ?? GetString(a, "iconPath") ?? GetString(a, "icon");
            var stats = a.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : a;
            var tier = GetInt(stats, "tier") ?? GetInt(a, "tier");
            var winRate = GetDouble(stats, "winRate") ?? GetDouble(stats, "win_rate") ?? 0;
            var pickRate = GetDouble(stats, "pickRate") ?? GetDouble(stats, "pick_rate") ?? 0;
            var games = GetInt(stats, "games") ?? GetInt(stats, "num_games") ?? 0;

            list.Add(new AramChampionAugmentStat(id, name, rarity, tier, winRate, pickRate, games, iconUrl));
        }

        return list.OrderByDescending(x => x.WinRate).ThenBy(x => x.Tier ?? int.MaxValue).ToList();
    }

    private static JsonElement.ArrayEnumerator ExtractArray(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
        if (root.TryGetProperty("data", out var dataProp))
        {
            if (dataProp.ValueKind == JsonValueKind.Array) return dataProp.EnumerateArray();
            if (dataProp.ValueKind == JsonValueKind.Object && dataProp.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                return inner.EnumerateArray();
        }
        if (root.TryGetProperty(key, out var direct) && direct.ValueKind == JsonValueKind.Array)
            return direct.EnumerateArray();

        return default;
    }

    private static List<int> ReadIntList(JsonElement elem, string propName)
    {
        var list = new List<int>();
        if (!elem.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (TryReadInt(item, out var id) && id > 0) list.Add(id);
        }
        return list;
    }

    private static string NormalizeRarity(JsonElement elem)
    {
        var raw = GetString(elem, "rarityName") ?? GetString(elem, "rarity");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.ToLowerInvariant() switch
            {
                "ksilver" or "silver" or "0" => "白银",
                "kgold" or "gold" or "1" => "黄金",
                "kprismatic" or "prismatic" or "2" => "棱彩",
                _ => raw
            };
        }

        return GetInt(elem, "rarity") switch
        {
            0 => "白银",
            1 => "黄金",
            2 => "棱彩",
            _ => "未知"
        };
    }

    private static string? GetString(JsonElement elem, string propName) =>
        elem.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt(JsonElement elem, string propName) =>
        elem.TryGetProperty(propName, out var p) && TryReadInt(p, out var v) ? v : null;

    private static double? GetDouble(JsonElement elem, string propName)
    {
        if (!elem.TryGetProperty(propName, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static bool TryReadInt(JsonElement p, out int value)
    {
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value)) return true;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out value)) return true;
        value = 0;
        return false;
    }

    private sealed record LocalPointer(string BaseUrl, string DataVersion, string? GamePatch);

    public void Dispose()
    {
        _syncLock.Dispose();
        _httpClient.Dispose();
    }
}
