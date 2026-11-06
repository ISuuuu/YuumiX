using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.App.Pages.Search;
using YuumiX.Core.Features.Match.Models;
using Xunit;

namespace YuumiX.App.Tests;

public sealed class SearchMotionTests
{
    [Fact]
    public async Task Detail_FirstResultAndEnrichment_UseProductionFadeWithStableIdentity()
    {
        using var scope = new ResourceScope();
        var pending = new TaskCompletionSource<MatchDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = Body("对局详情");
        var loading = scope.UseRequest(_ => pending.Task, 10001L);
        Assert.Same(content, SearchMotion.Detail(content, false, 10001L, loading));

        pending.SetResult(Detail(10001L));
        await scope.Dispatcher.RunNextAsync();
        var resource = scope.UseRequest(_ => pending.Task, 10001L);
        var first = SearchMotion.Detail(content, false, 10001L, resource);
        AssertFade(first, "detailFadeIn", 10001L);
        Assert.Equal(content.Content, Assert.IsType<TextBlockElement>(first).Content);

        // 补充字段导致内容变化，但仍向 Reactor 提交相同的动画名称和 trigger。
        var enriched = SearchMotion.Detail(Body("已补充段位和隐私"), false, 10001L, resource);
        Assert.Equal(Animation(first).Name, Animation(enriched).Name);
        Assert.Equal(Animation(first).Trigger, Animation(enriched).Trigger);
    }

    [Fact]
    public async Task List_SilentRefresh_KeepsPreviousPageAndProductionTrigger()
    {
        using var scope = new ResourceScope();
        string key = SearchEffects.HistoryCacheKey("player", 0, 1);
        var firstRequest = new TaskCompletionSource<SearchHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = Body("战绩列表");
        var loading = scope.UseRequest(_ => firstRequest.Task, "player", 0, 0, 1);
        Assert.Null(SearchEffects.CurrentHistoryPage(loading, key));
        Assert.Same(content, SearchMotion.List(content, false, 0, key));

        var firstResult = new SearchHistoryResult(key, Page(10001L));
        firstRequest.SetResult(firstResult);
        await scope.Dispatcher.RunNextAsync();
        var firstResource = scope.UseRequest(_ => firstRequest.Task, "player", 0, 0, 1);
        var firstPage = SearchEffects.CurrentHistoryPage(firstResource, key);
        Assert.NotNull(firstPage);
        var first = SearchMotion.List(content, false, firstPage.Games.Count, key);
        AssertFade(first, "listFadeIn", key);

        var refresh = new TaskCompletionSource<SearchHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = scope.UseRequest(_ => refresh.Task, "player", 0, 1, 1);
        var retained = SearchEffects.CurrentHistoryPage(refreshing, key, firstResult);
        Assert.Same(firstPage, retained);
        Assert.NotNull(retained);
        var duringRefresh = SearchMotion.List(content, false, retained.Games.Count, key);
        Assert.Equal(Animation(first).Trigger, Animation(duringRefresh).Trigger);

        refresh.SetResult(new SearchHistoryResult(key, Page(10002L)));
        await scope.Dispatcher.RunNextAsync();
        var refreshed = scope.UseRequest(_ => refresh.Task, "player", 0, 1, 1);
        var refreshedPage = SearchEffects.CurrentHistoryPage(refreshed, key);
        Assert.NotNull(refreshedPage);
        Assert.Equal(10002L, Assert.Single(refreshedPage.Games).GameId);
        var afterRefresh = SearchMotion.List(content, false, refreshedPage.Games.Count, key);
        Assert.Equal(Animation(first).Name, Animation(afterRefresh).Name);
        Assert.Equal(Animation(first).Trigger, Animation(afterRefresh).Trigger);
    }

    [Theory]
    [InlineData("other-player", 0)]
    [InlineData("player", 1)]
    public async Task List_TargetOrPageChange_DropsOldResponseAndUsesLatestIdentity(string target, int pageIndex)
    {
        using var scope = new ResourceScope();
        string oldKey = SearchEffects.HistoryCacheKey("player", 0, 1);
        string newKey = SearchEffects.HistoryCacheKey(target, pageIndex, 1);
        var oldRequest = new TaskCompletionSource<SearchHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRequest = new TaskCompletionSource<SearchHistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        scope.UseRequest(ct => { oldToken = ct; return oldRequest.Task; }, "player", 0, 0, 1);
        var loading = scope.UseRequest(_ => newRequest.Task, target, pageIndex, 0, 1);
        Assert.True(oldToken.IsCancellationRequested);
        var previous = new SearchHistoryResult(oldKey, Page(10001L));
        Assert.Null(SearchEffects.CurrentHistoryPage(loading, newKey, previous));

        newRequest.SetResult(new SearchHistoryResult(newKey, Page(10002L)));
        await scope.Dispatcher.RunNextAsync();
        var newest = scope.UseRequest(_ => newRequest.Task, target, pageIndex, 0, 1);
        var page = SearchEffects.CurrentHistoryPage(newest, newKey, previous);
        Assert.NotNull(page);
        AssertFade(SearchMotion.List(Body("新战绩"), false, page.Games.Count, newKey), "listFadeIn", newKey);

        int renderRequests = scope.RenderRequests;
        oldRequest.SetResult(previous);
        await scope.Dispatcher.RunNextAsync();
        Assert.Equal(renderRequests, scope.RenderRequests);
        Assert.Same(newest, scope.UseRequest(_ => newRequest.Task, target, pageIndex, 0, 1));
    }

