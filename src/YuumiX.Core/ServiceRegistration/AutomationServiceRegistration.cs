using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Automation;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.SavedPlayers;
using YuumiX.Core.Features.Summoner;
using YuumiX.Core.Lcu;
using YuumiX.Core.Persistence;

namespace YuumiX.Core.ServiceRegistration;

/// <summary>
/// 自动化代理与主控宿主注册。
/// </summary>
public static class AutomationServiceRegistration
{
    public static IServiceCollection AddAutomationServices(this IServiceCollection services)
    {
        services.AddSingleton<AgentSupervisor>(provider => new AgentSupervisor(
            logger: provider.GetService<ILogger<AgentSupervisor>>()));
        services.AddSingleton<AgentLifecycle>();

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoAcceptAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AutoAcceptAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoReconnectAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AutoReconnectAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoBanPickAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<IOpggService>(),
                provider.GetService<ILogger<AutoBanPickAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoCreateLobbyAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AutoCreateLobbyAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AramTeamSideAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AramTeamSideAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoEncounterRecorderAgent(
                () => accessor.Transport,
                provider.GetRequiredService<SavedPlayersStore>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AutoEncounterRecorderAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoTagReminderAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<SavedPlayersStore>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AutoTagReminderAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoInviteAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetService<ILogger<AutoInviteAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoHonorAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetService<ILogger<AutoHonorAgent>>());
        });

        services.AddAutomationAgent(provider =>
        {
            var accessor = provider.GetRequiredService<ILcuTransportAccessor>();
            return new AutoPlayAgainAgent(
                () => accessor.Transport,
                () => provider.GetRequiredService<AppConfig>(),
                provider.GetRequiredService<AgentSupervisor>(),
                provider.GetRequiredService<AgentLifecycle>(),
                provider.GetService<ILogger<AutoPlayAgainAgent>>());
        });

        // Live Client Data 是游戏进程的独立 HTTP 端口（127.0.0.1:2999），不走 LCU Transport。
        services.AddAutomationAgent(provider => new MultiKillCaptureAgent(
            () => provider.GetRequiredService<AppConfig>(),
            () => provider.GetService<ISummonerService>(),
            provider.GetService<ILogger<MultiKillCaptureAgent>>()));

        services.AddSingleton<AutomationHost>();
        return services;
    }

    private static IServiceCollection AddAutomationAgent<TAgent>(
        this IServiceCollection services,
        Func<IServiceProvider, TAgent> factory)
        where TAgent : class, IAutomationAgent
    {
        services.AddSingleton(factory);
        services.AddSingleton<IAutomationAgent>(provider => provider.GetRequiredService<TAgent>());
        return services;
    }
}
