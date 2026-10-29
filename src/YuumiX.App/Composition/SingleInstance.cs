using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace YuumiX.App.Composition;

/// <summary>
/// 进程单例守护：同一登录会话内只允许存在一个 YuumiX 实例，重复启动改为唤醒已有实例。
///
/// 为什么必须拦（而不是「多点几次也不影响」）：
/// 每个实例都会独立拉起 LCU 连接、自动化宿主、上传队列与托盘图标，并各自读写同一份配置、
/// 日志与 <c>saved_players.db</c>。两份并存时的实际表现是——设置页改完被另一实例覆盖、
/// 同一场对局被记两次（路人集「相遇次数」直接翻倍）、托盘出现两个一模一样的图标。
///
/// 实现通道的选择：命名内核对象（互斥量作存在性令牌）+ 命名事件（唤醒首实例）。
/// 不用 Windows App SDK 的 <c>AppInstance.GetInstances()</c>——那是打包应用（MSIX）的通道，
/// 本项目是 <c>WindowsPackageType=None</c> 的非打包形态，不存在实例注册表可用。
///
/// 名字前缀用 <c>Local\</c> 而非 <c>Global\</c>：单例的语义范围应当是「每个登录会话一份」。
/// 快速用户切换 / 远程桌面多会话下各会话的数据目录本就互不相同，用 <c>Global\</c> 会让
/// 第二个登录的用户启动时被静默拦下且拿不到任何窗口。
/// </summary>
public static class SingleInstance
{
    // 名字带版本后缀只用于将来「换协议」：一旦改动唤醒方式，新版本必须能与旧版本并存判定，
    // 直接改名会让升级过渡期的两个版本同时启动。当前没有这个需求，故保持 v1 不变。
    private const string MutexName = @"Local\YuumiX.SingleInstance.v1";
    private const string ActivationEventName = @"Local\YuumiX.Activate.v1";

    private static ILogger? _logger;

    /// <summary>存在性令牌：只读 <c>createdNew</c>，从不参与等待，故全程不持有所有权。</summary>
    private static Mutex? _mutex;

    /// <summary>唤醒信号（自动重置）：后到的实例 Set，首实例的监听线程接。</summary>
    private static EventWaitHandle? _activationEvent;

    /// <summary>仅本进程可见的退出信号，用来把监听线程从阻塞等待中放出来。</summary>
    private static EventWaitHandle? _shutdownEvent;

    private static Action? _activateAction;
    private static DispatcherQueue? _dispatcherQueue;

    /// <summary>唤醒请求早于主窗挂载时暂存，等 <see cref="RegisterActivation"/> 补发一次。</summary>
    private static volatile bool _activationPending;

