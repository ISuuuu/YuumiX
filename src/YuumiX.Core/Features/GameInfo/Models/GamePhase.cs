namespace YuumiX.Core.Features.GameInfo.Models;

/// <summary>
/// LCU Gameflow Phase 枚举，对应 <c>/lol-gameflow/v1/gameflow-phase</c>。
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
    WaitingForStats,
    PreEndOfGame,
    EndOfGame,
    Reconnect,
}

public static class GamePhaseExtensions
{
    public static GamePhase ParseGamePhase(string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return GamePhase.None;
        }

        return phase.Trim() switch
        {
            "Lobby" => GamePhase.Lobby,
            "Matchmaking" => GamePhase.Matchmaking,
            "ReadyCheck" => GamePhase.ReadyCheck,
            "ChampSelect" => GamePhase.ChampSelect,
            "GameStart" => GamePhase.GameStart,
            "InProgress" => GamePhase.InProgress,
            "WaitingForStats" => GamePhase.WaitingForStats,
            "PreEndOfGame" => GamePhase.PreEndOfGame,
            "EndOfGame" => GamePhase.EndOfGame,
            "Reconnect" => GamePhase.Reconnect,
            _ => GamePhase.None,
        };
    }

    public static bool IsGameActive(this GamePhase phase) =>
        phase is GamePhase.ChampSelect or GamePhase.GameStart or GamePhase.InProgress;
}
