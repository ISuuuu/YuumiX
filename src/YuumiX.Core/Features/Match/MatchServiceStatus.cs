namespace YuumiX.Core.Features.Match;

/// <summary>战绩请求的生命周期状态。</summary>
public enum MatchLoadState
{
    /// <summary>尚未请求过，或结果已被清空。</summary>
    Idle,

    /// <summary>请求进行中。</summary>
    Loading,

    /// <summary>请求成功。</summary>
    Loaded,

    /// <summary>请求失败（客户端未连接、接口 4xx/5xx、解析异常）。</summary>
    Failed,
}

/// <summary>
/// 一次战绩请求的状态快照，经 <see cref="IMatchService.Status"/> 推给界面。
///
/// <see cref="Message"/> 是面向日志与提示的中文说明，不用于程序判断——判断一律看
/// <see cref="State"/>，避免文案调整破坏调用方逻辑（与 <c>LcuConnectionState</c> 同一纪律）。
/// </summary>
public sealed record MatchServiceStatus(MatchLoadState State, string? Message)
{
    /// <summary>空闲。</summary>
    public static MatchServiceStatus Idle { get; } = new(MatchLoadState.Idle, null);

    /// <summary>加载中，<paramref name="message"/> 说明正在做什么（如"正在拉取第 2 页"）。</summary>
    public static MatchServiceStatus Loading(string message) => new(MatchLoadState.Loading, message);

    /// <summary>加载成功，<paramref name="message"/> 可携带条数等摘要。</summary>
    public static MatchServiceStatus Loaded(string message) => new(MatchLoadState.Loaded, message);

    /// <summary>加载失败，<paramref name="message"/> 为可直接展示的失败原因。</summary>
    public static MatchServiceStatus Failed(string message) => new(MatchLoadState.Failed, message);
}
