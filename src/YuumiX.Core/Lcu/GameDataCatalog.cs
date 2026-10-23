using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Lcu;

/// <summary>海克斯强化（斗魂竞技场）条目。图标字段在 LCU 不同版本里名字不一致，解析时已归一。</summary>
public sealed record CherryAugmentDetail(int Id, string Name, string IconPath);

/// <summary>
/// 游戏静态资源映射表（ID → iconPath / 名称），对应原 <c>GameDataAssets</c>。
///
/// 这层只保存**路径**，不保存字节：真正取图由 <c>AssetService</c> 负责（ADR-11）。
/// 拆开的原因是两者的失效节奏不同——路径表随客户端版本变，图片则可以缓存 7 天。
///
/// 战绩解析、对局详情、云顶页面都依赖这份表把 <c>item0=1001</c> 之类的 ID 变成可显示的图。
/// </summary>
public sealed class GameDataCatalog
{
    /// <summary>空表。客户端未连接或数据未就绪时返回它，调用方无需判空。</summary>
    public static readonly GameDataCatalog Empty = new(
        new Dictionary<int, string>(),
        new Dictionary<int, string>(),
        new Dictionary<int, string>(),
        new Dictionary<int, string>(),
        new Dictionary<int, CherryAugmentDetail>());

    public GameDataCatalog(
        IReadOnlyDictionary<int, string> items,
        IReadOnlyDictionary<int, string> spells,
        IReadOnlyDictionary<int, string> runes,
        IReadOnlyDictionary<int, string> champions,
        IReadOnlyDictionary<int, CherryAugmentDetail> augments)
    {
        Items = items;
        Spells = spells;
        Runes = runes;
        Champions = champions;
        Augments = augments;
    }

    /// <summary>物品 ID → 图标路径。</summary>
    public IReadOnlyDictionary<int, string> Items { get; }

    /// <summary>召唤师技能 ID → 图标路径。</summary>
    public IReadOnlyDictionary<int, string> Spells { get; }

    /// <summary>符文（perk）ID → 图标路径。</summary>
    public IReadOnlyDictionary<int, string> Runes { get; }

    /// <summary>英雄 ID → 英雄名称（上传/展示用）。</summary>
    public IReadOnlyDictionary<int, string> Champions { get; }

    /// <summary>海克斯强化 ID → 详情。</summary>
    public IReadOnlyDictionary<int, CherryAugmentDetail> Augments { get; }

    /// <summary>
    /// 是否拿到了核心数据。判定范围与原实现一致：**不含**海克斯强化——
    /// 该接口在部分客户端形态下不存在，把它算进就绪条件会让重试永远失败。
    /// </summary>
    public bool HasCoreData =>
        Items.Count > 0 || Spells.Count > 0 || Runes.Count > 0 || Champions.Count > 0;

    public override string ToString() =>
        $"GameData(物品={Items.Count}, 技能={Spells.Count}, 符文={Runes.Count}, 英雄={Champions.Count}, 强化={Augments.Count})";
}

/// <summary>
/// 游戏资源映射表的加载器，对应原 <c>fetch_game_data_assets</c>。
///
/// 两层容错，缺一不可：
/// <list type="number">
///   <item><b>整轮重试</b>：客户端刚连上时这几个接口可能整体返回空数组，重试 3 次、间隔 2 秒；</item>
///   <item><b>单接口灵活解析</b>：某个接口返回的既不是数组、也不是标准字段名时，退化为按对象遍历提取。
///         原实现里这一段是被真实结构漂移逼出来的，不是过度防御。</item>
/// </list>
/// </summary>
public sealed class GameDataLoader
{
    /// <summary>整轮重试次数。</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>整轮重试间隔。</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    internal const string ItemsPath = "/lol-game-data/assets/v1/items.json";
    internal const string SpellsPath = "/lol-game-data/assets/v1/summoner-spells.json";
    internal const string PerksPath = "/lol-game-data/assets/v1/perks.json";
    internal const string ChampionsPath = "/lol-game-data/assets/v1/champion-summary.json";
    internal const string AugmentsPath = "/lol-game-data/assets/v1/cherry-augments.json";

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly ILogger<GameDataLoader>? _logger;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;

    /// <param name="transportProvider">
    /// 当前传输层提供器（来自 <c>LcuConnection.Transport</c>）。用提供器而非实例，
    /// 因为重连会换传输层；未连接时返回 <c>null</c>，加载直接给出空表。
    /// </param>
    /// <param name="logger">可选日志。</param>
    /// <param name="retryDelay">整轮重试间隔（测试可缩短）。</param>
    /// <param name="maxAttempts">整轮重试次数。</param>
    public GameDataLoader(
        Func<ILcuTransport?> transportProvider,
        ILogger<GameDataLoader>? logger = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        _transportProvider = transportProvider;
        _logger = logger;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _maxAttempts = maxAttempts;
    }

