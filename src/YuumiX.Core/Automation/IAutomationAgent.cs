namespace YuumiX.Core.Automation;

/// <summary>
/// 自动化代理接口。定义常驻监听与后台自动化任务的生命周期。
/// </summary>
public interface IAutomationAgent : IAsyncDisposable
{
    /// <summary>代理名称。</summary>
    string Name { get; }

    /// <summary>是否处于运行状态。</summary>
    bool IsRunning { get; }

    /// <summary>启动代理循环。</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>停止代理循环。</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
