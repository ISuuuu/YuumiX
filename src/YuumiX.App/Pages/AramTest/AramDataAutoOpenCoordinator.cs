using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using YuumiX.App.Composition;
using YuumiX.App.Windowing;
using YuumiX.Core.Features.AramData;
using YuumiX.Core.Features.GameInfo;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Pages.AramTest;

/// <summary>
/// 选人/对局自动弹窗与启动后台版本检测协调器：
/// 1) 应用启动时自动检测 myWebAPI 是否有新版本，有则静默全量更新到本地缓存；
/// 2) 监听 <see cref="IGameInfoService.StateStream"/>，选定英雄时自动在主窗体右侧弹出窗口。
/// </summary>
public sealed class AramDataAutoOpenCoordinator : Component
{
    public override Element Render()
    {
        var services = UseContext(AppHost.Context) ?? AppHost.Services;
        var gameInfo = services.Services.GetRequiredService<IGameInfoService>();
        var opggService = services.Services.GetRequiredService<IOpggService>();
        var aramClient = services.Services.GetRequiredService<AramDataClient>();
        var logger = UseMemo(() => services.LoggerFactory.CreateLogger<AramDataAutoOpenCoordinator>(), 0);

        var (state, setState) = UseState(gameInfo.CurrentState, threadSafe: true);
        var (opggChampId, setOpggChampId) = UseState(0, threadSafe: true);
        var opggChampRef = UseRef(0);
        opggChampRef.Current = opggChampId;
        var lastChampRef = UseRef(0);
        var openedForMatchRef = UseRef(false);

        // 启动时静默检查一次远端 dataVersion，有新版本则自动拉取到本地缓存
        UseEffect(
            () =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                if (dispatcher != null)
                {
                    var hotkey = services.Config.General.AramDataHotkey;
                    AramHotkeyMonitor.Start(dispatcher, hotkey, () =>
                    {
                        var cur = gameInfo.CurrentState;
                        if (!string.IsNullOrEmpty(cur.CurrentSummonerPuuid))
                        {
                            var me = cur.MyTeam.FirstOrDefault(p =>
                                string.Equals(p.Puuid, cur.CurrentSummonerPuuid, StringComparison.OrdinalIgnoreCase));
                            if (me is { ChampionId: > 0 }) return me.ChampionId;
                        }
                        return opggChampRef.Current > 0 ? opggChampRef.Current : null;
                    });
                }

                var defaultUrl = !string.IsNullOrWhiteSpace(services.Config.General.AramDataApiUrl)
                    ? services.Config.General.AramDataApiUrl.Trim()
                    : "https://lol.ikim.nyc.mn/";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await aramClient.CheckAndSyncAsync(defaultUrl, force: false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[AramData] 启动后台版本检测跳过（服务端可能未启动）");
                    }
                });

                var sub = gameInfo.StateStream.Subscribe(setState);
                var opggSub = opggService.ChampSelectBuildEvents.Subscribe(new ActionObserver<OpggBuildEvent>(ev =>
                {
                    if (ev.ChampionId > 0)
                    {
                        setOpggChampId(ev.ChampionId);
                    }
                }));
                return () =>
                {
                    sub.Dispose();
                    opggSub.Dispose();
                };
            },
            0);

        var inMatchOrSelect = state.Phase is GamePhase.ChampSelect or GamePhase.GameStart or GamePhase.InProgress;
        var isMayhem = state.CurrentQueueId == 2400;
        var myChampId = 0;
        if (inMatchOrSelect && isMayhem && !state.IsDemoData)
        {
            var me = !string.IsNullOrEmpty(state.CurrentSummonerPuuid)
                ? state.MyTeam.FirstOrDefault(p => string.Equals(p.Puuid, state.CurrentSummonerPuuid, StringComparison.OrdinalIgnoreCase))
                : null;
            myChampId = me?.ChampionId ?? 0;
            if (myChampId <= 0 && opggChampId > 0)
            {
                myChampId = opggChampId;
            }
            else if (opggChampId > 0 && state.Phase == GamePhase.ChampSelect)
            {
                // 选人阶段 AutoBanPickAgent / current-champion 事件更实时，优先采用最新选人英雄
                myChampId = opggChampId;
            }
        }

        // 组合状态 key：未进入海克斯大乱斗时为 -1；进入但未选英雄时为 0；选定英雄后为 championId
        var triggerKey = (inMatchOrSelect && isMayhem && !state.IsDemoData) ? myChampId : -1;

        UseEffect(
            () =>
            {
                if (triggerKey < 0)
                {
                    openedForMatchRef.Current = false;
                    lastChampRef.Current = 0;
                    if (opggChampId != 0)
                    {
                        setOpggChampId(0);
                    }
                    return static () => { };
                }

                if (!openedForMatchRef.Current)
                {
                    openedForMatchRef.Current = true;
                    if (myChampId > 0)
                    {
                        lastChampRef.Current = myChampId;
                    }
                    logger.LogInformation("[AramData] 海克斯大乱斗自动弹出窗口 phase={Phase}, champId={ChampId}", state.Phase, myChampId);
                    AramDataWindowHost.ShowOrActivate(myChampId > 0 ? myChampId : null);
                }
                else if (myChampId > 0 && myChampId != lastChampRef.Current)
                {
                    lastChampRef.Current = myChampId;
                    logger.LogInformation("[AramData] 海克斯大乱斗选人联动跳转 champId={ChampId}", myChampId);
                    AramDataWindowHost.ShowOrActivate(myChampId);
                }

                return static () => { };
            },
            triggerKey);

        return Empty();
    }

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => onNext(value);
    }
}
