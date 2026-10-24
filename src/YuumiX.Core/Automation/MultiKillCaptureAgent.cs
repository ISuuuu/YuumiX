using System.Net.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Parsers;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.Automation;

/// <summary>
/// Live Client Data 事件解析与截图触发判定的纯函数集合。
/// 独立成静态类的原因：事件匹配是本 agent 最容易写错的逻辑（killer 名忽略 #tag 与大小写、
/// 等级白名单、Legendary 的事件字段容错），必须能脱离网络循环被单测覆盖。
/// </summary>
public static class LiveEventMatcher
{
    /// <summary>局内事件（对应 Live Client Data <c>/liveclientdata/eventdata</c> 的 Events 数组项）。</summary>
    public sealed record LiveEvent(
        int EventId,
        string EventName,
        string? KillerName,
        string? LegendaryTo,
        string? Recipient,
        int? KillStreak);

    /// <summary>解析 eventdata 响应；载荷不是对象、没有 Events 数组时返回空列表。</summary>
    public static IReadOnlyList<LiveEvent> ParseEvents(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Events", out var events)
                || events.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<LiveEvent>();
            foreach (var item in events.EnumerateArray())
            {
                result.Add(new LiveEvent(
                    JsonRead.Int(item, "EventID", 0),
                    JsonRead.String(item, "EventName") ?? string.Empty,
                    JsonRead.String(item, "KillerName"),
                    JsonRead.String(item, "LegendaryTo"),
                    JsonRead.String(item, "Recipient"),
                    item.TryGetProperty("KillStreak", out var streak) && streak.ValueKind == JsonValueKind.Number
                        ? streak.GetInt32()
                        : null));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 比较玩家名：忽略 Riot ID 的 <c>#tag</c> 与大小写差异
    /// （接口返回的名字形态不统一，旧实现即按此容错）。
    /// </summary>
    public static bool CompareNames(string? nameA, string? nameB)
    {
        if (nameA is null || nameB is null)
        {
            return false;
        }

        static string Clean(string name) =>
            name.Split('#')[0].Trim().ToLowerInvariant();

        return Clean(nameA) == Clean(nameB);
    }

    /// <summary>
    /// 判定一个新事件是否触发截图。触发时输出文件名类型段（TripleKill/QuadraKill/PentaKill/Legendary）。
    /// </summary>
    public static bool ShouldCapture(
        LiveEvent evt,
        string playerName,
        IReadOnlyCollection<int> levels,
        out string captureReason)
    {
        captureReason = string.Empty;

        // 三/四/五杀：Multikill 事件，KillStreak ∈ 配置等级且 killer 是自己
        if (evt.EventName == "Multikill")
        {
            if (evt.KillerName is not null
                && evt.KillStreak is { } streak
                && levels.Contains(streak)
                && CompareNames(evt.KillerName, playerName))
            {
                captureReason = streak switch
                {
                    3 => "TripleKill",
                    4 => "QuadraKill",
                    5 => "PentaKill",
                    _ => $"Multikill_{streak}",
                };
                return true;
            }

            return false;
        }

        // 超神（连续击杀 8 人）：Legendary 事件，触发者字段容错（LegendaryTo → KillerName → Recipient）
        if (evt.EventName == "Legendary")
        {
            if (!levels.Contains(8))
            {
                return false;
            }

            string? triggeredPlayer = evt.LegendaryTo ?? evt.KillerName ?? evt.Recipient;
            if (triggeredPlayer is not null && CompareNames(triggeredPlayer, playerName))
            {
                captureReason = "Legendary";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 解析保存目录：配置为空时用系统图片目录下的 Yuumi_Screenshots（旧实现同款）。
    /// </summary>
    public static string ResolveSaveDirectory(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        string? pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pictures)
            ? Path.Combine(".", "Yuumi_Screenshots")
            : Path.Combine(pictures, "Yuumi_Screenshots");
    }

    /// <summary>生成截图文件名：<c>{类型}_{玩家名}_{yyyyMMdd_HHmmss}.png</c>（旧实现同款，保留 #tag 段——
    /// Windows 文件名允许 #）；仅剥离路径非法字符，避免特殊输入写盘失败。</summary>
    public static string BuildFileName(string reason, string playerName, DateTime timestamp)
    {
        string safeName = string.Join("_",
            playerName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return $"{reason}_{safeName}_{timestamp:yyyyMMdd_HHmmss}.png";
    }
}

/// <summary>
/// 多杀自动截图守护（对应旧 <c>agents/auto_screenshot.rs</c>）。
///
/// 行为对齐：
/// <list type="number">
///   <item>仅对局进行中（InProgress / Reconnect，由 <see cref="OnPhaseChanged"/> 驱动）以 1 秒间隔轮询；</item>
///   <item>轮询目标是 Live Client Data <c>https://127.0.0.1:2999</c> —— 这是游戏进程的独立 HTTP 端口，
///         证书是自签的、不走系统代理，与 LCU Transport 完全无关（任务红线 5 的例外项）；</item>
///   <item>玩家名优先取 <c>activeplayername</c>（游戏载入完成后可用），失败回退 LCU 当前召唤师；</item>
///   <item>首次拿到事件列表时把已见 EventID 初始化为最大值，避免把开局前的旧事件全部截图；</item>
///   <item>截图异步执行，绝不阻塞轮询循环。</item>
/// </list>
/// </summary>
public sealed class MultiKillCaptureAgent : IAutomationAgent
{
    private const string LiveClientDataBase = "https://127.0.0.1:2999";
    private static readonly TimeSpan InGameInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(2);

    private readonly Func<AppConfig> _configProvider;
    private readonly Func<ISummonerService?> _summonerProvider;
    private readonly ILogger<MultiKillCaptureAgent>? _logger;

    private readonly object _stateLock = new();
    private bool _isRunning;
    private volatile bool _inGame;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>截图执行器（默认 GDI BitBlt；测试可注入假实现）。</summary>
    private readonly Func<string, bool> _captureToFile;

    public string Name => "MultiKillCaptureAgent";
    public bool IsRunning => _isRunning;

    public MultiKillCaptureAgent(
        Func<AppConfig> configProvider,
        Func<ISummonerService?> summonerProvider,
        ILogger<MultiKillCaptureAgent>? logger = null,
        Func<string, bool>? captureToFile = null)
    {
        _configProvider = configProvider;
        _summonerProvider = summonerProvider;
        _logger = logger;
        _captureToFile = captureToFile ?? GdiScreenCapture.TryCaptureToFile;
    }

    /// <summary>游戏阶段变化：仅 InProgress 与 Reconnect 视为对局进行中（旧实现同款语义）。</summary>
    public void OnPhaseChanged(GamePhase phase)
    {
        _inGame = phase is GamePhase.InProgress or GamePhase.Reconnect;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                return Task.CompletedTask;
            }

            _isRunning = true;
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
        }

        _logger?.LogInformation("多杀截图守护已启动（等待对局进行中信号）");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _isRunning = false;
            _loopCts?.Cancel();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _loopCts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        long? lastEventId = null;
        string? playerName = null;

        // 独立 HTTP 客户端：自签证书 + 绕过代理 + 短超时（对齐旧实现的专用 client）
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        // 回环地址必须显式直连：系统代理会拦截 127.0.0.1 导致连接不上（与 LcuHttpTransport 同一坑）
        handler.UseProxy = false;
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(800) };

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_inGame)
                {
                    // 非游戏中：重置状态，转入 2 秒一次的挂起轮询
                    lastEventId = null;
                    playerName = null;
                    await Task.Delay(IdleInterval, token).ConfigureAwait(false);
                    continue;
                }

                // 1. 获取当前控制的玩家名（API 在游戏正式载入完成后才可用）
                playerName ??= await ResolvePlayerNameAsync(httpClient, token).ConfigureAwait(false);

                // 2. 拉取局内实时事件并匹配
                if (playerName is not null)
                {
                    (lastEventId, var captures) = await PollEventsAsync(
                        httpClient, playerName, lastEventId, token).ConfigureAwait(false);

                    foreach (var (reason, name) in captures)
                    {
                        // 异步执行截图：静默进行，绝不阻塞轮询事件循环
                        var saveDir = LiveEventMatcher.ResolveSaveDirectory(_configProvider().Functions.ScreenshotSavePath);
                        _ = Task.Run(() => CaptureAsync(saveDir, reason, name));
                    }
                }

                await Task.Delay(InGameInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "多杀截图轮询出现异常（游戏可能尚未完全载入）");
                await Task.Delay(InGameInterval, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>玩家名：Live Client Data activeplayername → LCU 当前召唤师 gameName/displayName。</summary>
    private async Task<string?> ResolvePlayerNameAsync(HttpClient httpClient, CancellationToken token)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                $"{LiveClientDataBase}/liveclientdata/activeplayername", token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                string name = JsonSerializer.Deserialize<string>(body) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _logger?.LogInformation("已获取到当前游戏内角色名 (LCD): {Name}", name);
                    return name;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 游戏可能尚未完全载入，走 LCU 兜底
        }

        try
        {
            var summoner = _summonerProvider()?.GetCurrentSummonerAsync(token);
            if (summoner is not null)
            {
                var profile = await summoner.ConfigureAwait(false);
                string? name = profile?.GameName is { Length: > 0 } gameName
                    ? gameName
                    : profile?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _logger?.LogInformation("已获取到当前游戏内角色名 (LCU Fallback): {Name}", name);
                    return name;
                }
            }
        }
        catch (Exception)
        {
            // LCU 未连接：下轮再试
        }

        return null;
    }

