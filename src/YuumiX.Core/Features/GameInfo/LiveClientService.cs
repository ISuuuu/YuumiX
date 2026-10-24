using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// <see cref="ILiveClientService"/> 的生产实现。
/// 直连本机 <c>https://127.0.0.1:2999</c>，使用与 LCU 相同的证书忽略与绕开系统代理配置，
/// 请求超时 1.5 秒，失败或端口未就绪时安全返回空值。
/// </summary>
public sealed class LiveClientService : ILiveClientService, IDisposable
{
    internal const string BaseUrl = "https://127.0.0.1:2999";
    internal const string PlayerListPath = "/liveclientdata/playerlist";
    internal const string ActivePlayerNamePath = "/liveclientdata/activeplayername";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<LiveClientService>? _logger;

    public LiveClientService(
        HttpMessageHandler? handler = null,
        ILogger<LiveClientService>? logger = null)
    {
        _logger = logger;
        if (handler is not null)
        {
            _http = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromMilliseconds(1500),
            };
            _ownsHttpClient = true;
        }
        else
        {
            _http = new HttpClient(LcuHttpHandlerFactory.Create(), disposeHandler: true)
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromMilliseconds(1500),
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<IReadOnlyList<LiveClientPlayer>> GetPlayerListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(PlayerListPath, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var list = await JsonSerializer.DeserializeAsync<List<LiveClientPlayer>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return list ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger?.LogDebug(ex, "LiveClient playerlist 获取未就绪或失败");
            return [];
        }
    }

    public async Task<string?> GetActivePlayerNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(ActivePlayerNamePath, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string name = raw.Trim().Trim('"').Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.LogDebug(ex, "LiveClient activeplayername 获取未就绪或失败");
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
