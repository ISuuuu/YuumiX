using System.Text;

namespace YuumiX.Core.Lcu;

/// <summary>
/// 连接 LCU 所需的凭据。
///
/// 对应原项目 <c>monitor.rs</c> 中三元组 <c>(pid, port, token)</c> 的 C# 形态，
/// 额外携带 <see cref="Server"/>：lockfile 不含大区信息，需要从进程命令行
/// 的 <c>--rso_platform_id=</c> 补充，供后续 SGP（观战）使用。
/// </summary>
public sealed record LcuCredentials
{
    public LcuCredentials(uint port, string token, uint processId = 0, string? server = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535u);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Port = port;
        Token = token;
        ProcessId = processId;
        Server = server;
    }

    /// <summary>LCU HTTPS 端口（lockfile 第三段）。</summary>
    public uint Port { get; }

    /// <summary>remoting auth token（lockfile 第四段）。</summary>
    public string Token { get; }

    /// <summary>LeagueClientUx 进程 ID，用于校验凭据是否已失效。</summary>
    public uint ProcessId { get; }

    /// <summary>登录大区标识（如 <c>CN1</c>），可能为空。</summary>
    public string? Server { get; }

    /// <summary>LCU 固定监听回环地址。自签证书，必须绕过证书校验。</summary>
    public Uri BaseAddress => new($"https://127.0.0.1:{Port}");

    /// <summary>
    /// HTTP Basic 认证头。用户名固定为 <c>riot</c>，与原实现 <c>format!("riot:{}", token)</c> 一致。
    /// </summary>
    public string AuthorizationHeader =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{Token}"));

    /// <summary>日志与诊断用的安全字符串：绝不输出明文 token。</summary>
    public override string ToString() => $"LCU(pid={ProcessId}, port={Port}, server={Server ?? "?"})";
}
