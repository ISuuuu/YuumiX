using YuumiX.Core.Features.Tools.Models;

namespace YuumiX.Core.Features.Tools;

/// <summary>
/// 工具箱核心服务接口。
/// 统筹观战、皮肤拉取、个人主页定制、设置锁定、大厅创建、秒退以及客户端修复。
/// </summary>
public interface IToolsService
{
    /// <summary>启动对局观战（支持 LCU API 与 CMD 双通道）。</summary>
    Task<SpectateResult> LaunchSpectateAsync(string summonerName, SpectateMethod method = SpectateMethod.Cmd, CancellationToken cancellationToken = default);

    /// <summary>从 LCU 获取指定英雄的皮肤列表。</summary>
    Task<IReadOnlyList<ChampionSkinItem>> GetChampionSkinsAsync(int championId, CancellationToken cancellationToken = default);

    /// <summary>设置个人生涯背景皮肤。</summary>
    Task SetProfileBackgroundAsync(int skinId, CancellationToken cancellationToken = default);

    /// <summary>应用段位伪装。</summary>
    Task SetRankSpoofAsync(string queue, string tier, string division = "I", CancellationToken cancellationToken = default);

    /// <summary>设置在线状态 (chat/away/offline)。</summary>
    Task SetOnlineStatusAsync(string availability, CancellationToken cancellationToken = default);

    /// <summary>设置个性签名。</summary>
    Task SetStatusMessageAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>一键卸下展示的全部挑战勋章。</summary>
    Task ClearChallengeBadgesAsync(CancellationToken cancellationToken = default);

    /// <summary>卸下生涯头像框。</summary>
    Task ClearProfileTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>获取当前拥有的挑战勋章列表。</summary>
    Task<IReadOnlyList<ChallengeItem>> GetPlayerChallengesAsync(CancellationToken cancellationToken = default);

    /// <summary>更新当前佩戴的称号和最多 3 个挑战勋章。</summary>
    Task UpdatePlayerPreferencesAsync(string title, IReadOnlyList<long> challengeIds, CancellationToken cancellationToken = default);

    /// <summary>选人阶段秒退 (Dodge Queue)。</summary>
    Task DodgeChampSelectAsync(CancellationToken cancellationToken = default);

    /// <summary>创建指定队列的大厅房间。</summary>
    Task CreateLobbyAsync(int queueId, CancellationToken cancellationToken = default);

    /// <summary>获取游戏配置文件 PersistedSettings.json 是否为只读。</summary>
    bool GetGameSettingsReadonly(IReadOnlyList<string> lolPaths);

    /// <summary>设置游戏配置文件 PersistedSettings.json 的只读状态。</summary>
    void SetGameSettingsReadonly(IReadOnlyList<string> lolPaths, bool isReadonly);

    /// <summary>修复 LCU 客户端窗口缩放与黑屏 (Win32 API)。</summary>
    Task<string> FixClientWindowAsync(CancellationToken cancellationToken = default);
}
