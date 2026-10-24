using YuumiX.Core.Features.GameInfo.Models;

namespace YuumiX.Core.Features.GameInfo;

/// <summary>
/// 英雄联盟游戏引擎内网端口（https://127.0.0.1:2999）Live Client Data 服务。
/// 在游戏加载及进行期间（GameStart / InProgress）暴露，用于脱敏对局中补充敌方真实英雄与实名。
/// </summary>
public interface ILiveClientService
{
    /// <summary>
    /// 获取当前对局中全部玩家列表。未就绪或请求失败时返回空列表。
    /// </summary>
    Task<IReadOnlyList<LiveClientPlayer>> GetPlayerListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前本机操作的玩家召唤师名称。未就绪或请求失败时返回 <c>null</c>。
    /// </summary>
    Task<string?> GetActivePlayerNameAsync(CancellationToken cancellationToken = default);
}
