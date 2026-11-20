using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Assets;
using YuumiX.Core.Features.AramBench;
using YuumiX.Core.Features.AramData;
using YuumiX.Core.Features.Career;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Features.Match;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Replay;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Features.Tools;
using YuumiX.Core.Lcu;
using YuumiX.Core.Lcu.Sgp;
using YuumiX.Core.Persistence;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.ServiceRegistration;

/// <summary>
/// 业务领域服务注册（战绩、召唤师、生涯、工具箱、OP.GG、板凳席、对局信息等）。
/// </summary>
public static class FeaturesServiceRegistration
{
    public static IServiceCollection AddFeatureServices(this IServiceCollection services)
    {
        // ── 战绩查询与召唤师 ────────────────────────────────────────
        services.AddSingleton<ISgpService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new SgpService(
                () => accessor.Transport,
                () => accessor.Credentials,
                logger: provider.GetRequiredService<ILogger<SgpService>>());
        });

        services.AddSingleton<IMatchService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new MatchService(
                () => accessor.Transport,
                sgpService: provider.GetRequiredService<ISgpService>(),
                credentialsProvider: () => accessor.Credentials,
                logger: provider.GetRequiredService<ILogger<MatchService>>());
        });

        services.AddSingleton<ISummonerService>(provider =>
        {
            var lcu = provider.GetRequiredService<LcuConnection>();
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            var service = new SummonerService(
                () => accessor.Transport,
                logger: provider.GetRequiredService<ILogger<SummonerService>>());
            lcu.States.Subscribe(state =>
            {
                if (state.Status == LcuConnectionStatus.Disconnected)
                {
                    service.InvalidateCache();
                }
            });
            return service;
        });

        // ── 路人集 SQLite 存储 ──────────────────────────────────────
        services.AddSingleton(provider => new SavedPlayersStore(
            provider.GetRequiredService<RuntimePaths>(),
            provider.GetService<ILogger<SavedPlayersStore>>()));

        // ── 生涯（排位、战利品、熟练度与最近队友）──────────────────
        services.AddSingleton<ICareerService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new CareerService(
                () => accessor.Transport,
                savedPlayersStore: provider.GetRequiredService<SavedPlayersStore>(),
                gameDataService: provider.GetRequiredService<GameDataService>(),
                logger: provider.GetRequiredService<ILogger<CareerService>>());
        });

        // ── ROFL 对局回放服务 ───────────────────────────────────────
        services.AddSingleton<IReplayService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new ReplayService(
                () => accessor.Transport,
                logger: provider.GetRequiredService<ILogger<ReplayService>>());
        });

        // ── 搜索历史存储 ────────────────────────────────────────────
        services.AddSingleton(provider => new SearchHistoryStore(
            provider.GetRequiredService<RuntimePaths>(),
            logger: provider.GetService<ILogger<SearchHistoryStore>>()));

        // ── 对局生命周期监控服务 ────────────────────────────────────
        services.AddSingleton(provider => new GameLifecycleService(
            provider.GetRequiredService<LcuConnection>(),
            logger: provider.GetService<ILogger<GameLifecycleService>>()));

        // ── 工具箱与好友清理 ────────────────────────────────────────
        services.AddSingleton<IChampionAliasProvider>(provider => new ChampionAliasProvider(
            provider.GetRequiredService<RuntimePaths>(),
            logger: provider.GetService<ILogger<ChampionAliasProvider>>()));

        services.AddSingleton<IToolsService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new ToolsService(
                () => accessor.Transport,
                provider.GetRequiredService<ISgpService>(),
                () => accessor.Credentials,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetService<ILogger<ToolsService>>(),
                aliasProvider: provider.GetRequiredService<IChampionAliasProvider>());
        });

        services.AddSingleton<IFriendCleanerService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new FriendCleanerService(
                () => accessor.Transport,
                provider.GetService<ILogger<FriendCleanerService>>());
        });

        // ── 客户端路径检测与启动 ────────────────────────────────────
        services.AddSingleton<LolPathDetector>(new LolPathDetector(
            WindowsRegistryReader.Instance,
            WindowsProcessPathSource.Instance));
        services.AddSingleton<LolClientLauncher>(_ => new LolClientLauncher(
            WindowsProcessPathSource.Instance));
        services.AddSingleton<ClientLaunchCoordinator>(provider => new ClientLaunchCoordinator(
            provider.GetRequiredService<LolClientLauncher>(),
            provider.GetRequiredService<LcuConnection>(),
            logger: provider.GetService<ILogger<ClientLaunchCoordinator>>()));

        // ── OP.GG 英雄梯度与出装推荐 ────────────────────────────────
        services.AddSingleton(provider => new OpggHttpClient(
            () => provider.GetRequiredService<AppConfig>(),
            provider.GetRequiredService<RuntimePaths>(),
            provider.GetService<ILogger<OpggHttpClient>>()));
        services.AddSingleton<IOpggService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new OpggService(
                () => accessor.Transport,
                provider.GetRequiredService<OpggHttpClient>(),
                provider.GetService<ILogger<OpggService>>());
        });

        // ── 大乱斗板凳席悬浮窗 ──────────────────────────────────────
        services.AddSingleton<IAramBenchService>(provider =>
        {
            var connection = provider.GetRequiredService<LcuConnection>();
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AramBenchService(
                () => accessor.Transport,
                connection.Events,
                connection.States,
                provider.GetService<ILogger<AramBenchService>>());
        });

        // ── 对局信息（GameInfo 模块）────────────────────────────────
        services.AddSingleton<GameInfoSnapshotStore>(provider => new GameInfoSnapshotStore(
            provider.GetRequiredService<RuntimePaths>().GameInfoSnapshotFilePath,
            provider.GetService<ILogger<GameInfoSnapshotStore>>()));
        services.AddSingleton<ILiveClientService>(provider => new LiveClientService(
            logger: provider.GetService<ILogger<LiveClientService>>()));
        services.AddSingleton(provider => new AramDataClient(
            Path.Combine(provider.GetRequiredService<RuntimePaths>().DataDirectory, "aram-cache"),
            provider.GetRequiredService<ILogger<AramDataClient>>(),
            () => provider.GetRequiredService<AppConfig>()));
        services.AddSingleton<IMasteryService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new MasteryService(() => accessor.Transport);
        });
        services.AddSingleton<IFateService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new FateService(
                () => accessor.Transport,
                () => provider.GetService<GameDataService>()?.Current);
        });
        services.AddSingleton<IGameInfoService>(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new GameInfoService(
                () => accessor.Transport,
                provider.GetRequiredService<ISummonerService>(),
                provider.GetRequiredService<IMatchService>(),
                provider.GetRequiredService<ICareerService>(),
                provider.GetRequiredService<IMasteryService>(),
                provider.GetRequiredService<IFateService>(),
                provider.GetRequiredService<AppConfig>(),
                connection: provider.GetRequiredService<LcuConnection>(),
                logger: provider.GetRequiredService<ILogger<GameInfoService>>(),
                sgpService: provider.GetRequiredService<ISgpService>(),
                savedPlayersStore: provider.GetRequiredService<SavedPlayersStore>(),
                liveClientService: provider.GetRequiredService<ILiveClientService>(),
                catalogProvider: () => provider.GetService<GameDataService>()?.Current,
                snapshotStore: provider.GetRequiredService<GameInfoSnapshotStore>());
        });

        return services;
    }
}
