namespace YuumiX.Core.Lcu;

/// <summary>
/// 一次 LCU 请求。
///
/// <see cref="Path"/> 必须是绝对路径（以 <c>/</c> 开头），且需通过
/// <see cref="LcuApiCatalog.IsAllowed"/> 校验——校验由实现方负责，
/// 让「能不能发」这件事只有一处判定。
/// </summary>
public sealed record LcuRequest
{
    public LcuRequest(HttpMethod method, string path, string? jsonBody = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path[0] != '/')
        {
            throw new ArgumentException($"LCU 路径必须以 '/' 开头，实际为：{path}", nameof(path));
        }

        Method = method;
        Path = path;
        JsonBody = jsonBody;
    }

    public HttpMethod Method { get; }

    public string Path { get; }

    /// <summary>请求体（已序列化的 JSON）。仅 POST / PUT / PATCH 使用。</summary>
    public string? JsonBody { get; }

    public static LcuRequest Get(string path) => new(HttpMethod.Get, path);

    public static LcuRequest Post(string path, string jsonBody) => new(HttpMethod.Post, path, jsonBody);

    public static LcuRequest Put(string path, string jsonBody) => new(HttpMethod.Put, path, jsonBody);

    public static LcuRequest Delete(string path) => new(HttpMethod.Delete, path);
}

/// <summary>一次 LCU 响应。响应体按原样保留为字符串，由上层解析器负责反序列化。</summary>
public sealed record LcuResponse(int StatusCode, string Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>404 在 LCU 上是常态（例如未在选人阶段查询选人会话），需要能被显式区分。</summary>
    public bool IsNotFound => StatusCode == 404;
}

/// <summary>
/// LCU 传输抽象。存在的唯一目的是让上层可被单测：测试注入假实现，
/// 生产注入 <c>LcuHttpTransport</c>。
///
/// 继承 <see cref="IDisposable"/>：生产实现自持 <c>HttpClient</c> 与连接池，
/// 而「谁创建、谁释放」必须由接口表达——否则连接层只能靠类型转换猜测生命周期。
/// 测试桩实现为无操作即可。
/// </summary>
public interface ILcuTransport : IDisposable
{
    /// <summary>
    /// 发送请求。实现方负责：路径白名单校验、并发限流、传输层重试、证书忽略。
    /// </summary>
    /// <exception cref="InvalidOperationException">路径不在白名单内。</exception>
    Task<LcuResponse> SendAsync(LcuRequest request, CancellationToken cancellationToken = default);
}
