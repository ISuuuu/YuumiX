namespace YuumiX.Core.Features.Opgg.Models;

/// <summary>
/// 选人锁定英雄后触发的 OP.GG 联动载荷。
/// </summary>
public sealed record OpggBuildEvent(int ChampionId, string Mode, string Position);