    /// <summary>加载一份映射表。失败时返回空表或部分结果，绝不抛异常。</summary>
    public async Task<GameDataCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 未连接，跳过游戏资源加载");
            return GameDataCatalog.Empty;
        }

        var catalog = GameDataCatalog.Empty;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            catalog = await LoadOnceAsync(transport, cancellationToken).ConfigureAwait(false);

            if (catalog.HasCoreData)
            {
                _logger?.LogInformation("游戏资源加载完成: {Catalog}", catalog);
                return catalog;
            }

            if (attempt < _maxAttempts)
            {
                _logger?.LogWarning(
                    "游戏资源为空（{Attempt}/{MaxAttempts}），{Delay} 秒后重试",
                    attempt,
                    _maxAttempts,
                    _retryDelay.TotalSeconds);

                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger?.LogWarning("{MaxAttempts} 次尝试后游戏资源仍为空", _maxAttempts);
        return catalog;
    }

    private async Task<GameDataCatalog> LoadOnceAsync(ILcuTransport transport, CancellationToken cancellationToken)
    {
        // 五个接口并发：串行时最坏情况要等 5 × 单次超时，而它们之间没有依赖
        var itemsTask = FetchIdIconMapAsync(transport, ItemsPath, cancellationToken);
        var spellsTask = FetchIdIconMapAsync(transport, SpellsPath, cancellationToken);
        var runesTask = FetchIdIconMapAsync(transport, PerksPath, cancellationToken);
        var championsTask = FetchChampionNamesAsync(transport, cancellationToken);
        var augmentsTask = FetchAugmentsAsync(transport, cancellationToken);

        await Task.WhenAll(itemsTask, spellsTask, runesTask, championsTask, augmentsTask).ConfigureAwait(false);

        var items = await EnsureNonEmptyAsync(transport, ItemsPath, itemsTask, cancellationToken).ConfigureAwait(false);
        var spells = await EnsureNonEmptyAsync(transport, SpellsPath, spellsTask, cancellationToken).ConfigureAwait(false);
        var runes = await EnsureNonEmptyAsync(transport, PerksPath, runesTask, cancellationToken).ConfigureAwait(false);

        return new GameDataCatalog(
            items,
            spells,
            runes,
            await championsTask.ConfigureAwait(false),
            await augmentsTask.ConfigureAwait(false));
    }

    /// <summary>标准解析没拿到数据时，用灵活解析再试一次同一个路径。</summary>
    private async Task<Dictionary<int, string>> EnsureNonEmptyAsync(
        ILcuTransport transport,
        string path,
        Task<Dictionary<int, string>> standardTask,
        CancellationToken cancellationToken)
    {
        var standard = await standardTask.ConfigureAwait(false);
        if (standard.Count > 0)
        {
            return standard;
        }

        _logger?.LogWarning("{Path} 标准解析为空，改用灵活解析重试", path);

        var root = await FetchJsonAsync(transport, path, cancellationToken).ConfigureAwait(false);
        return root is null ? standard : ExtractIdIconMap(root.Value, flexible: true);
    }

    private async Task<Dictionary<int, string>> FetchIdIconMapAsync(
        ILcuTransport transport,
        string path,
        CancellationToken cancellationToken)
    {
        var root = await FetchJsonAsync(transport, path, cancellationToken).ConfigureAwait(false);
        return root is null ? [] : ExtractIdIconMap(root.Value, flexible: false);
    }

    private async Task<Dictionary<int, string>> FetchChampionNamesAsync(
        ILcuTransport transport,
        CancellationToken cancellationToken)
    {
        var root = await FetchJsonAsync(transport, ChampionsPath, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return [];
        }

        var map = new Dictionary<int, string>();

        if (root.Value.ValueKind != JsonValueKind.Array)
        {
            _logger?.LogWarning("champion-summary.json 不是数组，跳过英雄名称映射");
            return map;
        }

        foreach (var entry in root.Value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryReadInt(entry, "id", out var id)
                || !entry.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            map[id] = name;
        }

        if (map.Count == 0)
        {
            _logger?.LogWarning("champion-summary.json 解析后为空，上传时 championName 将退化为未知");
        }

        return map;
    }

    private async Task<Dictionary<int, CherryAugmentDetail>> FetchAugmentsAsync(
        ILcuTransport transport,
        CancellationToken cancellationToken)
    {
        var root = await FetchJsonAsync(transport, AugmentsPath, cancellationToken).ConfigureAwait(false);
        if (root is null || root.Value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var map = new Dictionary<int, CherryAugmentDetail>();

        foreach (var entry in root.Value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !TryReadInt(entry, "id", out var id))
            {
                continue;
            }

            // 图标字段在不同版本里叫 augmentSmallIconPath / iconPath / icon
            var iconPath = ReadFirstString(entry, "augmentSmallIconPath", "iconPath", "icon");
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                continue;
            }

            var name = ReadFirstString(entry, "nameTRA", "simpleNameTRA", "name") ?? string.Empty;
            map[id] = new CherryAugmentDetail(id, name, iconPath);
        }

        return map;
    }

    private async Task<JsonElement?> FetchJsonAsync(
        ILcuTransport transport,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await transport.SendAsync(LcuRequest.Get(path), cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                _logger?.LogWarning("获取 {Path} 失败: HTTP {StatusCode}", path, response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(response.Body);

            // Clone 脱离文档生命周期：JsonDocument 一释放，RootElement 即失效
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "解析 {Path} 失败", path);
            return null;
        }
        catch (Exception ex) when (ex is LcuTransportException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "请求 {Path} 失败", path);
            return null;
        }
    }

    /// <summary>
    /// 从 JSON 中提取 (id, iconPath)。
    ///
    /// 标准模式只认「顶层数组 + 每项 id/iconPath」；灵活模式额外接受：
    /// <list type="bullet">
    ///   <item>顶层对象，值里嵌数组（按技能名分组的形态）；</item>
    ///   <item>顶层对象，值里嵌对象；</item>
    ///   <item>顶层对象本身就是单条记录。</item>
    /// </list>
    /// 这三种形态都来自原实现遇到过的真实漂移，不是假设。
    /// </summary>
    private static Dictionary<int, string> ExtractIdIconMap(JsonElement root, bool flexible)
    {
        var map = new Dictionary<int, string>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                TryAdd(entry, map);
            }

            return map;
        }

        if (!flexible || root.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var entry in property.Value.EnumerateArray())
                    {
                        TryAdd(entry, map);
                    }

                    break;

                case JsonValueKind.Object:
                    TryAdd(property.Value, map);
                    break;
            }
        }

        if (map.Count == 0)
        {
            TryAdd(root, map);
        }

        return map;
    }

    private static void TryAdd(JsonElement entry, Dictionary<int, string> map)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !TryReadInt(entry, "id", out var id)
            || !entry.TryGetProperty("iconPath", out var iconElement)
            || iconElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var iconPath = iconElement.GetString();
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            map[id] = iconPath;
        }
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;

        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static string? ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }
}

