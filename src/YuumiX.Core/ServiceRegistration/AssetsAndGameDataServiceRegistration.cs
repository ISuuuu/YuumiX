using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Assets;
using YuumiX.Core.Lcu;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.ServiceRegistration;

/// <summary>
/// 资源缓存管线（LCU / CDragon）与游戏静态数据映射表注册。
/// </summary>
public static class AssetsAndGameDataServiceRegistration
{
    public static IServiceCollection AddAssetsAndGameDataServices(this IServiceCollection services)
    {
        // 缓存目录与 Rust 实现一致（<数据目录>/cache/assets），沿用同一套 FNV 哈希命名；
        // 数据目录已独立（%APPDATA%/YuumiX），不再与旧版 Yuumi 复用缓存。
        services.AddSingleton(provider => new AssetCache(
            AssetCacheDirectory(provider.GetRequiredService<RuntimePaths>()),
            TimeProvider.System,
            provider.GetRequiredService<ILogger<AssetCache>>()));
        services.AddSingleton(provider => new AssetCacheStamp(
            AssetCacheDirectory(provider.GetRequiredService<RuntimePaths>()),
            provider.GetRequiredService<ILogger<AssetCacheStamp>>()));

        // 游戏资源映射表：通过 ILcuTransportAccessor 按需取当前传输层，跨重连复用映射表
        services.AddSingleton(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new GameDataLoader(
                () => accessor.Transport,
                provider.GetRequiredService<ILogger<GameDataLoader>>());
        });
        services.AddSingleton(provider => new GameDataService(
            provider.GetRequiredService<GameDataLoader>(),
            provider.GetRequiredService<ILogger<GameDataService>>()));

        services.AddSingleton(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new LcuAssetSource(
                () => accessor.Credentials,
                logger: provider.GetRequiredService<ILogger<LcuAssetSource>>());
        });
        services.AddSingleton(provider => new CdnAssetSource(
            logger: provider.GetRequiredService<ILogger<CdnAssetSource>>()));
        services.AddSingleton(provider => new AssetService(
            provider.GetRequiredService<AssetCache>(),
            [
                provider.GetRequiredService<LcuAssetSource>(),
                provider.GetRequiredService<CdnAssetSource>(),
            ],
            AssetService.DefaultConcurrency,
            provider.GetRequiredService<ILogger<AssetService>>()));

        return services;
    }

    /// <summary>资源缓存目录：<c>&lt;数据目录&gt;/cache/assets</c>，与原实现同构。</summary>
    private static string AssetCacheDirectory(RuntimePaths paths) =>
        Path.Combine(paths.DataDirectory, "cache", "assets");
}