    [Fact]
    public async Task Detail_RapidSelection_DropsCancelledLateResponse()
    {
        using var scope = new ResourceScope();
        var oldRequest = new TaskCompletionSource<MatchDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRequest = new TaskCompletionSource<MatchDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var oldLoading = scope.UseRequest(ct => { oldToken = ct; return oldRequest.Task; }, 10001L);
        Assert.Null(SearchMotion.Detail(Body("旧对局"), false, 10001L, oldLoading).KeyframeAnimations);
        var newLoading = scope.UseRequest(_ => newRequest.Task, 10002L);
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Null(SearchMotion.Detail(Body("新对局"), false, 10002L, newLoading).KeyframeAnimations);

        newRequest.SetResult(Detail(10002L));
        await scope.Dispatcher.RunNextAsync();
        var newest = scope.UseRequest(_ => newRequest.Task, 10002L);
        AssertFade(SearchMotion.Detail(Body("新对局"), false, 10002L, newest), "detailFadeIn", 10002L);

        // 故意让服务忽略取消并晚返回，验证真实 UseResource 的旧响应隔离。
        int renderRequests = scope.RenderRequests;
        oldRequest.SetResult(Detail(10001L));
        await scope.Dispatcher.RunNextAsync();
        Assert.Equal(renderRequests, scope.RenderRequests);
        var afterLateResponse = scope.UseRequest(_ => newRequest.Task, 10002L);
        Assert.Same(newest, afterLateResponse);
        AssertFade(SearchMotion.Detail(Body("新对局"), false, 10002L, afterLateResponse), "detailFadeIn", 10002L);
    }

    [Fact]
    public async Task Detail_Unmount_CancelsRequestAndPreventsLateRender()
    {
        using var scope = new ResourceScope();
        var pending = new TaskCompletionSource<MatchDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var loading = scope.UseRequest(ct => { token = ct; return pending.Task; }, 10001L);
        Assert.Null(SearchMotion.Detail(Body("详情"), false, 10001L, loading).KeyframeAnimations);

        scope.Unmount();
        Assert.True(token.IsCancellationRequested);
        int renderRequests = scope.RenderRequests;
        pending.SetResult(Detail(10001L));
        await scope.Dispatcher.RunNextAsync();
        Assert.Equal(renderRequests, scope.RenderRequests);
    }

    [Fact]
    public async Task Detail_FailedRequest_DoesNotBuildSuccessAnimation()
    {
        using var scope = new ResourceScope();
        var pending = new TaskCompletionSource<MatchDetail?>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.UseRequest(_ => pending.Task, 10001L);
        var failure = new InvalidOperationException("查询失败");
        pending.SetException(failure);
        await scope.Dispatcher.RunNextAsync();
        var resource = scope.UseRequest(_ => pending.Task, 10001L);
        Assert.Same(failure, Assert.IsType<AsyncValue<MatchDetail?>.Error>(resource).Exception);
        var content = Body("重试");
        Assert.Same(content, SearchMotion.Detail(content, false, 10001L, resource));
    }

