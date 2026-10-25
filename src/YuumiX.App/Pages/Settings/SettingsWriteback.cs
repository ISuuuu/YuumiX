using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Reactor.Core;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Pages.Settings;

/// <summary>
/// 设置页防抖写回的落地逻辑（R14 从页面抽出的纯过程部分）：
/// 定时器/卸载时机由页面的 UseEffect 驱动，这里只消费镜像 ref 里的最新草稿并写配置。
/// </summary>
internal static class SettingsWriteback
{
    /// <summary>把待提交的路径草稿写入配置（空值跳过，对齐旧 handleEditPathDirect）。</summary>
    public static void ApplyPathEdit(SettingsUi ui, Ref<Sections.PathEditUi?> pathEditRef, Action<Sections.PathEditUi?> setPathEdit)
    {
        var edit = pathEditRef.Current;
        if (edit is null)
        {
            return;
        }

        setPathEdit(null);
        var config = ui.Config;
        var value = (edit.Draft ?? string.Empty).Trim();

        if (edit.IsWeGame)
        {
            if ((config.General.WegamePath ?? string.Empty) == value)
            {
                return;
            }

            config.General.WegamePath = value.Length == 0 ? null : value;
            ui.Commit(nameof(GeneralConfig.WegamePath));
            return;
        }

        var paths = config.General.LolPath;
        if (edit.Index >= paths.Count || value.Length == 0 || paths[edit.Index] == value)
        {
            return;
        }

        paths[edit.Index] = value;
        ui.Commit(nameof(GeneralConfig.LolPath));
    }

    /// <summary>把云端文本草稿写入配置并触发 Hub 重启/停止（旧 update_config 的差异副作用）。</summary>
    public static void ApplyCloudTextEdit(
        SettingsUi ui,
        Ref<Sections.CloudTextUi> cloudTextRef,
        Action<Sections.CloudTextUi> setCloudText,
        Sections.CloudTextUi empty)
    {
        var draft = cloudTextRef.Current;
        setCloudText(empty);
        var config = ui.Config;

        if (draft.Url is not null && draft.Url.Trim() != config.General.UploadApiUrl)
        {
            config.General.UploadApiUrl = draft.Url.Trim();
            ui.Commit(nameof(GeneralConfig.UploadApiUrl));

            // 地址填好或改动的瞬间，把以前没传完的对局重新入队
            // （旧 commands/config.rs 的 trigger_upload_retry：地址变了才值得重试）
            if (!string.IsNullOrWhiteSpace(config.General.UploadApiUrl))
            {
                _ = Task.Run(() => ui.Services.Services
                    .GetRequiredService<YuumiX.Core.Upload.UploadQueueService>()
                    .TriggerPendingRetry());
            }
        }

        if (draft.UserId is not null && draft.UserId.Trim() != config.General.SignalrUserId)
        {
            config.General.SignalrUserId = draft.UserId.Trim();
            ui.Commit(nameof(GeneralConfig.SignalrUserId));
        }

        _ = ui.Services.Services.GetRequiredService<YuumiX.Core.Realtime.SignalrService>().ApplyConfigAsync();
    }

    /// <summary>
    /// 卸载兜底：防抖窗口内切走页面时，直接把待写颜色/路径/云端文本落盘
    /// （不碰 UI 状态，避免卸载后 setState；卸载时没有重渲染可依赖，草稿直接写进配置对象）。
    /// </summary>
    public static void FlushPendingEdits(
        SettingsUi ui,
        Ref<string?> pendingColorFieldRef,
        Ref<Sections.PathEditUi?> pathEditRef,
        Ref<Sections.CloudTextUi> cloudTextRef)
    {
        var config = ui.Config;
        var dirty = false;
        if (pendingColorFieldRef.Current is not null)
        {
            pendingColorFieldRef.Current = null;
            dirty = true;
        }

        if (pathEditRef.Current is { } edit)
        {
            var value = (edit.Draft ?? string.Empty).Trim();
            if (edit.IsWeGame)
            {
                config.General.WegamePath = value.Length == 0 ? null : value;
            }
            else if (value.Length > 0 && edit.Index < config.General.LolPath.Count)
            {
                config.General.LolPath[edit.Index] = value;
            }

            dirty = true;
        }

        var cloud = cloudTextRef.Current;
        if (cloud.Url is not null)
        {
            config.General.UploadApiUrl = cloud.Url.Trim();
            dirty = true;
        }

        if (cloud.UserId is not null)
        {
            config.General.SignalrUserId = cloud.UserId.Trim();
            dirty = true;
        }

        if (!dirty)
        {
            return;
        }

        try
        {
            ui.Services.ConfigStore.SaveWithBackup(config);
        }
        catch (Exception ex)
        {
            ui.Logger.LogWarning(ex, "离开设置页时补写防抖中的设置失败");
        }
    }
}
