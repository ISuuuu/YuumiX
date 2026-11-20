using System.Net;
using Microsoft.Extensions.Logging;

namespace YuumiX.Core.Common;

/// <summary>
/// 外网 HTTP 客户端代理与基础 <see cref="SocketsHttpHandler"/> 构造工具。
/// 收敛多处重复的代理地址规范化、URI 解析与 <see cref="WebProxy"/> 装配逻辑，
/// 同时允许各调用方保留自身的超时、解压、连接池生命周期与系统代理回退策略。
/// </summary>
public static class ProxyHttpClientFactory
{
    /// <summary>
    /// 规范化代理地址：无协议前缀时默认补全 <c>http://</c>。
    /// </summary>
    public static string NormalizeProxyUrl(string proxyAddr)
    {
        var trimmed = proxyAddr.Trim();
        return trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"http://{trimmed}";
    }

    /// <summary>
    /// 尝试将配置中的代理地址解析为绝对 <see cref="Uri"/>。
    /// </summary>
    public static bool TryParseProxyUri(string? proxyAddr, out Uri? proxyUri)
    {
        if (string.IsNullOrWhiteSpace(proxyAddr))
        {
            proxyUri = null;
            return false;
        }

        string normalized = NormalizeProxyUrl(proxyAddr);
        return Uri.TryCreate(normalized, UriKind.Absolute, out proxyUri);
    }

    /// <summary>
    /// 创建已按配置装配代理的 <see cref="SocketsHttpHandler"/>。
    /// </summary>
    /// <param name="enableProxy">是否启用用户配置的 HTTP 代理。</param>
    /// <param name="proxyAddr">用户配置的代理地址（可省略 <c>http://</c> 前缀）。</param>
    /// <param name="disableSystemProxyWhenDisabled">
    /// 当 <paramref name="enableProxy"/> 为 <c>false</c> 或地址为空时，是否显式设 <see cref="SocketsHttpHandler.UseProxy"/> 为 <c>false</c>
    /// （例如 GitHub 更新客户端语义）；为 <c>false</c> 时保留系统默认代理行为。
    /// </param>
    /// <param name="pooledConnectionLifetime">可选的连接池存活时长。</param>
    /// <param name="automaticDecompression">可选的自动解压方式。</param>
    /// <param name="logger">可选日志记录器，用于记录代理生效或地址无效警告。</param>
    /// <param name="logContext">日志前缀标签（如 <c>[AramData]</c> 或 <c>OP.GG</c>）。</param>
    public static SocketsHttpHandler CreateHandler(
        bool enableProxy = false,
        string? proxyAddr = null,
        bool disableSystemProxyWhenDisabled = false,
        TimeSpan? pooledConnectionLifetime = null,
        DecompressionMethods automaticDecompression = DecompressionMethods.None,
        ILogger? logger = null,
        string? logContext = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = automaticDecompression,
        };

        if (pooledConnectionLifetime.HasValue)
        {
            handler.PooledConnectionLifetime = pooledConnectionLifetime.Value;
        }

        if (enableProxy && !string.IsNullOrWhiteSpace(proxyAddr))
        {
            if (TryParseProxyUri(proxyAddr, out var proxyUri) && proxyUri is not null)
            {
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
                if (logger is not null)
                {
                    logger.LogInformation("{Context} 请求已配置代理: {ProxyUrl}", logContext ?? "HTTP", proxyUri);
                }
            }
            else if (logger is not null)
            {
                logger.LogWarning("{Context} 无效的代理地址: {ProxyAddr}", logContext ?? "HTTP", proxyAddr);
            }
        }
        else if (disableSystemProxyWhenDisabled)
        {
            handler.UseProxy = false;
        }

        return handler;
    }

    /// <summary>
    /// 创建带代理配置的 <see cref="HttpClient"/>。
    /// </summary>
    public static HttpClient CreateClient(
        TimeSpan timeout,
        bool enableProxy = false,
        string? proxyAddr = null,
        bool disableSystemProxyWhenDisabled = false,
        TimeSpan? pooledConnectionLifetime = null,
        DecompressionMethods automaticDecompression = DecompressionMethods.None,
        ILogger? logger = null,
        string? logContext = null)
    {
        var handler = CreateHandler(
            enableProxy,
            proxyAddr,
            disableSystemProxyWhenDisabled,
            pooledConnectionLifetime,
            automaticDecompression,
            logger,
            logContext);

        return new HttpClient(handler)
        {
            Timeout = timeout,
        };
    }
}
