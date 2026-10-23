using System.Text.Json.Serialization;

namespace YuumiX.Core.Features.Tools.Models;

/// <summary>
/// 观战启动方式。
/// </summary>
public enum SpectateMethod
{
    /// <summary>
    /// CMD 方式：通过 SGP 获取凭据并直接启动 League of Legends.exe spectator（绕开 Already in gameflow，国服首选）。
    /// </summary>
    Cmd,

    /// <summary>
    /// LCU 官方接口方式（/lol-spectator/v1/spectate/launch）。
    /// </summary>
    Lcu,
}

/// <summary>
/// SGP 观战凭据（gsm/v1/ledge/spectator/region/{server}/puuid/{puuid} 响应中的 playerCredentials）。
/// </summary>
public sealed class SpectateCredentials
{
    [JsonPropertyName("observerServerIp")]
    public string ObserverServerIp { get; set; } = string.Empty;

    [JsonPropertyName("observerServerPort")]
    public int ObserverServerPort { get; set; }

    [JsonPropertyName("observerEncryptionKey")]
    public string ObserverEncryptionKey { get; set; } = string.Empty;

    [JsonPropertyName("gameId")]
    public long GameId { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;
}

/// <summary>
/// 观战操作结果。
/// </summary>
public sealed record SpectateResult(
    bool Success,
    string Message,
    SpectateMethod Method,
    SpectateCredentials? Credentials = null);
