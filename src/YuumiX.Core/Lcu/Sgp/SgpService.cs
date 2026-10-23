using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Lcu.Sgp;

/// <summary>
/// <see cref="ISgpService"/> 的生产实现。
///
/// 具备：
/// <list type="bullet">
///   <item>大区白名单与 SGP 外网基址动态解析；</item>
///   <item>LCU <c>/entitlements/v1/token</c> 凭证获取与 30 分钟安全缓存；</item>
///   <item>遇到 401 Unauthorized 时的强制换票单次重试机制；</item>
///   <item>调用 <see cref="MatchParser.ParseSgpHistory"/> 的高保真解析与异常隔离。</item>
/// </list>
/// </summary>
public sealed class SgpService : ISgpService, IDisposable
{
    /// <summary>腾讯大区白名单（小写）。</summary>
    private static readonly HashSet<string> TencentServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "hn1", "hn10", "bgp2", "tj100", "cq100", "gz100", "nj100", "tj101",
    };

    /// <summary>需要使用 k8s-sgp 子域名的特殊大区。</summary>
    private static readonly HashSet<string> K8sSgpServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "hn1", "hn10", "bgp2",
    };

    private static readonly TimeSpan TokenCacheDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultSgpTimeout = TimeSpan.FromSeconds(15);
    private const string RiotClientUserAgent = "RiotClient/78.0.1.1352 (Windows;10;co;red)";

    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<LcuCredentials?>? _credentialsProvider;
    private readonly ILogger<SgpService>? _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedAccessToken;
    private DateTimeOffset _tokenFetchedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public SgpService(
        Func<ILcuTransport?> transportProvider,
        Func<LcuCredentials?>? credentialsProvider = null,
        HttpMessageHandler? sgpHandler = null,
        ILogger<SgpService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);

        _transportProvider = transportProvider;
        _credentialsProvider = credentialsProvider;
        _logger = logger;

        if (sgpHandler is not null)
        {
            _httpClient = new HttpClient(sgpHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = DefaultSgpTimeout,
            };
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", RiotClientUserAgent);
            _ownsHttpClient = true;
        }
    }

    /// <summary>判断大区是否属于腾讯国服。</summary>
    public bool IsSupported(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return false;
        }

        return TencentServers.Contains(server.Trim());
    }

    /// <summary>构造 SGP 基址。</summary>
    public static string BuildSgpBaseUrl(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);

        string normalized = server.Trim().ToLowerInvariant();
        return K8sSgpServers.Contains(normalized)
            ? $"https://{normalized}-k8s-sgp.lol.qq.com:21019"
            : $"https://{normalized}-sgp.lol.qq.com:21019";
    }

    /// <summary>
    /// 获取 SGP accessToken。带 30 分钟缓存与 401 强制刷新。
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ILcuTransport? transport = _transportProvider();
        if (transport is null)
        {
            _logger?.LogDebug("LCU 传输层未就绪，跳过获取 SGP accessToken");
            return null;
        }

        if (!forceRefresh && _cachedAccessToken is not null && DateTimeOffset.UtcNow - _tokenFetchedAt < TokenCacheDuration)
        {
            return _cachedAccessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cachedAccessToken is not null && DateTimeOffset.UtcNow - _tokenFetchedAt < TokenCacheDuration)
            {
                return _cachedAccessToken;
            }

            _logger?.LogDebug("正在从 LCU 请求 /entitlements/v1/token 获取 SGP 凭证");
            LcuResponse response = await transport.SendAsync(
                LcuRequest.Get("/entitlements/v1/token"),
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                _logger?.LogWarning("获取 SGP accessToken 失败（HTTP {StatusCode}）", response.StatusCode);
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(response.Body);
            if (!document.RootElement.TryGetProperty("accessToken", out JsonElement tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String)
            {
                _logger?.LogWarning("LCU entitlements 响应中未找到有效的 accessToken");
                return null;
            }

            string? token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger?.LogWarning("LCU entitlements 响应中 accessToken 为空串");
                return null;
            }

            _cachedAccessToken = token;
            _tokenFetchedAt = DateTimeOffset.UtcNow;
            _logger?.LogDebug("已成功获取并缓存 SGP accessToken（有效期 30 分钟）");
            return _cachedAccessToken;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "解析 /entitlements/v1/token 失败");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "请求 /entitlements/v1/token 异常");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// 获取指定玩家在指定大区的观战凭据。
    /// </summary>
    public async Task<YuumiX.Core.Features.Tools.Models.SpectateCredentials?> GetSpectateCredentialsAsync(
        string? server,
        string puuid,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);

        string? effectiveServer = server;
        if (string.IsNullOrWhiteSpace(effectiveServer) && _credentialsProvider is not null)
        {
            effectiveServer = _credentialsProvider()?.Server;
        }

        if (!IsSupported(effectiveServer))
        {
            _logger?.LogDebug("大区 {Server} 不支持 SGP，跳过观战凭据获取", effectiveServer ?? "(null)");
            return null;
        }

        string baseUrl = BuildSgpBaseUrl(effectiveServer!);
        string url = $"{baseUrl}/gsm/v1/ledge/spectator/region/{effectiveServer}/puuid/{Uri.EscapeDataString(puuid)}";

        string? token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger?.LogWarning("无法获取 SGP accessToken，终止本次 SGP 观战请求");
            return null;
        }

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await SendSgpRequestAsync(url, token, cancellationToken).ConfigureAwait(false);

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger?.LogWarning("SGP 请求返回 401 未授权，尝试强制刷新 accessToken 并重试一次");
                token = await GetAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    httpResponse.Dispose();
                    httpResponse = await SendSgpRequestAsync(url, token, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SGP 观战网络请求异常：{Url}", url);
            return null;
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger?.LogWarning("SGP 观战响应失败（HTTP {StatusCode}）：{Url}", (int)httpResponse.StatusCode, url);
                return null;
            }

            try
            {
                byte[] bodyBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(bodyBytes);
                if (!document.RootElement.TryGetProperty("playerCredentials", out JsonElement credElem)
                    || credElem.ValueKind != JsonValueKind.Object)
                {
                    _logger?.LogDebug("SGP 观战响应未包含 playerCredentials（该召唤师当前可能不在游戏中）");
                    return null;
                }

                string ip = credElem.GetProperty("observerServerIp").GetString() ?? string.Empty;
                int port = credElem.GetProperty("observerServerPort").GetInt32();
                string key = credElem.GetProperty("observerEncryptionKey").GetString() ?? string.Empty;
                long gameId = credElem.GetProperty("gameId").GetInt64();

                return new YuumiX.Core.Features.Tools.Models.SpectateCredentials
                {
                    ObserverServerIp = ip,
                    ObserverServerPort = port,
                    ObserverEncryptionKey = key,
                    GameId = gameId,
                    Server = effectiveServer!,
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "解析 SGP 观战凭据 JSON 异常");
                return null;
            }
        }
    }

    /// <summary>
    /// 通过 SGP 接口获取战绩摘要列表。
    /// </summary>
    public async Task<IReadOnlyList<MatchSummary>> GetMatchHistoryAsync(
        string? server,
        string puuid,
        int startIndex,
        int count,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(puuid);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        string? effectiveServer = server;
        if (string.IsNullOrWhiteSpace(effectiveServer) && _credentialsProvider is not null)
        {
            effectiveServer = _credentialsProvider()?.Server;
        }

        if (!IsSupported(effectiveServer))
        {
            _logger?.LogDebug("大区 {Server} 不支持 SGP，跳过 SGP 战绩拉取", effectiveServer ?? "(null)");
            return [];
        }

        string baseUrl = BuildSgpBaseUrl(effectiveServer!);
        string url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/match-history-query/v1/products/lol/player/{1}/SUMMARY?startIndex={2}&count={3}",
            baseUrl,
            Uri.EscapeDataString(puuid),
            startIndex,
            count);

        string? token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger?.LogWarning("无法获取 SGP accessToken，终止本次 SGP 战绩请求");
            return [];
        }

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await SendSgpRequestAsync(url, token, cancellationToken).ConfigureAwait(false);

            // 若遇 401 Unauthorized，强制刷新 accessToken 并重试一次
            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger?.LogWarning("SGP 请求返回 401 未授权，尝试强制刷新 accessToken 并重试一次");
                token = await GetAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    httpResponse.Dispose();
                    httpResponse = await SendSgpRequestAsync(url, token, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SGP 战绩网络请求异常：{Url}", url);
            return [];
        }

        using (httpResponse)
        {
            if (!httpResponse.IsSuccessStatusCode)
            {
                _logger?.LogWarning("SGP 战绩响应失败（HTTP {StatusCode}）：{Url}", (int)httpResponse.StatusCode, url);
                return [];
            }

            try
            {
                byte[] bodyBytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(bodyBytes);
                IReadOnlyList<MatchSummary> parsed = MatchParser.ParseSgpHistory(document.RootElement, puuid);
                _logger?.LogDebug("SGP 成功拉取并解析 {Count} 场对局（startIndex={StartIndex}, count={RequestCount}）",
                    parsed.Count, startIndex, count);
                return parsed;
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "SGP 战绩载荷解析 JSON 失败");
                return [];
            }
        }
    }

    private async Task<HttpResponseMessage> SendSgpRequestAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tokenLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
