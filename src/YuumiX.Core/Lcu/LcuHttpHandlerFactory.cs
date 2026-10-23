using System.Net;

namespace YuumiX.Core.Lcu;

/// <summary>
/// LCU 的 HTTP 处理器工厂。存在的原因是**同一套非默认配置要在两处使用**：
/// API 传输（<see cref="LcuHttpTransport"/>）与资源下载（<c>LcuAssetSource</c>）。
/// 两处都必须：忽略自签证书、绕开系统代理。配置一旦分叉，症状是「API 正常但图标全裂」，
/// 而这类症状最难归因，所以收敛到一处。
/// </summary>
internal static class LcuHttpHandlerFactory
{
    /// <summary>
    /// 生产处理器。两处非默认配置都有明确理由：
    /// <list type="bullet">
    ///   <item><b>忽略证书</b>：LCU 用自签证书，等价于原实现的 <c>danger_accept_invalid_certs</c>；</item>
    ///   <item><b>禁用代理</b>：本机系统代理在 <c>127.0.0.1:7897</c>，回环地址走代理必然连不上。
    ///         注意 <c>Proxy = null</c> 在 .NET 上表示「沿用系统默认代理」，必须用无地址的
    ///         <see cref="WebProxy"/>（其 <c>GetProxy</c> 返回目标本身，即直连）。</item>
    /// </list>
    /// </summary>
    public static HttpMessageHandler Create()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = new WebProxy(),
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        return handler;
    }
}
