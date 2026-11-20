using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Assets;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.ServiceRegistration;

/// <summary>
/// LCU 进程探测、连接管理与传输层访问器注册。
/// </summary>
public static class LcuServiceRegistration
{
    public static IServiceCollection AddLcuServices(this IServiceCollection services)
    {
        services.AddSingleton<ILcuProcessSource>(_ => new WindowsLcuProcessSource());
        services.AddSingleton(provider => new LcuProcessFinder(
            provider.GetRequiredService<ILcuProcessSource>(),
            logger: provider.GetRequiredService<ILogger<LcuProcessFinder>>()));

        services.AddSingleton(provider => new LcuConnection(
            provider.GetRequiredService<LcuProcessFinder>(),
            provider.GetRequiredService<AppConfig>().Functions.ApiConcurrencyNumber,
            provider.GetRequiredService<ILoggerFactory>(),
            cacheStamp: provider.GetRequiredService<AssetCacheStamp>()));

        services.AddSingleton<ILcuTransportAccessor>(provider => new LcuConnectionTransportAccessor(
            provider.GetRequiredService<LcuConnection>()));

        return services;
    }
}