    /// <summary>
    /// 拉取并处理事件列表。返回新的 lastEventId 与本轮触发的截图（原因 + 玩家名）。
    /// </summary>
    internal async Task<(long? LastEventId, IReadOnlyList<(string Reason, string PlayerName)> Captures)> PollEventsAsync(
        HttpClient httpClient,
        string playerName,
        long? lastEventId,
        CancellationToken token)
    {
        using var response = await httpClient.GetAsync(
            $"{LiveClientDataBase}/liveclientdata/eventdata", token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (lastEventId, []);
        }

        string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var events = LiveEventMatcher.ParseEvents(body);
        if (events.Count == 0)
        {
            return (lastEventId, []);
        }

        // 首次拿到有效事件时初始化已见 ID，防止拉起旧数据
        long seen = lastEventId ?? events.Max(e => (long)e.EventId);

        var captures = new List<(string, string)>();
        var config = _configProvider().Functions;
        if (config.EnableScreenshotOnMultikill)
        {
            foreach (var evt in events)
            {
                if (evt.EventId <= seen)
                {
                    continue;
                }

                seen = evt.EventId;
                if (LiveEventMatcher.ShouldCapture(evt, playerName, config.ScreenshotOnMultikillLevels, out var reason))
                {
                    _logger?.LogInformation("检测到符合截图条件的事件: {Reason}，触发异步截图", reason);
                    captures.Add((reason, playerName));
                }
            }
        }
        else
        {
            // 功能关闭时也要推进已见 ID，避免开启后回放历史事件
            seen = Math.Max(seen, events.Max(e => (long)e.EventId));
        }

        return (seen, captures);
    }

    /// <summary>执行截图并保存（失败只记日志，不影响轮询）。</summary>
    private void CaptureAsync(string saveDir, string reason, string playerName)
    {
        try
        {
            Directory.CreateDirectory(saveDir);
            string fileName = LiveEventMatcher.BuildFileName(reason, playerName, DateTime.Now);
            string path = Path.Combine(saveDir, fileName);
            if (_captureToFile(path))
            {
                _logger?.LogInformation("截图保存成功: {Path}", path);
            }
            else
            {
                _logger?.LogWarning("自动截图处理失败: GDI 截图返回失败");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "自动截图处理失败");
        }
    }
}
