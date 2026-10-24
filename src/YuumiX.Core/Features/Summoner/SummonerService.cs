using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Features.Summoner;

/// <summary>
/// <see cref="ISummonerService"/> 的 LCU 实现。
///
/// 三个要点：
/// <list type="number">
///   <item><b>传输层用提供器而非实例。</b>取 <c>LcuConnection.Transport</c> 的当前值：
///         客户端重连会换端口与 token，绑一个实例会在重连后永久 401。未连接时提供器返回
///         <c>null</c>，直接给 <c>null</c> 而不是抛异常——调用方是界面，不该为此写 try/catch。</item>
///   <item><b>结果缓存。</b>当前召唤师在一次登录期间不会变，而搜索页、生涯页每帧都可能问一次；
///         不缓存等于把本机 HTTP 当内存读。默认 5 分钟，可由构造参数覆盖（测试传 <see cref="TimeSpan.Zero"/> 关闭）。</item>
///   <item><b>不做并发单飞。</b>同时到达的两次请求最多多打一次本机 GET，代价远小于
///         引入共享 Task 带来的取消语义歧义（第一个调用方取消，第二个跟着失败）。</item>
/// </list>
/// </summary>
public sealed class SummonerService : ISummonerService
{
    /// <summary>默认缓存时长。</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>当前召唤师接口路径。</summary>
    internal const string CurrentSummonerPath = "/lol-summoner/v1/current-summoner";

    /// <summary>按名字查询的接口模板（参数：已转义的 Riot ID）。</summary>
    internal const string NamePathTemplate = "/lol-summoner/v1/summoners?name={0}";

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<SummonerService>? _logger;
    private readonly object _gate = new();

    private SummonerProfile? _cached;
    private long _cachedAt;

    /// <param name="transportProvider">当前传输层（来自 <c>LcuConnection.Transport</c>）；未连接时返回 <c>null</c>。</param>
    /// <param name="cacheTtl">缓存时长；传 <see cref="TimeSpan.Zero"/> 关闭缓存。</param>
    /// <param name="logger">可选日志。</param>
    public SummonerService(
        Func<ILcuTransport?> transportProvider,
        TimeSpan? cacheTtl = null,
        ILogger<SummonerService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);

