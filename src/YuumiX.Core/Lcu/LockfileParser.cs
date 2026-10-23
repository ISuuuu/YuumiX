namespace YuumiX.Core.Lcu;

/// <summary>lockfile 解析结果。格式：<c>name:pid:port:password:protocol</c>。</summary>
public sealed record LockfileEntry(string Name, uint ProcessId, uint Port, string Password, string Protocol);

/// <summary>
/// 解析 <c>&lt;安装目录&gt;/lockfile</c>。
///
/// 该文件在 LeagueClient 启动时写入、退出时删除，内容为独立成行的单行文本。
/// 解析必须是纯函数：原实现里这段逻辑与进程扫描、文件 IO 缠在一起，
/// 是排障时最难受的地方（读不到 / 格式不完整 / pid 已死 三种失败长得一样）。
/// </summary>
public static class LockfileParser
{
    /// <summary>
    /// 解析 lockfile 文本。任何不完整状态都返回 <c>false</c> 而不是抛异常——
    /// 客户端启动瞬间文件可能只写了一部分，下一次轮询即可。
    /// </summary>
    public static bool TryParse(string? content, out LockfileEntry? entry)
    {
        entry = null;

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // 限制为 5 段：password 是 base64 风格，理论上不含 ':'，但仍按「最后一段是协议」处理
        var parts = content.Trim().Split(':', 5);
        if (parts.Length < 4)
        {
            return false;
        }

        if (!uint.TryParse(parts[1], out var processId) || processId == 0)
        {
            return false;
        }

        if (!uint.TryParse(parts[2], out var port) || port is 0 or > 65535)
        {
            return false;
        }

        var password = parts[3];
        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        // 老客户端可能没有协议段，缺省按 https
        var protocol = parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4])
            ? parts[4].Trim()
            : "https";

        entry = new LockfileEntry(parts[0], processId, port, password, protocol);
        return true;
    }
}

/// <summary>
/// 从 LeagueClientUx 的命令行参数中提取凭据。
///
/// 与 lockfile 是两条独立来源：lockfile 缺失（权限、安装形态、异常退出残留）时靠它兜底。
/// 原实现用 <c>regex_lite</c>，这里改为手写扫描：三个前缀的定位逻辑完全一致，
/// 而引入正则只为这点匹配并不划算。
/// </summary>
public static class CommandLineCredentials
{
    private const string AppPortPrefix = "--app-port=";
    private const string AuthTokenPrefix = "--remoting-auth-token=";
    private const string PlatformIdPrefix = "--rso_platform_id=";

    /// <summary>
    /// 提取端口与 token。<paramref name="server"/> 是登录大区标识，缺失时为 <c>null</c>。
    /// 端口与 token 必须同时存在才算成功——只有其一会让后续请求全部 401。
    /// </summary>
    public static bool TryExtract(string? commandLine, out uint port, out string token, out string? server)
    {
        port = 0;
        token = string.Empty;
        server = null;

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var portText = ReadValue(commandLine, AppPortPrefix, stopsAtDigitOnly: true);
        if (portText is null || !uint.TryParse(portText, out port) || port is 0 or > 65535)
        {
            return false;
        }

        var tokenText = ReadValue(commandLine, AuthTokenPrefix);
        if (string.IsNullOrWhiteSpace(tokenText))
        {
            return false;
        }

        token = tokenText;
        server = ReadValue(commandLine, PlatformIdPrefix);
        return true;
    }

    /// <summary>
    /// 取前缀之后的值，遇到空白或引号即止。
    /// <paramref name="stopsAtDigitOnly"/> 为 true 时（端口），只接受连续数字。
    /// </summary>
    private static string? ReadValue(string source, string prefix, bool stopsAtDigitOnly = false)
    {
        var index = source.IndexOf(prefix, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + prefix.Length;
        var end = start;
        while (end < source.Length)
        {
            var current = source[end];

            if (stopsAtDigitOnly)
            {
                if (!char.IsAsciiDigit(current))
                {
                    break;
                }
            }
            else if (char.IsWhiteSpace(current) || current is '"' or '\'')
            {
                break;
            }

            end++;
        }

        return end > start ? source[start..end] : null;
    }
}
