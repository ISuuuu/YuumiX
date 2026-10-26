using YuumiX.Core.Common;
using YuumiX.Core.Persistence;
using YuumiX.Core.Updates;

namespace YuumiX.App.Composition;

/// <summary>
/// 可订阅的「检测到可用更新」状态：侧边栏设置项红点的唯一数据源。
///
/// 为什么需要这样一个状态对象：更新检查有三个触发点（应用启动、后台周期复查、设置页手动检查），
/// 消费方却有两处且互不相识——组合根 <see cref="Shell.AppShell"/> 负责画红点，
/// 设置页负责展示版本详情与下载入口。二者共享的是「当前有没有可用更新」这一事实，
/// 而 <see cref="GeneralConfig.EnableCheckUpdate"/> 这类配置本身没有变更通知，页面之间也没有共享状态。
/// 因此沿用 ThemeState / MenuVisibilityState 的既有解法（ADR-12 决策三）：
/// 谁检测到谁发布，谁显示谁订阅。
///
/// 放在 App 而非 Core：红点与提示是纯 UI 概念，Core 不需要知道它的存在（ADR-09）。
/// </summary>
public sealed class UpdateNoticeState
{
    private readonly EventStream<UpdateNoticeState> _changes;

    public UpdateNoticeState(Action<Exception>? onObserverError = null)
    {
        _changes = new EventStream<UpdateNoticeState>(onObserverError);
    }

    /// <summary>最近一次检测到的可用更新；<c>null</c> 表示当前没有可用更新。</summary>
    public UpdateInfo? Pending { get; private set; }

    /// <summary>是否存在可用更新——红点的显示条件。</summary>
    public bool HasUpdate => Pending is not null;

    /// <summary>可用更新的版本号；无更新时为 <c>null</c>。</summary>
    public string? Version => Pending?.Version;

    /// <summary>
    /// 上次更新未完成的原因码（<see cref="UpdateService.FailureCodeWaitExit"/> 等），
    /// <c>null</c> 表示没有待提示的失败。
    ///
    /// 为什么放在这个状态对象里：它同样源自「上一次退出之后」发生的事，
    /// 生命周期与「待提示的更新」一致，都由启动时读取一次、UI 消费一次即清。
    /// 注意它**不参与订阅通知**：失败提示只在挂载时弹一条 Toast，不需要重渲染。
    /// </summary>
    public string? FailureCode { get; private set; }

    /// <summary>记录一次上次更新失败（由启动流程调用）。</summary>
    public void ReportFailure(string code)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            FailureCode = code;
        }
    }

    /// <summary>UI 已消费失败提示后清空，避免同一次渲染重复弹窗。</summary>
    public void ClearFailure() => FailureCode = null;

    /// <summary>订阅检测结果变化。返回的 <see cref="IDisposable"/> 必须由调用方在卸载时释放。</summary>
    public IDisposable Subscribe(Action<UpdateNoticeState> onChanged) => _changes.Subscribe(onChanged);

    /// <summary>
    /// 发布一次检测结果：<c>null</c> 表示无可用更新（红点熄灭）。
    ///
    /// 同一版本重复发布不做通知：启动检查与周期复查会反复命中同一个版本，
    /// 每次都推一次重渲染只会让红点的呼吸动画从头开始（观感上「闪一下」）。
    /// 版本相同但下载地址变了（例如重新打包资产）才视为新结果。
    /// </summary>
    public void Publish(UpdateInfo? info)
    {
        if (string.Equals(Pending?.Version, info?.Version, StringComparison.Ordinal)
            && string.Equals(Pending?.DownloadUrl, info?.DownloadUrl, StringComparison.Ordinal))
        {
            return;
        }

        Pending = info;
        _changes.Publish(this);
    }

    /// <summary>
    /// 清除提示。关闭「检查更新」开关时调用——功能已停，继续亮着一个不再更新的红点
    /// 会把用户引到一个「点了也没有结果」的入口。
    /// </summary>
    public void Clear() => Publish(null);
}