        _transportProvider = transportProvider;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
        _logger = logger;
    }

    public async Task<SummonerProfile?> GetCurrentSummonerAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null && !IsExpired())
            {
                return _cached;
            }
        }

        SummonerProfile? profile = await FetchAsync(cancellationToken).ConfigureAwait(false);

        if (profile is not null)
        {
            lock (_gate)
            {
                _cached = profile;
                _cachedAt = Environment.TickCount64;
            }
        }

        return profile;
    }

    /// <summary>
    /// 按 Riot ID 查询召唤师。刻意不缓存：搜索目标每次都不同，
    /// 缓存只会把上一次的查询结果端出来（换名字后看起来像"搜错了"）。
    /// </summary>
    public async Task<SummonerProfile?> FindByNameAsync(
        string riotId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(riotId);

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 未连接，跳过召唤师搜索");
            return null;
        }

        // 名字里的 '#' 必须转义，否则会被当成 URL 片段，服务端只看到 '#' 之前的部分
        string path = string.Format(
            CultureInfo.InvariantCulture,
            NamePathTemplate,
            Uri.EscapeDataString(riotId.Trim()));

        LcuResponse response = await transport
            .SendAsync(LcuRequest.Get(path), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            // 404 = 名字不存在，是本接口的正常返回之一，不当成错误
            _logger?.LogInformation("按名字查询召唤师失败: HTTP {StatusCode}", response.StatusCode);
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);

            // 载荷与 current-summoner 同构，复用同一个解析器；
            // 个别版本会包一层数组，这里取首个元素而不是另写一套解析。
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            SummonerProfile? profile = SummonerParser.ParseSummoner(root);
            if (profile is null)
            {
                _logger?.LogWarning("召唤师搜索载荷不完整，已忽略");
            }

            return profile;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "召唤师搜索载荷解析失败");
            return null;
        }
    }

    /// <summary>按 puuid 查询的接口模板（与战绩详情的私密检测同接口）。</summary>
    internal const string PuuidPathTemplate = "/lol-summoner/v2/summoners/puuid/{0}";

    /// <summary>按 summonerId 查询的接口模板。</summary>
    internal const string SummonerIdPathTemplate = "/lol-summoner/v1/summoners/{0}";

    /// <summary>
    /// 按 puuid 查询召唤师。刻意不缓存（与 <see cref="FindByNameAsync"/> 同一理由：查看目标每次都不同）。
    /// 载荷与 current-summoner 同构，复用 <see cref="SummonerParser"/>。
    /// </summary>
    public async Task<SummonerProfile?> FindByPuuidAsync(
        string puuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 未连接，跳过召唤师查询");
            return null;
        }

        string path = string.Format(
            CultureInfo.InvariantCulture,
            PuuidPathTemplate,
            Uri.EscapeDataString(puuid.Trim()));

        LcuResponse response = await transport
            .SendAsync(LcuRequest.Get(path), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            // 404 = puuid 不存在（私密账号在部分版本也会 404），是本接口的正常返回之一
            _logger?.LogInformation("按 puuid 查询召唤师失败: HTTP {StatusCode}", response.StatusCode);
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            SummonerProfile? profile = SummonerParser.ParseSummoner(document.RootElement);
            if (profile is null)
            {
                _logger?.LogWarning("按 puuid 查询召唤师载荷不完整，已忽略");
            }

            return profile;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "按 puuid 查询召唤师载荷解析失败");
            return null;
        }
    }

    /// <summary>
    /// 按 summonerId 查询召唤师。刻意不缓存。
    /// 载荷与 current-summoner 同构，复用 <see cref="SummonerParser"/>。
    /// </summary>
    public async Task<SummonerProfile?> FindBySummonerIdAsync(
        long summonerId,
        CancellationToken cancellationToken = default)
    {
        if (summonerId <= 0)
        {
            return null;
        }

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 未连接，跳过召唤师查询");
            return null;
        }

        string path = string.Format(
            CultureInfo.InvariantCulture,
            SummonerIdPathTemplate,
            summonerId);

        LcuResponse response = await transport
            .SendAsync(LcuRequest.Get(path), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            _logger?.LogInformation("按 summonerId 查询召唤师失败: HTTP {StatusCode}", response.StatusCode);
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            SummonerProfile? profile = SummonerParser.ParseSummoner(document.RootElement);
            if (profile is null)
            {
                _logger?.LogWarning("按 summonerId 查询召唤师载荷不完整，已忽略");
            }

            return profile;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "按 summonerId 查询召唤师载荷解析失败");
            return null;
        }
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
            _cachedAt = 0;
        }
    }

    private bool IsExpired()
    {
        if (_cacheTtl <= TimeSpan.Zero)
        {
            return true;
        }

        // TickCount64 是单调递增的毫秒计数，不受系统时间调整影响（用 DateTime 会在改时间后失效）
        return Environment.TickCount64 - _cachedAt >= (long)_cacheTtl.TotalMilliseconds;
    }

    private async Task<SummonerProfile?> FetchAsync(CancellationToken cancellationToken)
    {
        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 未连接，跳过召唤师查询");
            return null;
        }

        LcuResponse response = await transport
            .SendAsync(LcuRequest.Get(CurrentSummonerPath), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            // 客户端正在登录/退出的瞬间，这个接口会返回 404，属正常状态而非错误
            _logger?.LogInformation("获取当前召唤师失败: HTTP {StatusCode}", response.StatusCode);
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            SummonerProfile? profile = SummonerParser.ParseSummoner(document.RootElement);

            if (profile is null)
            {
                _logger?.LogWarning("召唤师载荷不完整，已忽略");
            }

            return profile;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "召唤师载荷解析失败");
            return null;
        }
    }
}