    [Fact]
    public void Detail_SynchronousResult_DoesNotForceLoadingOrDelay()
    {
        using var scope = new ResourceScope();
        var resource = scope.UseRequest(_ => Task.FromResult<MatchDetail?>(Detail(10001L)), 10001L);
        Assert.IsType<AsyncValue<MatchDetail?>.Data>(resource);
        AssertFade(SearchMotion.Detail(Body("详情"), false, 10001L, resource), "detailFadeIn", 10001L);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReducedMotionOrHighContrast_PreservesContentWithoutAnimation(bool reducedMotion, bool highContrast)
    {
        bool skipMotion = reducedMotion || highContrast;
        var content = Body("结果");
        var resource = new AsyncValue<MatchDetail?>.Data(Detail(10001L));
        Assert.Same(content, SearchMotion.List(content, skipMotion, 1, "history"));
        Assert.Same(content, SearchMotion.Detail(content, skipMotion, 10001L, resource));
        Assert.Null(content.KeyframeAnimations);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Detail_WithoutSelection_PreservesContent(long gameId)
    {
        var content = Body("未选择对局");
        Assert.Same(content, SearchMotion.Detail(content, false, gameId, new AsyncValue<MatchDetail?>.Data(Detail(10001L))));
        Assert.Same(content, SearchMotion.Detail(content, false, 10001L, new AsyncValue<MatchDetail?>.Data(null)));
    }

    private static KeyframeEntry Animation(Element element)
    {
        Assert.NotNull(element.KeyframeAnimations);
        return Assert.Single(element.KeyframeAnimations);
    }

    private static void AssertFade(Element element, string name, object trigger)
    {
        var animation = Animation(element);
        Assert.Equal(name, animation.Name);
        Assert.Equal(trigger, animation.Trigger);
        Assert.Equal(TimeSpan.FromMilliseconds(150), animation.Definition.Duration);
        Assert.False(animation.Definition.Loop);
        var frames = animation.Definition.Keyframes;
        Assert.Equal(2, frames.Length);
        Assert.Equal(0f, frames[0].Progress);
        Assert.Equal(0f, frames[0].Opacity);
        Assert.Equal(1f, frames[1].Progress);
        Assert.Equal(1f, frames[1].Opacity);
        Assert.All(frames, frame =>
        {
            Assert.Null(frame.Scale);
            Assert.Null(frame.Translation);
            Assert.Null(frame.Rotation);
        });
    }

    private static MatchDetail Detail(long gameId) => new()
    {
        GameId = gameId, QueueId = 420, MapId = 11, QueueName = "排位", MapName = "峡谷",
        GameCreation = 0, DurationSeconds = 1200,
        Blue = new MatchDetailTeam { TeamId = 100, Players = [], Kills = 0, Win = true },
        Red = new MatchDetailTeam { TeamId = 200, Players = [], Kills = 0, Win = false },
    };

    private static MatchHistoryPage Page(long gameId) => MatchHistoryPage.Empty with
    {
        Games = (MatchSummary[])[new MatchSummary
        {
            GameId = gameId, QueueId = 420, QueueName = "排位", MapName = "峡谷", GameCreation = 0,
            DurationSeconds = 1200, Win = true, Remake = false, ChampionId = 1, Spell1Id = 4, Spell2Id = 14,
            ChampionLevel = 18, Kills = 1, Deaths = 1, Assists = 1, ItemIds = [], RuneId = 0,
            CreepScore = 0, Gold = 0, DamageToChampions = 0, TotalHeal = 0, AugmentIds = [],
        }],
    };

    private sealed class ResourceScope : IDisposable
    {
        // Reactor 的无窗口渲染生命周期是 internal；仅测试绑定这些入口，避免复制其取消和缓存实现。
        private static readonly Action<RenderContext, Action> BeginRender = Method("BeginRender", typeof(Action))
            .CreateDelegate<Action<RenderContext, Action>>();
        private static readonly Action<RenderContext> FlushEffects = Method("FlushEffects")
            .CreateDelegate<Action<RenderContext>>();
        private static readonly Action<RenderContext> RunCleanups = Method("RunCleanups")
            .CreateDelegate<Action<RenderContext>>();
        private readonly RenderContext _context = new();
        private readonly QueryCache _cache = new();

        internal QueuedDispatcher Dispatcher { get; } = new();
        internal int RenderRequests { get; private set; }

        internal AsyncValue<T> UseRequest<T>(Func<CancellationToken, Task<T>> fetcher, params object[] dependencies)
        {
            BeginRender(_context, () => RenderRequests++);
            var resource = _context.UseResource(fetcher, _cache, dependencies, dispatcher: Dispatcher);
            FlushEffects(_context);
            return resource;
        }

        internal void Unmount() => RunCleanups(_context);

        public void Dispose()
        {
            Unmount();
            _cache.Dispose();
            Dispatcher.Dispose();
        }

        private static MethodInfo Method(string name, params Type[] parameters) =>
            typeof(RenderContext).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameters, null)
            ?? throw new InvalidOperationException($"当前 Reactor 缺少测试生命周期入口：{name}");
    }

    private sealed class QueuedDispatcher : IHookDispatcher, IDisposable
    {
        private readonly ConcurrentQueue<Action> _actions = new();
        private readonly SemaphoreSlim _ready = new(0);

        public void Post(Action action)
        {
            _actions.Enqueue(action);
            _ready.Release();
        }

        internal async Task RunNextAsync()
        {
            Assert.True(await _ready.WaitAsync(TimeSpan.FromSeconds(5)), "资源完成回调未到达");
            if (!_actions.TryDequeue(out var action))
                throw new InvalidOperationException("已通知的资源回调不在队列中");
            action();
        }

        public void Dispose() => _ready.Dispose();
    }
}