    /// <summary>
    /// 尝试成为首实例。
    /// </summary>
    /// <param name="logger">
    /// 可选日志。启动早期（<c>AppHost.Initialize</c> 之前）调用时传 null 是正常的：
    /// 日志基础设施属于 AppHost，为写一行「已存在实例」而提前初始化它，
    /// 会让被拦截的进程去打开首实例正在写的日志文件，代价大于收益。
    /// </param>
    /// <returns>
    /// true = 本进程是首实例，调用方继续正常启动；
    /// false = 已有实例在运行，本进程已尽力把对方拉到前台，调用方应立即退出。
    /// </returns>
    public static bool TryAcquire(ILogger? logger = null)
    {
        if (_mutex is not null)
        {
            return true;
        }

        _logger = logger;

        Mutex mutex;
        bool createdNew;
        try
        {
            // initiallyOwned: false —— 这个互斥量只当「存在性令牌」，不参与任何互斥等待：
            // 判断单例只看 createdNew（同名内核对象已存在 = 已有实例）。
            // 不取所有权就不存在「忘了 ReleaseMutex 导致下一版永远起不来」的失败模式；
            // 进程退出时句柄由内核关闭，最后一个句柄消失即对象销毁，下次启动自然拿到新对象。
            mutex = new Mutex(initiallyOwned: false, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 同名对象存在但当前进程无权打开：几乎必然是另一个完整性级别（如管理员身份运行过）
            // 的实例持有它。按「已有实例」处理——宁可少开一份，也不能让两份同时写同一份数据。
            _logger?.LogWarning(ex, "无法打开单例互斥量，按已有实例处理");
            return false;
        }

        if (!createdNew)
        {
            mutex.Dispose();
            SignalRunningInstance();
            return false;
        }

        _mutex = mutex;
        StartActivationListener();
        return true;
    }

    /// <summary>
    /// 注册「把主窗口拉回前台」的动作。由主窗外壳在挂载时调用一次（与
    /// <see cref="AppLifetime.RegisterExitAction"/> 同一时机、同一调度器）。
    ///
    /// 动作由调用方提供而不是在这里写 Win32：窗口可能是被「关闭到托盘」藏起来的，
    /// 只有应用自己知道该走 <c>Show + Activate</c>；从外部进程强行 ShowWindow
    /// 会让 AppWindow 的可见性状态与内核窗口状态脱节。
    /// </summary>
    public static void RegisterActivation(Action activate, DispatcherQueue dispatcherQueue)
    {
        _activateAction = activate;
        _dispatcherQueue = dispatcherQueue;

        // 首实例还在启动时用户又点了一次图标：那个信号当时没有接收方，这里补发一次。
        if (_activationPending)
        {
            _activationPending = false;
            EnqueueActivation(dispatcherQueue, activate);
        }
    }

    /// <summary>注销唤醒动作（主窗卸载 / 进程退出）。</summary>
    public static void UnregisterActivation()
    {
        _activateAction = null;
        _dispatcherQueue = null;
    }

    /// <summary>
    /// 释放单例资源。消息循环结束后调用；进程退出本身也会关闭句柄，这里显式做是为了
    /// 不必等进程真正结束——用户「退出 → 立刻重开」的窗口期内不该被误判为已有实例。
    /// </summary>
    public static void Release()
    {
        // 先置退出信号让监听线程从 WaitAny 返回，再释放它正在等待的句柄。
        try
        {
            _shutdownEvent?.Set();
        }
        catch (ObjectDisposedException ex)
        {
            // 重复释放或受限环境未创建事件：关闭阶段已无意义，仅记录。
            _logger?.LogDebug(ex, "单例退出信号已释放");
        }

        _activationEvent?.Dispose();
        _activationEvent = null;
        _shutdownEvent?.Dispose();
        _shutdownEvent = null;

        // 只释放句柄、不调 ReleaseMutex：本进程从未持有所有权（见 TryAcquire）。
        _mutex?.Dispose();
        _mutex = null;

        UnregisterActivation();
    }

    /// <summary>
    /// 首实例侧：创建唤醒事件并起监听线程。
    ///
    /// 事件必须在互斥量判定成功之后创建——顺序反了会有真实竞态：先建事件的进程
    /// 可能还没建互斥量，后到的进程便会误判自己是首实例，两份应用同时跑起来。
    /// </summary>
    private static void StartActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 受限环境（沙箱、组策略禁用命名内核对象）下退化为「只拦截、不唤醒」：
            // 用户第二次点击图标不会有任何反应，但绝不会出现第二份实例。
            _logger?.LogWarning(ex, "创建单例唤醒事件失败：本次运行无法被重复启动唤起");
            _activationEvent?.Dispose();
            _activationEvent = null;
            _shutdownEvent?.Dispose();
            _shutdownEvent = null;
            return;
        }

        // 专用线程而不是 Task：这是一次整进程生命周期的阻塞等待，
        // 放进线程池会长期占住一个工作线程（线程池按 CPU 核数配比，占一个就少一个）。
        var thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "YuumiX.SingleInstance",
        };
        thread.Start();
    }

    private static void ListenLoop()
    {
        var activation = _activationEvent;
        var shutdown = _shutdownEvent;
        if (activation is null || shutdown is null)
        {
            return;
        }

        WaitHandle[] handles = [activation, shutdown];
        while (true)
        {
            int signaled;
            try
            {
                signaled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                // Release 先置退出信号再释放句柄，两步之间仍有微小窗口；
                // 退出期被唤醒走这里，与收到退出信号等价。
                return;
            }

            if (signaled != 0)
            {
                return;
            }

            DispatchActivation();
        }
    }

    /// <summary>后实例侧：唤醒已在运行的首实例。失败不抛，本进程无论如何都要退出。</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            // 直接创建而非 TryOpenExisting：CreateEvent 按名字幂等，已有则拿到同一对象。
            // 首实例若尚未建好事件（它抢到互斥量才建），这次 Set 会落在无人接收的对象上，
            // 但那时首实例正在启动、主窗本来就会自己出现，不影响用户观感。
            using var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            handle.Set();
            _logger?.LogInformation("已有 YuumiX 实例在运行，已请求其显示主窗口");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 唤醒通道不可用（受限环境）：仍然退出，单例语义优先——
            // 打不开第二份是正确结果，只是用户需要自己从托盘找回窗口。
            _logger?.LogWarning(ex, "唤醒已有实例失败，本次启动直接结束");
        }
    }

    private static void DispatchActivation()
    {
        var dispatcher = _dispatcherQueue;
        var action = _activateAction;
        if (dispatcher is null || action is null)
        {
            // 主窗还没挂载（首实例仍在启动）：记下来等 RegisterActivation 补发。
            _activationPending = true;
            return;
        }

        EnqueueActivation(dispatcher, action);
    }

    private static void EnqueueActivation(DispatcherQueue dispatcher, Action action)
    {
        try
        {
            if (!dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "显示主窗口失败");
                    }
                }))
            {
                _logger?.LogWarning("向 UI 调度器派发显示主窗口动作被拒绝");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "派发显示主窗口动作失败");
        }
    }
}
