using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Notice;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;
using YuumiX.Core.Realtime;
using YuumiX.Core.Runtime;
using YuumiX.Core.Updates;
using YuumiX.Core.Upload;

namespace YuumiX.Core.ServiceRegistration;

/// <summary>
/// 云端服务（SignalR / 上传队列）、在线更新、公告与后台任务协调器注册。
/// </summary>
public static class CloudAndUpdateServiceRegistration
{
    public static IServiceCollection AddCloudAndUpdateServices(this IServiceCollection services)
    {
        // Hub 基址与上传 API 共用 General.UploadApiUrl；状态经 EventStream 推给设置页徽标。
        services.AddSingleton(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new SignalrService(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetService<ILogger<SignalrService>>());
        });

        services.AddSingleton(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new UploadService(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetService<ILogger<UploadService>>());
        });

        // 上传失败落盘 + 去重队列：构造即启动后台 worker，必须是单例。
        services.AddSingleton(provider => new PendingUploadStore(
            provider.GetRequiredService<RuntimePaths>(),
            provider.GetService<ILogger<PendingUploadStore>>()));

        services.AddSingleton(provider => new UploadQueueService(
            provider.GetRequiredService<UploadService>(),
            provider.GetRequiredService<PendingUploadStore>(),
            () => provider.GetRequiredService<AppConfig>(),
            async () => (await provider.GetRequiredService<ISummonerService>()
                .GetCurrentSummonerAsync().ConfigureAwait(false))?.Puuid,
            championNameResolver: id => ResolveChampionName(
                provider.GetRequiredService<GameDataService>(), id),
            logger: provider.GetService<ILogger<UploadQueueService>>()));

        services.AddSingleton(provider => new UploadBatchService(
            provider.GetRequiredService<UploadService>(),
            () => provider.GetRequiredService<AppConfig>(),
            championNameResolver: id => ResolveChampionName(
                provider.GetRequiredService<GameDataService>(), id),
            logger: provider.GetService<ILogger<UploadBatchService>>()));

        // 在线更新与公告
        services.AddSingleton(provider => new UpdateService(
            provider.GetRequiredService<RuntimePaths>(),
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<UpdateService>>()));

        services.AddSingleton(provider => new NoticeService(
            provider.GetRequiredService<RuntimePaths>(),
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetService<ILogger<NoticeService>>()));

        // Core 层后台任务协调器
        services.AddSingleton(provider => new AppBackgroundCoordinator(
            provider.GetRequiredService<GameLifecycleService>(),
            provider.GetRequiredService<IMatchService>(),
            provider.GetRequiredService<ISummonerService>(),
            provider.GetRequiredService<UploadQueueService>(),
            provider.GetRequiredService<UpdateService>(),
            provider.GetRequiredService<SignalrService>(),
            provider.GetService<ILogger<AppBackgroundCoordinator>>()));

        return services;
    }

    /// <summary>
    /// 英雄 ID → 名称，供上传载荷填充 <c>championName</c>。
    /// 映射表没加载出来时退化为 "Unknown"。
    /// </summary>
    private static string ResolveChampionName(GameDataService gameData, int championId) =>
        gameData.Current?.Champions.TryGetValue(championId, out string? name) == true ? name : "Unknown";
}
