using System.Text.Json;
using Xunit;
using YuumiX.Core.Features.GameLifecycle;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

public sealed class GameLifecycleServiceTests
{
    [Fact]
    public void PhaseTransitions_TriggerGameEndedAndNewGameStarted()
    {
        var finder = new LcuProcessFinder(new WindowsLcuProcessSource());
        using var connection = new LcuConnection(finder, 4, null);
        using var service = new GameLifecycleService(connection);

        long gameEndedTime = 0;
        long newGameTime = 0;

        service.GameEnded.Subscribe(t => gameEndedTime = t);
        service.NewGameStarted.Subscribe(t => newGameTime = t);

        // 1. 进入选人
        service.OnPhaseChanged("ChampSelect");
        Assert.True(newGameTime > 0);

        // 2. 进入游戏中
        service.OnPhaseChanged("InProgress");

        // 3. 对局结束，进入结算
        service.OnPhaseChanged("WaitingForStats");
        Assert.True(gameEndedTime > 0);

        // 4. 回到大厅
        long previousGameEnded = gameEndedTime;
        service.OnPhaseChanged("Lobby");
        // 不应重复触发
        Assert.Equal(previousGameEnded, gameEndedTime);
    }
}
