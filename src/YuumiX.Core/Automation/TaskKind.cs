namespace YuumiX.Core.Automation;

/// <summary>
/// 自动化任务类型。
/// </summary>
public enum TaskKindType
{
    AutoPickLock,
    AutoBanLock,
    AutoSwap,
    AutoTrade,
    AutoAccept,
    AutoPlayAgain,
    AutoReconnect,
    AutoHonor,
    AutoSpell,
    AramBenchSwap,
    AutoCreateLobby,
    AramTeamSide,
    AutoEncounterRecord,
    AutoTagReminder,
    Other,
}

/// <summary>
/// 任务重复触发时的去重策略。
/// </summary>
public enum DuplicateStrategy
{
    /// <summary>替换旧任务：通知取消正在执行的同类旧任务，等待其收尾后启动新任务。</summary>
    ReplaceOld,
    /// <summary>拒绝新任务：若已有同类任务正在执行，放弃本次触发。</summary>
    RejectNew,
    /// <summary>允许并发执行。</summary>
    AllowConcurrent,
}

/// <summary>
/// 任务类型描述（支持带有自定义名称的 Other 任务类型）。
/// </summary>
public readonly record struct TaskKind(TaskKindType Type, string? CustomName = null)
{
    public static readonly TaskKind AutoPickLock = new(TaskKindType.AutoPickLock);
    public static readonly TaskKind AutoBanLock = new(TaskKindType.AutoBanLock);
    public static readonly TaskKind AutoSwap = new(TaskKindType.AutoSwap);
    public static readonly TaskKind AutoTrade = new(TaskKindType.AutoTrade);
    public static readonly TaskKind AutoAccept = new(TaskKindType.AutoAccept);
    public static readonly TaskKind AutoPlayAgain = new(TaskKindType.AutoPlayAgain);
    public static readonly TaskKind AutoReconnect = new(TaskKindType.AutoReconnect);
    public static readonly TaskKind AutoHonor = new(TaskKindType.AutoHonor);
    public static readonly TaskKind AutoSpell = new(TaskKindType.AutoSpell);
    public static readonly TaskKind AramBenchSwap = new(TaskKindType.AramBenchSwap);
    public static readonly TaskKind AutoCreateLobby = new(TaskKindType.AutoCreateLobby);
    public static readonly TaskKind AramTeamSide = new(TaskKindType.AramTeamSide);
    public static readonly TaskKind AutoEncounterRecord = new(TaskKindType.AutoEncounterRecord);
    public static readonly TaskKind AutoTagReminder = new(TaskKindType.AutoTagReminder);

    public static TaskKind Other(string name) => new(TaskKindType.Other, name);

    /// <summary>
    /// 获取该类型任务的默认去重策略。
    /// </summary>
    public DuplicateStrategy DefaultStrategy => Type switch
    {
        TaskKindType.AutoPickLock
            or TaskKindType.AutoSpell
            or TaskKindType.AutoSwap
            or TaskKindType.AutoTrade => DuplicateStrategy.ReplaceOld,

        TaskKindType.AutoBanLock
            or TaskKindType.AutoAccept
            or TaskKindType.AutoPlayAgain
            or TaskKindType.AutoReconnect
            or TaskKindType.AutoCreateLobby
            or TaskKindType.AramTeamSide
            or TaskKindType.AutoEncounterRecord
            or TaskKindType.AutoTagReminder => DuplicateStrategy.RejectNew,

        TaskKindType.AutoHonor
            or TaskKindType.AramBenchSwap
            or TaskKindType.Other => DuplicateStrategy.AllowConcurrent,

        _ => DuplicateStrategy.AllowConcurrent,
    };
}
