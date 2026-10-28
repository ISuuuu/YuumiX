using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YuumiX.App.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Composition;

/// <summary>
/// 单条 Toast 消息。Id 全局递增，供宿主做调和 Key 与定点移除。
/// </summary>
public sealed record ToastEntry(long Id, string Message, InfoBarSeverity Severity);

/// <summary>
/// 全局 Toast 服务（R5）：页面只调 <c>Success</c> / <c>Error</c> / <c>Info</c>，
/// 呈现与生命周期由 <see cref="ToastHost"/> 承担。
/// </summary>
public interface IToastService
{
    /// <summary>成功提示。</summary>
    void Success(string message);

    /// <summary>错误提示。</summary>
    void Error(string message);

    /// <summary>中性提示（进行中、已存在等非成败语义）。</summary>
    void Info(string message);
}

/// <summary>
/// <see cref="IToastService"/> 的队列实现。每个窗口根组件各持一份
/// （主窗口 AppShell 与 OP.GG 独立窗口互不串扰）。
///
/// 队列状态用锁保护：入队可能来自页面后台回调线程（await ConfigureAwait(false) 之后），
/// 变更经 <see cref="Changed"/> 通知宿主组件，由 threadSafe UseState 封送回 UI 线程。
/// 旧的五份页面级手写实现（Timer + lock + DispatcherQueue + 渲染闭包捕获）在这里收敛为一份，
/// 同刻多条入队互相覆盖丢失的并发缺陷一并消除。
/// </summary>
public sealed class ToastQueue : IToastService
{
    /// <summary>同屏最多同时展示的条数（对齐旧版 n-message-provider :max="5"）。</summary>
    private const int MaxVisible = 5;

    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly List<ToastEntry> _items = [];
    private long _nextId;

    /// <summary>队列变更通知，参数为当前快照。可能在任意线程触发。</summary>
    public event Action<IReadOnlyList<ToastEntry>>? Changed;

    /// <summary>当前队列快照。</summary>
    public IReadOnlyList<ToastEntry> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <inheritdoc/>
    public void Success(string message) => Push(message, InfoBarSeverity.Success);

    /// <inheritdoc/>
    public void Error(string message) => Push(message, InfoBarSeverity.Error);

    /// <inheritdoc/>
    public void Info(string message) => Push(message, InfoBarSeverity.Informational);

    /// <summary>移除一条 Toast；用户点 InfoBar 关闭按钮时由宿主回调。</summary>
    public void Dismiss(long id)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(item => item.Id == id) == 0)
            {
                return;
            }
        }

        Changed?.Invoke(Snapshot);
    }

    private void Push(string message, InfoBarSeverity severity)
    {
        long id;
        lock (_gate)
        {
            id = ++_nextId;
            _items.Add(new ToastEntry(id, message, severity));
            if (_items.Count > MaxVisible)
            {
                _items.RemoveAt(0);
            }
        }

        Changed?.Invoke(Snapshot);
        _ = ExpireAfterDelayAsync(id);
    }

    private async Task ExpireAfterDelayAsync(long id)
    {
        await Task.Delay(DisplayDuration).ConfigureAwait(false);
        Dismiss(id);
    }
}

/// <summary>
/// 全局 Toast 宿主：挂在窗口根（AppShell / OpggWindowRoot），订阅 <see cref="ToastQueue"/>
/// 用原生 InfoBar 呈现队列。宿主不随页面卸载——页面切换后未过期的 Toast 继续展示直至超时，
/// 页面级本地状态随组件卸载而残留/泄漏的问题由此消除。
///
/// 观感约定——只有两档，其余等级一律折进「正常」：
///   1. **正常**（<c>Success</c> / <c>Informational</c>）：走「修复无限加载」那一条的原生外观——
///      <see cref="InfoBarSeverity.Informational"/> 的中性底色 + 单色描边 + 原生 ⓘ 图标（系统蓝 #0078D4），
///      图标/底色/描边/内边距/尺寸一律不覆盖。
///   2. **异常**（<c>Error</c>）：保留原生 <see cref="InfoBarSeverity.Error"/> 的红底红框 + 红色 ! 图标，
///      与正常态区分开——出错需要一眼看出来，否则用户会漏读。
/// 刻意不用原生的 Success：成功与中性共用正常态，避免「同一屏三种颜色」的老问题。
/// </summary>
public sealed class ToastHost : Component
{
    public override Element Render()
    {
        // 宿主承担 Dismiss 回调，需要队列的具体类型；页面侧仍只依赖 IToastService。
        var queue = (ToastQueue)UseContext(AppHost.ToastContext);
        var (items, setItems) = UseState<IReadOnlyList<ToastEntry>>(
            Array.Empty<ToastEntry>(), threadSafe: true);

        UseEffect(
            () =>
            {
                void OnChanged(IReadOnlyList<ToastEntry> snapshot) => setItems(snapshot);
                queue.Changed += OnChanged;
                // 挂载即对齐一次：订阅之前发生的入队不会重放事件
                setItems(queue.Snapshot);
                return () => queue.Changed -= OnChanged;
            },
            0);

        if (items.Count == 0)
        {
            return Empty();
        }

        return VStack(8,
                items.Select(item => RenderBar(item, queue).WithKey(item.Id.ToString()))
                .ToArray())
            .VAlign(VerticalAlignment.Top)
            .HAlign(HorizontalAlignment.Center)
            .Margin(new Thickness(0, 48, 0, 0));
    }

    /// <summary>
    /// 渲染一条提示。只有「正常 / 异常」两档（见类型注释），不做逐等级换图标：
    /// 图标与材质都由 <see cref="InfoBarSeverity"/> 原生提供，不覆盖 <c>IconSource</c>——
    /// 自定义 FontIcon 拿不到该等级的图标色，反而会渲染成普通前景色。
    /// </summary>
    private static Element RenderBar(ToastEntry item, ToastQueue queue) =>
        (InfoBar(null, item.Message) with
        {
            Severity = item.Severity == InfoBarSeverity.Error
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Informational,
            IsOpen = true,
            // 用户点关闭：InfoBar 内部将 IsOpen 置 false 并触发 Closed，这里回删队列。
            OnClosed = () => queue.Dismiss(item.Id),
        })
        .MaxWidth(480);
}
