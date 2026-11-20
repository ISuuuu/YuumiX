namespace YuumiX.Core.Lcu;

/// <summary>
/// LCU 运行时传输层与凭据访问器。
/// 重连会替换底层传输实例，各服务通过此访问器跨重连按需获取当前传输层与凭据，
/// 避免在 DI 注册中重复编写 <c>() =&gt; provider.GetRequiredService&lt;LcuConnection&gt;().Transport</c> 闭包。
/// </summary>
public interface ILcuTransportAccessor
{
    /// <summary>当前活动的 LCU HTTP 传输层；未连接时为 <c>null</c>。</summary>
    ILcuTransport? Transport { get; }

    /// <summary>当前活动的 LCU 凭据；未连接时为 <c>null</c>。</summary>
    LcuCredentials? Credentials { get; }
}

internal sealed class LcuConnectionTransportAccessor(LcuConnection connection) : ILcuTransportAccessor
{
    private readonly LcuConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public ILcuTransport? Transport => _connection.Transport;

    public LcuCredentials? Credentials => _connection.Credentials;
}
