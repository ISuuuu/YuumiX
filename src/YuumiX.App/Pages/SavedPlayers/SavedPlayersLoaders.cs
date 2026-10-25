using Microsoft.UI.Reactor;
using YuumiX.App.Composition;
using YuumiX.Core;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;

namespace YuumiX.App.Pages.SavedPlayers;

/// <summary>
/// 路人集页的异步辅助（自 <c>SavedPlayersPage</c> 拆出，R14）：
/// 账号解析（UseResource fetcher）与导入/导出（UseMutation mutator）的实现。
/// </summary>
internal static class SavedPlayersLoaders
{
    /// <summary>
    /// 解析当前应展示的账号（UseResource fetcher）：优先当前登录账号，
    /// 离线或未登录则回退为最后一位登录者。连接状态变化时由页面 deps 触发重拉。
    /// </summary>
    internal static async Task<string?> ResolveSelfPuuidAsync(
        LcuConnection? lcu,
        ISummonerService? summonerService,
        SavedPlayersStore store,
        CancellationToken cancellationToken)
    {
        string? resolved = null;
        if (lcu?.CurrentState.Status == LcuConnectionStatus.Connected && summonerService is not null)
        {
            try
            {
                var me = await summonerService.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
                if (me is not null && !string.IsNullOrWhiteSpace(me.Puuid))
                {
                    resolved = me.Puuid;
                }
            }
            catch
            {
                // 忽略异常，降级到数据库回退
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = await store.GetLatestSelfPuuidAsync(cancellationToken).ConfigureAwait(false);
        }

        return resolved;
    }

    /// <summary>
    /// 导出标记玩家（UseMutation mutator）。Win32 文件对话框是阻塞调用，
    /// 包 Task.Run 卸载到线程池对齐迁移前行为（对话框跨线程安全，见 Win32FileDialog 说明）。
    /// </summary>
    internal static async Task<string> ExportAsync(
        SavedPlayersStore store,
        string? selfPuuid,
        CancellationToken cancellationToken)
    {
        var path = await Task.Run(() => Win32FileDialog.ShowSaveFileDialog("导出标记玩家", "tagged_players.json")).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var json = await store.ExportTaggedPlayersJsonAsync(selfPuuid, cancellationToken).ConfigureAwait(false);
        await System.IO.File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
        return System.IO.Path.GetFileName(path);
    }

    /// <summary>搜索防抖（effect 回调体）：输入停顿 300ms 后把草稿提交为生效词；连续键入时上一定时器被取消。</summary>
    internal static async Task CommitSearchDebouncedAsync(
        string draft,
        Action<string> applyCommitted,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        applyCommitted(draft.Trim());
    }

    /// <summary>导入标记玩家（UseMutation mutator）；取消选择返回 null。</summary>
    internal static async Task<int?> ImportAsync(
        SavedPlayersStore store,
        string? selfPuuid,
        CancellationToken cancellationToken)
    {
        var path = await Task.Run(() => Win32FileDialog.ShowOpenFileDialog("导入标记玩家")).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return null;
        }

        var json = await System.IO.File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await store.ImportTaggedPlayersJsonAsync(json, selfPuuid, cancellationToken).ConfigureAwait(false);
    }
}
