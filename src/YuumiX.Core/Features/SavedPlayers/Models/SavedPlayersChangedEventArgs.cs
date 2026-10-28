namespace YuumiX.Core.Features.SavedPlayers.Models;

/// <summary>
/// 路人集变更事件类型。
/// </summary>
public enum SavedPlayersChangeKind
{
    /// <summary>对局结束批量记录相遇玩家</summary>
    EncountersRecorded,
    /// <summary>保存或更新玩家标记</summary>
    TagUpdated,
    /// <summary>删除玩家及相遇历史</summary>
    PlayerDeleted,
    /// <summary>批量导入标记玩家</summary>
    Imported,
    /// <summary>批量回填玩家身份信息</summary>
    Backfilled,
}

/// <summary>
/// 路人集数据变更事件载荷。
/// </summary>
public sealed record SavedPlayersChangedEventArgs(
    SavedPlayersChangeKind Kind,
    string? SelfPuuid = null,
    long? GameId = null);