/// <summary>
/// 映射表的进程内缓存。
///
/// 为什么需要它：一份完整映射有数千条记录，战绩页每次翻页都重新拉五个接口既慢又浪费；
/// 而客户端版本在会话期间不会变，缓存到进程结束是安全的。
/// 客户端重启后由 <see cref="Invalidate"/> 显式失效——不做成自动监听是为了让
/// 「什么时候重新加载」由调用方决定，避免又一条隐式的后台循环。
/// </summary>
public sealed class GameDataService : IDisposable
{
    private readonly GameDataLoader _loader;
    private readonly ILogger<GameDataService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private GameDataCatalog? _cached;

    public GameDataService(GameDataLoader loader, ILogger<GameDataService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(loader);

        _loader = loader;
        _logger = logger;
    }

    /// <summary>最近一次加载成功的表；未加载或已失效时为 <c>null</c>。</summary>
    public GameDataCatalog? Current => Volatile.Read(ref _cached);

    /// <summary>
    /// 取映射表。已缓存直接返回；否则加载一次（并发调用只会触发一次加载）。
    /// 空表不会被当作有效缓存——客户端刚启动时加载失败是常态，下次调用应重试。
    /// </summary>
    public async Task<GameDataCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _cached) is { HasCoreData: true } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _cached) is { HasCoreData: true } winner)
            {
                return winner;
            }

            var catalog = await _loader.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (catalog.HasCoreData)
            {
                Volatile.Write(ref _cached, catalog);
            }

            return catalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清空缓存（客户端重启或版本变化时调用）。</summary>
    public void Invalidate()
    {
        _logger?.LogDebug("游戏资源缓存已失效");
        Volatile.Write(ref _cached, null);
    }

    public void Dispose() => _gate.Dispose();
}
