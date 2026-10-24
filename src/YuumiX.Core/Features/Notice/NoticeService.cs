using Microsoft.Extensions.Logging;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;
using YuumiX.Core.Updates;

namespace YuumiX.Core.Features.Notice;

/// <summary>公告内容快照。<see cref="FromCache"/> 为 true 表示本次网络拉取失败、内容来自本地缓存。</summary>
public sealed record NoticeContent(string Markdown, bool FromCache);

/// <summary>
/// 公告服务：从 GitHub 仓库拉取根目录的 <c>NOTICE.md</c>。
///
/// 对应旧 NoticePopup.vue 的 <c>fetch_github_text</c> 链路：
/// <list type="number">
///   <item>域名白名单、代理复用与 15s 超时全部继承自 <see cref="GitHubClient"/>；</item>
///   <item>对齐旧实现「打开公告即取最新」——每次调用都直接请求，不设 TTL；</item>
///   <item>请求失败时回退 <see cref="CachePath"/> 里上次成功的原文（页面据此提示「当前为缓存」），
///     连缓存都没有时异常向上抛，由页面呈现错误态与重试入口。</item>
/// </list>
/// </summary>
public sealed class NoticeService
{
    /// <summary>公告源：仓库 main 分支根目录的 NOTICE.md（raw 域名在 GitHubClient 白名单内）。</summary>
    public const string NoticeUrl =
        $"https://raw.githubusercontent.com/{GitHubClient.Repository}/main/NOTICE.md";

    private readonly RuntimePaths _paths;
    private readonly Func<AppConfig> _configProvider;
    private readonly ILogger<NoticeService>? _logger;

    public NoticeService(RuntimePaths paths, Func<AppConfig> configProvider, ILogger<NoticeService>? logger = null)
    {
        _paths = paths;
        _configProvider = configProvider;
        _logger = logger;
    }

    /// <summary>缓存文件路径：上次成功拉取的 NOTICE.md 原文，与 releases_cache.json 同目录。</summary>
    public string CachePath => Path.Combine(_paths.DataDirectory, "notice_cache.md");

    /// <summary>
    /// 获取公告。成功即写缓存；网络失败回退缓存（<see cref="NoticeContent.FromCache"/> = true）。
    /// </summary>
    /// <exception cref="InvalidOperationException">网络失败且无缓存可用时抛出（含 HTTP 状态或白名单信息）。</exception>
    public async Task<NoticeContent> GetNoticeAsync(CancellationToken token = default)
    {
        var config = _configProvider();
        try
        {
            string markdown = await GitHubClient.FetchTextAsync(
                NoticeUrl,
                config.General.EnableHttpProxy,
                config.General.HttpProxyAddr,
                token).ConfigureAwait(false);

            WriteCache(markdown);
            return new NoticeContent(markdown, FromCache: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "拉取公告失败，尝试回退本地缓存");
            string? cached = TryReadCache();
            if (cached is null)
            {
                throw;
            }

            return new NoticeContent(cached, FromCache: true);
        }
    }

    private string? TryReadCache()
    {
        try
        {
            return File.Exists(CachePath) ? File.ReadAllText(CachePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "读取公告缓存失败");
            return null;
        }
    }

    private void WriteCache(string markdown)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            string tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, markdown);
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写失败不影响本次展示，只损失下次的离线兜底。
            _logger?.LogWarning(ex, "写入公告缓存失败");
        }
    }
}
