using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Common;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// <see cref="IToolsService"/> 的生产实现。
/// </summary>
public sealed class ToolsService : IToolsService
{
    private const string WegameMarker = "wegame";
    private readonly Func<ILcuTransport?> _transportProvider;
    private readonly Func<LcuCredentials?>? _credentialsProvider;
    private readonly ISgpService _sgpService;
    private readonly ILogger<ToolsService>? _logger;

    public ToolsService(
        Func<ILcuTransport?> transportProvider,
        ISgpService sgpService,
        Func<LcuCredentials?>? credentialsProvider = null,
        ILogger<ToolsService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transportProvider);
        ArgumentNullException.ThrowIfNull(sgpService);

        _transportProvider = transportProvider;
        _sgpService = sgpService;
        _credentialsProvider = credentialsProvider;
        _logger = logger;
    }

    private ILcuTransport GetRequiredTransport()
    {
        var transport = _transportProvider();
        if (transport is null)
        {
            throw new InvalidOperationException("LCU 客户端未连接，请先启动英雄联盟客户端");
        }
        return transport;
    }

    /// <inheritdoc />
    public async Task<SpectateResult> LaunchSpectateAsync(
        string summonerName,
        SpectateMethod method = SpectateMethod.Cmd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summonerName);
        var transport = GetRequiredTransport();
        string name = summonerName.Trim();

        // 1. 获取目标召唤师 puuid
        var summonerResp = await transport.SendAsync(
            LcuRequest.Get($"/lol-summoner/v1/summoners?name={Uri.EscapeDataString(name)}"),
            cancellationToken).ConfigureAwait(false);

        if (!summonerResp.IsSuccess)
        {
            return new SpectateResult(false, $"未找到召唤师 \"{name}\" (HTTP {summonerResp.StatusCode})", method);
        }

        using var summonerDoc = JsonDocument.Parse(summonerResp.Body);
        if (!summonerDoc.RootElement.TryGetProperty("puuid", out var puuidElem)
            || puuidElem.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(puuidElem.GetString()))
        {
            return new SpectateResult(false, $"召唤师 \"{name}\" 数据中缺少 puuid", method);
        }
        string puuid = puuidElem.GetString()!;

        // 2. 如果指定 CMD 方式
        if (method == SpectateMethod.Cmd)
        {
            return await TryLaunchCmdSpectateAsync(name, puuid, cancellationToken).ConfigureAwait(false);
        }

        // 3. LCU API 方式（失败则降级到 CMD 方式，对齐 §3.7.1）
        var directLcuResult = await TryLaunchLcuSpectateAsync(name, puuid, cancellationToken).ConfigureAwait(false);
        if (directLcuResult.Success)
        {
            return directLcuResult;
        }

        _logger?.LogWarning("LCU 观战失败 ({Error})，尝试降级到 CMD 方式", directLcuResult.Message);
        return await TryLaunchCmdSpectateAsync(name, puuid, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpectateResult> TryLaunchCmdSpectateAsync(
        string name,
        string puuid,
        CancellationToken cancellationToken)
    {
        string? server = _credentialsProvider?.Invoke()?.Server;
        if (string.IsNullOrWhiteSpace(server))
        {
            return new SpectateResult(false, "无法获取大区信息（--rso_platform_id），请重启客户端后重试", SpectateMethod.Cmd);
        }

        if (!_sgpService.IsSupported(server))
        {
            return new SpectateResult(false, $"CMD 观战仅支持腾讯大区，当前大区 {server} 不支持", SpectateMethod.Cmd);
        }

        var credentials = await _sgpService.GetSpectateCredentialsAsync(server, puuid, cancellationToken).ConfigureAwait(false);
        if (credentials is null)
        {
            return new SpectateResult(false, "该召唤师当前不在游戏中或无法获取观战凭据", SpectateMethod.Cmd);
        }

        return new SpectateResult(true, $"获取观战凭据成功（GameId: {credentials.GameId}）", SpectateMethod.Cmd, credentials);
    }

    private async Task<SpectateResult> TryLaunchLcuSpectateAsync(
        string name,
        string puuid,
        CancellationToken cancellationToken)
    {
        var transport = GetRequiredTransport();

        // 尝试从好友列表匹配 gameId
        string? dropInGameId = null;
        var friendsResp = await transport.SendAsync(LcuRequest.Get("/lol-chat/v1/friends"), cancellationToken).ConfigureAwait(false);
        if (friendsResp.IsSuccess)
        {
            try
            {
                using var friendsDoc = JsonDocument.Parse(friendsResp.Body);
                if (friendsDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in friendsDoc.RootElement.EnumerateArray())
                    {
                        if (f.TryGetProperty("puuid", out var p) && p.GetString() == puuid)
                        {
                            if (f.TryGetProperty("lol", out var lol) && lol.TryGetProperty("gameId", out var gId))
                            {
                                dropInGameId = gId.GetString();
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "解析好友列表匹配对局 ID 异常");
            }
        }

        dropInGameId ??= name;

        var body = JsonSerializer.Serialize(new
        {
            allowObserveMode = "ALL",
            dropInSpectateGameId = dropInGameId,
            gameQueueType = "",
            puuid,
        });

        var spectateResp = await transport.SendAsync(
            LcuRequest.Post("/lol-spectator/v1/spectate/launch", body),
            cancellationToken).ConfigureAwait(false);

        if (spectateResp.IsSuccess)
        {
            return new SpectateResult(true, $"LCU 观战指令已发送，正在引导客户端: {name}", SpectateMethod.Lcu);
        }

        return new SpectateResult(false, $"LCU 观战请求失败 (HTTP {spectateResp.StatusCode})", SpectateMethod.Lcu);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChampionSkinItem>> GetChampionSkinsAsync(
        int championId,
        CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var resp = await transport.SendAsync(
            LcuRequest.Get($"/lol-game-data/assets/v1/champions/{championId}.json"),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"无法获取英雄 {championId} 的皮肤数据 (HTTP {resp.StatusCode})");
        }

        using var doc = JsonDocument.Parse(resp.Body);
        if (!doc.RootElement.TryGetProperty("skins", out var skinsElem) || skinsElem.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ChampionSkinItem>();
        foreach (var s in skinsElem.EnumerateArray())
        {
            int id = s.GetProperty("id").GetInt32();
            string skinName = s.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            string loadScreenPath = s.TryGetProperty("loadScreenPath", out var lsp) && !string.IsNullOrWhiteSpace(lsp.GetString())
                ? lsp.GetString()!
                : $"/lol-game-data/assets/v1/champion-loadscreens/{championId}/{id}.jpg";
            string splashPath = s.TryGetProperty("splashPath", out var sp) && !string.IsNullOrWhiteSpace(sp.GetString())
                ? sp.GetString()!
                : $"/lol-game-data/assets/v1/champion-splashes/{championId}/{id}.jpg";

            list.Add(new ChampionSkinItem
            {
                Id = id,
                Name = skinName,
                LoadScreenPath = loadScreenPath,
                SplashPath = splashPath,
                IsBase = id % 1000 == 0,
            });
        }

        return list;
    }

    /// <inheritdoc />
    public async Task SetProfileBackgroundAsync(int skinId, CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new { backgroundSkinId = skinId });
        var resp = await transport.SendAsync(
            LcuRequest.Post("/lol-summoner/v1/current-summoner/summoner-profile", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"设置生涯背景失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task SetRankSpoofAsync(string queue, string tier, string division = "I", CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new
        {
            lol = new
            {
                rankedLeagueQueue = queue,
                rankedLeagueTier = tier,
                rankedLeagueDivision = division,
            }
        });

        var resp = await transport.SendAsync(
            LcuRequest.Put("/lol-chat/v1/me", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"设置段位伪装失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task SetOnlineStatusAsync(string availability, CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new { availability });
        var resp = await transport.SendAsync(
            LcuRequest.Put("/lol-chat/v1/me", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"设置在线状态失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task SetStatusMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new { statusMessage = message });
        var resp = await transport.SendAsync(
            LcuRequest.Put("/lol-chat/v1/me", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"设置个性签名失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task ClearChallengeBadgesAsync(CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new { challengeIds = Array.Empty<string>() });
        var resp = await transport.SendAsync(
            LcuRequest.Post("/lol-challenges/v1/update-player-preferences", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"清空挑战勋章失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task ClearProfileTokensAsync(CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new { borderToken = "" });
        var resp = await transport.SendAsync(
            LcuRequest.Post("/lol-summoner/v1/current-summoner/profile-tokens", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"卸下头像框失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChallengeItem>> GetPlayerChallengesAsync(CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var resp = await transport.SendAsync(
            LcuRequest.Get("/lol-challenges/v1/challenges/local-player"),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"获取挑战勋章列表失败 (HTTP {resp.StatusCode})");
        }

        using var doc = JsonDocument.Parse(resp.Body);
        var result = new List<ChallengeItem>();

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var elem = prop.Value;
                if (!elem.TryGetProperty("id", out var idElem)) continue;

                var item = new ChallengeItem
                {
                    Id = idElem.GetInt64(),
                    Name = elem.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                    Description = elem.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "",
                    CurrentLevel = elem.TryGetProperty("currentLevel", out var cl) ? (cl.GetString() ?? "NONE") : "NONE",
                    Category = elem.TryGetProperty("category", out var c) ? (c.GetString() ?? "") : "",
                    Percentile = elem.TryGetProperty("percentile", out var p) ? p.GetDouble() : 0,
                    Value = elem.TryGetProperty("value", out var v) ? v.GetDouble() : 0,
                };
                result.Add(item);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                if (!elem.TryGetProperty("id", out var idElem)) continue;
                var item = new ChallengeItem
                {
                    Id = idElem.GetInt64(),
                    Name = elem.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                    Description = elem.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "",
                    CurrentLevel = elem.TryGetProperty("currentLevel", out var cl) ? (cl.GetString() ?? "NONE") : "NONE",
                    Category = elem.TryGetProperty("category", out var c) ? (c.GetString() ?? "") : "",
                    Percentile = elem.TryGetProperty("percentile", out var p) ? p.GetDouble() : 0,
                    Value = elem.TryGetProperty("value", out var v) ? v.GetDouble() : 0,
                };
                result.Add(item);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task UpdatePlayerPreferencesAsync(
        string title,
        IReadOnlyList<long> challengeIds,
        CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        string? bannerAccent = null;

        // 尝试保留当前的横幅样式
        var meResp = await transport.SendAsync(LcuRequest.Get("/lol-chat/v1/me"), cancellationToken).ConfigureAwait(false);
        if (meResp.IsSuccess)
        {
            try
            {
                using var meDoc = JsonDocument.Parse(meResp.Body);
                if (meDoc.RootElement.TryGetProperty("lol", out var lol)
                    && lol.TryGetProperty("bannerIdSelected", out var banner)
                    && banner.ValueKind == JsonValueKind.String)
                {
                    bannerAccent = banner.GetString();
                }
            }
            catch
            {
                // 忽略非关键错误
            }
        }

        var bodyObj = new Dictionary<string, object>
        {
            ["title"] = title ?? "",
            ["challengeIds"] = challengeIds.Select(id => id.ToString()).ToList(),
        };

        if (!string.IsNullOrWhiteSpace(bannerAccent))
        {
            bodyObj["bannerAccent"] = bannerAccent;
        }

        var json = JsonSerializer.Serialize(bodyObj);
        var resp = await transport.SendAsync(
            LcuRequest.Post("/lol-challenges/v1/update-player-preferences", json),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess)
        {
            throw new InvalidOperationException($"装配勋章与称号失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public async Task DodgeChampSelectAsync(CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();

        // 1. 校验当前 phase
        var phaseResp = await transport.SendAsync(
            LcuRequest.Get("/lol-gameflow/v1/gameflow-phase"),
            cancellationToken).ConfigureAwait(false);

        if (!phaseResp.IsSuccess || !string.Equals(phaseResp.Body.Trim('"', ' '), "ChampSelect", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("仅在选人阶段支持秒退");
        }

        // 2. 尝试端点 1: 标准 dodge 端点
        var dodgeBody = JsonSerializer.Serialize(new
        {
            dodgeIds = Array.Empty<string>(),
            phase = "ChampSelect",
            state = "PartyDodged",
        });

        var r1 = await transport.SendAsync(LcuRequest.Post("/lol-gameflow/v1/session/dodge", dodgeBody), cancellationToken).ConfigureAwait(false);
        if (r1.IsSuccess) return;

        // 尝试端点 2: quit 端点
        var r2 = await transport.SendAsync(LcuRequest.Post("/lol-lobby-team-builder/champ-select/v1/session/quit", "{}"), cancellationToken).ConfigureAwait(false);
        if (r2.IsSuccess) return;

        // 尝试端点 3: lcdsServiceProxy
        const string lcdsPath = "/lol-login/v1/session/invoke?destination=lcdsServiceProxy&method=call&args=%5B%22%22%2C%22teambuilder-draft%22%2C%22quitV2%22%2C%22%22%5D";
        var r3 = await transport.SendAsync(LcuRequest.Post(lcdsPath, "{}"), cancellationToken).ConfigureAwait(false);
        if (r3.IsSuccess) return;

        throw new InvalidOperationException($"秒退请求失败 (HTTP {r1.StatusCode})");
    }

    /// <inheritdoc />
    public async Task CreateLobbyAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var body = JsonSerializer.Serialize(new { queueId });
        var resp = await transport.SendAsync(
            LcuRequest.Post("/lol-lobby/v2/lobby", body),
            cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccess && resp.StatusCode != 409)
        {
            throw new InvalidOperationException($"创建大厅失败 (HTTP {resp.StatusCode})");
        }
    }

    /// <inheritdoc />
    public bool GetGameSettingsReadonly(IReadOnlyList<string> lolPaths)
    {
        string? settingsFile = ResolvePersistedSettingsPath(lolPaths);
        if (settingsFile is null || !File.Exists(settingsFile))
        {
            return false;
        }

        var info = new FileInfo(settingsFile);
        return info.IsReadOnly;
    }

    /// <inheritdoc />
    public void SetGameSettingsReadonly(IReadOnlyList<string> lolPaths, bool isReadonly)
    {
        string? settingsFile = ResolvePersistedSettingsPath(lolPaths);
        if (settingsFile is null || !File.Exists(settingsFile))
        {
            throw new FileNotFoundException("游戏配置文件 PersistedSettings.json 不存在，请先登录一次游戏以自动生成该文件");
        }

        var info = new FileInfo(settingsFile)
        {
            IsReadOnly = isReadonly,
        };
    }

    private static string? ResolvePersistedSettingsPath(IReadOnlyList<string> lolPaths)
    {
        if (lolPaths is null || lolPaths.Count == 0) return null;

        string? realPath = lolPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && !string.Equals(p.Trim(), WegameMarker, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(realPath)) return null;

        string basePath = File.Exists(realPath) ? (Path.GetDirectoryName(realPath) ?? realPath) : realPath;
        return Path.Combine(basePath, "Game", "Config", "PersistedSettings.json");
    }

    /// <inheritdoc />
    public async Task<string> FixClientWindowAsync(CancellationToken cancellationToken = default)
    {
        var transport = GetRequiredTransport();
        var zoomResp = await transport.SendAsync(
            LcuRequest.Get("/riotclient/zoom-scale"),
            cancellationToken).ConfigureAwait(false);

        double zoom = 1.0;
        if (zoomResp.IsSuccess && double.TryParse(zoomResp.Body, out var parsedZoom))
        {
            zoom = parsedZoom;
        }

        if (!OperatingSystem.IsWindows())
        {
            return $"当前平台不支持 Win32 窗口修复 (zoom={zoom})";
        }

        uint targetPid = _credentialsProvider?.Invoke()?.ProcessId ?? 0;
        FixWindowWin32(targetPid);
        return $"窗口已修复 (zoom={zoom})";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void FixWindowWin32(uint targetPid)
    {
        IntPtr hwnd = FindWindowW("RiotWindow", null);

        if (hwnd == IntPtr.Zero && targetPid != 0)
        {
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == targetPid)
                {
                    hwnd = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0040); // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_SHOWWINDOW
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
