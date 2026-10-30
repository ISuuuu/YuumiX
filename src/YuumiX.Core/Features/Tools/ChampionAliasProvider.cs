using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Runtime;
using YuumiX.Core.Updates;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 英雄检索别名的运行时索引：内置基线 + 磁盘缓存 + 按需在线刷新。
/// <para>
/// 键为英雄 id（与 LCU <c>champion-summary.json</c> 的当前版本 id 一致，不含经典模式 60000+ 形态），
/// 值为<b>已规范化</b>的关键词串（小写、仅字母数字汉字），直接供 <see cref="ChampionSearch"/> 做包含匹配。
/// 规范化放在数据侧做一次，避免每次按键对全表重复规范化。
/// </para>
/// </summary>
public interface IChampionAliasProvider
{
    /// <summary>取某英雄的规范化关键词串；索引里没有该 id 时返回 <c>null</c>。</summary>
    string? GetKeywords(int championId);

    /// <summary>索引是否覆盖给定英雄 id 全集（用来发现「表里没有的新英雄」）。</summary>
    bool Covers(IReadOnlyCollection<int> championIds);

    /// <summary>
    /// 按需刷新：缓存缺失 / 应用版本变化 / 超过 7 天 / 覆盖不全时才联网，
    /// 其余情况不发请求。任何失败都静默回退到当前索引，不抛异常。
    /// </summary>
    /// <returns>本次是否真的发起了下载并成功应用。</returns>
    Task<bool> RefreshIfNeededAsync(IReadOnlyCollection<int> championIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IChampionAliasProvider"/> 的生产实现。
///
/// <para><b>三层数据</b></para>
/// <list type="number">
///   <item><b>内置基线</b> <see cref="ChampionAliasTable"/>：编译进二进制，保证断网 / 未登录 / CDN 不可达时
///         别名搜索不降级（这是旧版 Yuumi 「运行时 fetch，失败即失去别名」的短板）。</item>
///   <item><b>磁盘缓存</b> <c>&lt;DataDirectory&gt;/cache/champions/champion_aliases.v1.json</c>：
///         首次成功下载后落盘，附带 <c>appVersion</c> 与 <c>fetchedAt</c> 两个刷新判据。</item>
///   <item><b>在线刷新</b>：腾讯英雄列表 <see cref="SourceUrl"/>（与旧版 <c>championCache.ts</c> 同源）。</item>
/// </list>
///
/// <para><b>刷新触发条件</b>（四条任一命中即刷新，都不命中则零网络请求）</para>
/// <list type="number">
///   <item>没有缓存文件（首次运行）；</item>
///   <item>缓存里的 <c>appVersion</c> 与当前进程版本不同（升级后自动更新，符合「随版本发布更新」的预期）；</item>
///   <item>缓存超过 <see cref="RefreshTtl"/>（7 天）——版本号没变但国服上新了英雄时的兜底；</item>
///   <item>LCU 返回的英雄 id 有缓存里没有的（新英雄立即触发，不用等版本号或 TTL）。</item>
/// </list>
///
/// <para><b>为什么不放进 config.json</b>：这是可再生的缓存数据，不参与配置读写与
/// <c>AppConfig.Normalize</c>；放进配置会让「配置文件」承担缓存职责（体积膨胀、迁移/备份噪音、
/// 用户手改后语义不明）。错误或损坏时直接丢弃重拉即可。</para>
/// </summary>
public sealed class ChampionAliasProvider : IChampionAliasProvider
{
    /// <summary>别名数据源（腾讯英雄列表，返回 <c>{"hero":[{heroId,keywords,...}]}</c>）。</summary>
    public const string SourceUrl = "https://game.gtimg.cn/images/lol/act/img/js/heroList/hero_list.js";

    /// <summary>缓存文件名；带 <c>.v1</c> 便于日后结构升级时整体作废。</summary>
    public const string CacheFileName = "champion_aliases.v1.json";

    /// <summary>缓存结构版本，读到不一致的 <c>schema</c> 直接丢弃重拉。</summary>
    public const int SchemaVersion = 1;

    /// <summary>缓存新鲜期；版本号未变时的兜底刷新周期。</summary>
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(7);

    /// <summary>单次下载超时；失败只影响本次刷新，不影响已有搜索能力。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly RuntimePaths _paths;
    private readonly Func<string> _versionProvider;
    private readonly ILogger<ChampionAliasProvider>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly HttpMessageHandler? _mockHandler;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>当前生效索引（不可变快照，刷新完成时整体替换，读侧无需加锁）。</summary>
    private volatile IReadOnlyDictionary<int, string> _index;

    private string? _cachedVersion;
    private DateTimeOffset? _fetchedAt;

    public ChampionAliasProvider(
        RuntimePaths paths,
        Func<string>? versionProvider = null,
        ILogger<ChampionAliasProvider>? logger = null,
        HttpMessageHandler? mockHandler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _versionProvider = versionProvider ?? (() => UpdateService.CurrentVersion);
        _logger = logger;
        _mockHandler = mockHandler;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 构造期同步读一次缓存（约 15 KB，毫秒级）：让下拉框渲染时 GetKeywords 是纯内存操作，
        // 不在 UI 线程上做磁盘 IO。失败一律退回内置基线。
        _index = LoadCacheOrDefault();
    }

    /// <summary>缓存文件路径：<c>&lt;DataDirectory&gt;/cache/champions/champion_aliases.v1.json</c>。</summary>
    public string CacheFilePath => Path.Combine(_paths.DataDirectory, "cache", "champions", CacheFileName);

    /// <inheritdoc />
    public string? GetKeywords(int championId)
    {
        return _index.TryGetValue(championId, out string? keywords) ? keywords : null;
    }

    /// <inheritdoc />
    public bool Covers(IReadOnlyCollection<int> championIds)
    {
        if (championIds.Count == 0)
        {
            return true;
        }

        var index = _index;
        foreach (int id in championIds)
        {
            if (!index.ContainsKey(id))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshIfNeededAsync(
        IReadOnlyCollection<int> championIds,
        CancellationToken cancellationToken = default)
    {
        if (!NeedRefresh(championIds))
        {
            return false;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间别的调用可能已经刷完
            if (!NeedRefresh(championIds))
            {
                return false;
            }

            var downloaded = await DownloadAsync(cancellationToken).ConfigureAwait(false);
            if (downloaded is null || downloaded.Count == 0)
            {
                return false;
            }

            ApplyIndex(downloaded);
            SaveCache(downloaded);
            _logger?.LogInformation("英雄别名表已更新：{Count} 位英雄", downloaded.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消（页面卸载 / 进程退出），保持现状
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "英雄别名表刷新失败，继续使用当前索引（内置基线/旧缓存）");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool NeedRefresh(IReadOnlyCollection<int> championIds)
    {
        if (_fetchedAt is null)
        {
            return true; // 首次运行：没有可用的磁盘缓存
        }

        if (!string.Equals(_cachedVersion, _versionProvider(), StringComparison.Ordinal))
        {
            return true; // 应用升级：随版本更新一次
        }

        if (_timeProvider.GetUtcNow() - _fetchedAt.Value > RefreshTtl)
        {
            return true; // 兜底：版本未变但可能上了新英雄
        }

        return !Covers(championIds); // 新英雄立刻补，不等版本号或 TTL
    }

    private async Task<Dictionary<int, RawKeyword>?> DownloadAsync(CancellationToken cancellationToken)
    {
        // 刷新频率极低（版本变更 / 7 天 / 出现新英雄），每次独立 HttpClient 即可，
        // 不必为它长期持有连接池与生命周期管理。测试注入的 handler 不归本方法释放。
        using var ownedHandler = _mockHandler is null ? new SocketsHttpHandler() : null;
        using var client = new HttpClient(_mockHandler ?? ownedHandler!, disposeHandler: false)
        {
            Timeout = RequestTimeout,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await client.GetAsync(SourceUrl, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            _logger?.LogWarning("英雄别名表下载失败: HTTP {Status}", (int)response.StatusCode);
            return null;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseSource(body);
    }

    /// <summary>解析腾讯原表：<c>{"hero":[{"heroId":"1","keywords":"安妮,黑暗之女,火女,Annie,anni..."}]}</c>。</summary>
    internal static Dictionary<int, RawKeyword>? ParseSource(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("hero", out var heroes)
                || heroes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var map = new Dictionary<int, RawKeyword>(heroes.GetArrayLength());
            foreach (var hero in heroes.EnumerateArray())
            {
                if (hero.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!hero.TryGetProperty("heroId", out var idElem)
                    || !hero.TryGetProperty("keywords", out var kwElem))
                {
                    continue;
                }

                // 原表 heroId 是字符串；写成数字的接口也兼容
                int id = idElem.ValueKind switch
                {
                    JsonValueKind.String => int.TryParse(idElem.GetString(), out int parsed) ? parsed : 0,
                    JsonValueKind.Number => idElem.TryGetInt32(out int numeric) ? numeric : 0,
                    _ => 0,
                };
                if (id <= 0)
                {
                    continue;
                }

                string keywords = kwElem.GetString() ?? string.Empty;
                if (keywords.Length == 0)
                {
                    continue;
                }

                map[id] = new RawKeyword(keywords);
            }

            return map.Count == 0 ? null : map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyDictionary<int, string> LoadCacheOrDefault()
    {
        var baseline = BuildBaseline();

        try
        {
            string path = CacheFilePath;
            if (!File.Exists(path))
            {
                ApplyPatches(baseline);
                return baseline;
            }

            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<CacheDocument>(stream);
            if (document is null
                || document.Schema != SchemaVersion
                || document.Keywords is null
                || document.Keywords.Count == 0)
            {
                _logger?.LogDebug("英雄别名缓存结构不匹配，忽略并等待重新下载: {Path}", path);
                ApplyPatches(baseline);
                return baseline;
            }

            foreach (var (key, raw) in document.Keywords)
            {
                if (!int.TryParse(key, out int id) || id <= 0 || string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                baseline[id] = ChampionSearch.Normalize(raw);
            }

            // 社区补丁优先级最高：缓存来自腾讯原表，不含「小火龙 / 丁真」这类玩家叫法
            ApplyPatches(baseline);

            _cachedVersion = document.AppVersion;
            _fetchedAt = document.FetchedAt;
            return baseline;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 缓存损坏/不可读：丢弃，退回内置基线；下次 RefreshIfNeeded 会重拉
            _logger?.LogWarning(ex, "读取英雄别名缓存失败，改用内置基线");
            ApplyPatches(baseline);
            return baseline;
        }
    }

    private void ApplyIndex(Dictionary<int, RawKeyword> downloaded)
    {
        var index = BuildBaseline();
        foreach (var (id, raw) in downloaded)
        {
            index[id] = ChampionSearch.Normalize(raw.Keywords);
        }

        ApplyPatches(index);
        _index = index;
        _cachedVersion = _versionProvider();
        _fetchedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// 内置基线的可变副本：<b>不含</b>社区补丁，补丁一律由 <see cref="ApplyPatches"/>
    /// 在「缓存覆盖之后」统一叠加一次，避免同一补丁被追加两遍。
    /// </summary>
    private static Dictionary<int, string> BuildBaseline()
    {
        var map = new Dictionary<int, string>(
            ChampionAliasTable.Keywords.Count + ChampionAliasTable.CommunityPatches.Count);

        foreach (var (id, keywords) in ChampionAliasTable.Keywords)
        {
            map[id] = ChampionSearch.Normalize(keywords);
        }

        return map;
    }

    private static void ApplyPatches(Dictionary<int, string> index)
    {
        foreach (var (id, patch) in ChampionAliasTable.CommunityPatches)
        {
            string normalized = ChampionSearch.Normalize(patch);
            if (normalized.Length == 0)
            {
                continue;
            }

            index[id] = index.TryGetValue(id, out string? existing) && existing.Length > 0
                ? existing + normalized
                : normalized;
        }
    }

    private void SaveCache(Dictionary<int, RawKeyword> downloaded)
    {
        try
        {
            var document = new CacheDocument
            {
                Schema = SchemaVersion,
                AppVersion = _versionProvider(),
                FetchedAt = _fetchedAt ?? _timeProvider.GetUtcNow(),
                Keywords = downloaded.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Keywords),
            };

            string path = CacheFilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 原子写：先写 .tmp 再替换，避免半个 JSON 让下次启动白读
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 写盘失败不影响本次生效（内存索引已经更新），下次启动重新拉一遍即可
            _logger?.LogWarning(ex, "写入英雄别名缓存失败: {Path}", CacheFilePath);
        }
    }

    /// <summary>下载结果的原始关键词（保持字符串形态，落盘时原样写回）。</summary>
    internal readonly record struct RawKeyword(string Keywords);

    /// <summary>缓存文件结构；字段名与文件一一对应，改名需同步升级 <see cref="SchemaVersion"/>。</summary>
    internal sealed class CacheDocument
    {
        public int Schema { get; set; }

        public string AppVersion { get; set; } = string.Empty;

        public DateTimeOffset FetchedAt { get; set; }

        public Dictionary<string, string>? Keywords { get; set; }
    }
}
