namespace YuumiX.Core.Automation;

/// <summary>
/// 客户端游戏流程阶段。
/// </summary>
public enum GamePhase
{
    None,
    Lobby,
    Matchmaking,
    ReadyCheck,
    ChampSelect,
    GameStart,
    InProgress,
    PreEndOfGame,
    WaitingForStats,
    EndOfGame,
    Reconnect,
    WatchInProgress,
    Unknown,
}

public static class GamePhaseExtensions
{
    /// <summary>
    /// 是否处于活跃对局相关阶段（从确认准备到游戏结算完毕）。
    /// </summary>
    public static bool IsMatchActive(this GamePhase phase) => phase switch
    {
        GamePhase.ReadyCheck
            or GamePhase.ChampSelect
            or GamePhase.GameStart
            or GamePhase.InProgress
            or GamePhase.PreEndOfGame
            or GamePhase.WaitingForStats
            or GamePhase.EndOfGame => true,
        _ => false,
    };

    /// <summary>
    /// 是否处于局内加载或游戏进行中。
    /// </summary>
    public static bool IsInGame(this GamePhase phase) => phase switch
    {
        GamePhase.GameStart or GamePhase.InProgress => true,
        _ => false,
    };

    /// <summary>
    /// 从 LCU 阶段字符串解析为强类型枚举。
    /// </summary>
    public static GamePhase ParseGamePhase(string? str) => str switch
    {
        "None" or "" => GamePhase.None,
        "Lobby" => GamePhase.Lobby,
        "Matchmaking" => GamePhase.Matchmaking,
        "ReadyCheck" => GamePhase.ReadyCheck,
        "ChampSelect" => GamePhase.ChampSelect,
        "GameStart" => GamePhase.GameStart,
        "InProgress" => GamePhase.InProgress,
        "PreEndOfGame" => GamePhase.PreEndOfGame,
        "WaitingForStats" => GamePhase.WaitingForStats,
        "EndOfGame" => GamePhase.EndOfGame,
        "Reconnect" => GamePhase.Reconnect,
        "WatchInProgress" => GamePhase.WatchInProgress,
        _ => GamePhase.Unknown,
    };

    public static string ToPhaseString(this GamePhase phase) => phase switch
    {
        GamePhase.None => "None",
        GamePhase.Lobby => "Lobby",
        GamePhase.Matchmaking => "Matchmaking",
        GamePhase.ReadyCheck => "ReadyCheck",
        GamePhase.ChampSelect => "ChampSelect",
        GamePhase.GameStart => "GameStart",
        GamePhase.InProgress => "InProgress",
        GamePhase.PreEndOfGame => "PreEndOfGame",
        GamePhase.WaitingForStats => "WaitingForStats",
        GamePhase.EndOfGame => "EndOfGame",
        GamePhase.Reconnect => "Reconnect",
        GamePhase.WatchInProgress => "WatchInProgress",
        _ => "Unknown",
    };
}
