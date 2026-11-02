using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using static Microsoft.UI.Reactor.Factories;
using YuumiX.Core.Features.Match.Models;
using YuumiX.Core.Features.Summoner.Models;
using YuumiX.Core.Localization;
using Xunit;
using YuumiX.App.Pages.Search;
using YuumiX.App.Theming;

namespace YuumiX.App.Tests;

public sealed class SearchRegressionTests
{
    [Fact]
    public void ResourceState_SeparatesHistoryFailureFromSuccessfulEmptyPage()
    {
        var i18n = new I18nService();
        var failed = SearchChrome.ResourceState(new AsyncValue<SearchHistoryResult>.Error(new Exception()), false, false, YuumiTheme.Default, () => { }, i18n);
        var empty = SearchChrome.ResourceState(new AsyncValue<SearchHistoryResult>.Data(new SearchHistoryResult("key", MatchHistoryPage.Empty)), true, false, YuumiTheme.Default, () => { }, i18n);
        Assert.IsNotType<BorderElement>(failed);
        var border = Assert.IsAssignableFrom<BorderElement>(empty);
        Assert.Equal("暂无战绩", Assert.IsAssignableFrom<TextBlockElement>(border.Child).Content);
    }

    [Fact]
    public void ResourceState_LoadingDoesNotDisplayEmptyResult()
    {
        var element = SearchChrome.ResourceState(
            new AsyncValue<SearchHistoryResult>.Loading(), false, false, YuumiTheme.Default, () => { }, new I18nService());
        Assert.IsAssignableFrom<GridElement>(element);
    }

    [Fact]
    public void SearchGuideEmpty_RendersSearchGuideWithHistoryChips()
    {
        var i18n = new I18nService();
        var selectedItem = string.Empty;
        var element = SearchChrome.SearchGuideEmpty(
            ["Player1#123", "Player2#456"],
            item => selectedItem = item,
            i18n);

        var grid = Assert.IsAssignableFrom<GridElement>(element);
        Assert.Equal(2, grid.Children.Length);

        // 左边栏：历史记录面板（直接平铺，无外框滚动条）
        var leftStack = Assert.IsAssignableFrom<StackElement>(grid.Children[0]);
        Assert.Equal(2, leftStack.Children.Length);
        var vstack = Assert.IsAssignableFrom<StackElement>(leftStack.Children[1]);
        Assert.Equal(2, vstack.Children.Length);

        // 右边栏：引导与技巧面板
        var rightBorder = Assert.IsAssignableFrom<BorderElement>(grid.Children[1]);
        var rightStack = Assert.IsAssignableFrom<StackElement>(rightBorder.Child);
        Assert.True(rightStack.Children.Length >= 3);
    }

    [Fact]
    public void SearchGuideEmpty_WithRemoveAction_PassesRemoveCallbackToChips()
    {
        var i18n = new I18nService();
        var selectedItem = string.Empty;
        var removedItem = string.Empty;
        var element = SearchChrome.SearchGuideEmpty(
            ["Player1#123", "Player2#456"],
            item => selectedItem = item,
            item => removedItem = item,
            i18n);

        var grid = Assert.IsAssignableFrom<GridElement>(element);
        Assert.Equal(2, grid.Children.Length);

        // 验证左边栏历史卡片组件属性传递与回调（无 ScrollViewer 滚动条）
        var leftStack = Assert.IsAssignableFrom<StackElement>(grid.Children[0]);
        var vstack = Assert.IsAssignableFrom<StackElement>(leftStack.Children[1]);

        var chipElement = Assert.IsAssignableFrom<ComponentElement<SearchHistoryChipProps>>(vstack.Children[0]);
        Assert.Equal("Player1#123", chipElement.Props.Item);
        chipElement.Props.OnSelect("Player1#123");
        Assert.Equal("Player1#123", selectedItem);
        chipElement.Props.OnRemove?.Invoke("Player1#123");
        Assert.Equal("Player1#123", removedItem);
    }

    [Fact]
    public void HistoryResource_UsesPreviousPageDuringReload_AndKeysSeparateTargetsAndPages()
    {
        var previous = MatchHistoryPage.Empty;
        var reloading = new AsyncValue<SearchHistoryResult>.Reloading(new SearchHistoryResult("one", previous));
        Assert.Same(previous, SearchEffects.CurrentHistoryPage(reloading, "one"));
        Assert.Null(SearchEffects.CurrentHistoryPage(reloading, "two"));
        Assert.Null(SearchEffects.CurrentHistoryPage(new AsyncValue<SearchHistoryResult>.Loading(), "one"));
        var snapshot = new SearchHistoryResult("one", previous);
        Assert.Same(previous, SearchEffects.CurrentHistoryPage(new AsyncValue<SearchHistoryResult>.Loading(), "one", snapshot));
        Assert.Null(SearchEffects.CurrentHistoryPage(new AsyncValue<SearchHistoryResult>.Loading(), "two", snapshot));
        Assert.Null(SearchEffects.CurrentHistoryPage(new AsyncValue<SearchHistoryResult>.Error(new Exception()), "one", snapshot));
        Assert.NotEqual(SearchEffects.HistoryCacheKey("one", 0, 1), SearchEffects.HistoryCacheKey("two", 0, 1));
        Assert.NotEqual(SearchEffects.HistoryCacheKey("one", 0, 1), SearchEffects.HistoryCacheKey("one", 1, 1));
    }

    [Fact]
    public async Task DelayedRefresh_CancellationStopsFurtherRefreshes()
    {
        using var cancellation = new CancellationTokenSource();
        var sequence = new Ref<int>(0);
        var refreshes = new List<int>();
        var task = SearchEffects.DelayedRefreshAsync(sequence, refreshes.Add, () => 1, 1, 2, cancellation.Token);
        cancellation.Cancel();
        await task;
        Assert.Equal(new[] { 1 }, refreshes);
    }

    [Fact]
    public async Task ClearNotice_CancellationDoesNotClearNewNotice()
    {
        using var cancellation = new CancellationTokenSource();
        bool cleared = false;
        var task = SearchEffects.ClearNoticeLaterAsync(() => cleared = true, 1000, cancellation.Token);
        cancellation.Cancel();
        await task;
        Assert.False(cleared);
    }

    [Fact]
    public async Task ParticipantEnrichment_OneFailureDoesNotDiscardOtherResults()
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<int>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            SearchLoaders.RunParticipantEnrichmentAsync(() =>
            {
                if (i == 3) throw new InvalidOperationException();
                results.Add(i);
                return Task.CompletedTask;
            }, errors.Add, CancellationToken.None)));
        Assert.Equal(9, results.Count);
        Assert.Single(errors);
        Assert.DoesNotContain(3, results);
    }

    [Fact]
    public async Task ParticipantEnrichment_CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SearchLoaders.RunParticipantEnrichmentAsync(
            () => Task.FromCanceled(cancellation.Token), _ => throw new Exception("取消不应视为失败"), cancellation.Token));
    }

    [Fact]
    public async Task UploadBatch_FailureReleasesIdsAndSuccessKeepsThem()
    {
        var uploaded = new HashSet<long>();
        await SearchEffects.RunUploadBatchAsync([1, 2], uploaded, (_, _) => Task.FromResult(false), CancellationToken.None);
        Assert.Empty(uploaded);
        await SearchEffects.RunUploadBatchAsync([1, 2], uploaded, (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.Equal(2, uploaded.Count);
    }

    [Fact]
    public async Task UploadBatch_CancelReleasesIdsImmediatelyWithoutRemovingNewAttempt()
    {
        var uploaded = new HashSet<long>();
        using var cancellation = new CancellationTokenSource();
        var finishOld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = SearchEffects.RunUploadBatchAsync([1], uploaded, (_, _) => finishOld.Task, cancellation.Token);
        cancellation.Cancel();
        Assert.Empty(uploaded);
        await SearchEffects.RunUploadBatchAsync([1], uploaded, (_, _) => Task.FromResult(true), CancellationToken.None);
        finishOld.SetResult(false);
        await old;
        Assert.Contains(1, uploaded);
    }

    [Fact]
    public async Task DelayedRefresh_ObservesUpdatedLatestGameAndStops()
    {
        var sequence = new Ref<int>(0);
        long? latest = 1;
        var refreshes = new List<int>();
        Task refresh = SearchEffects.DelayedRefreshAsync(sequence, refreshes.Add, () => latest, 1, 2);

        Assert.Equal(new[] { 1 }, refreshes);
        latest = 2;
        await refresh;

        Assert.Equal(new[] { 1 }, refreshes);
    }

    [Fact]
    public async Task DelayedRefresh_RetriesUntilLatestGameChanges()
    {
        var sequence = new Ref<int>(0);
        long? latest = 1;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = SearchEffects.DelayedRefreshAsync(sequence, value =>
        {
            if (value == 2)
            {
                latest = 3;
                retried.SetResult();
            }
        }, () => latest, 1, 2);

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await refresh;

        Assert.Equal(2, sequence.Current);
    }

    [Fact]
    public void SearchToolbar_NoticeTransition_KeepsRootGridStructureStable()
    {
        var dummyBar = Border(Empty()).Width(100);

        var idleLayout = SearchChrome.BuildToolbarLayout(dummyBar, notice: null);
        var activeLayout = SearchChrome.BuildToolbarLayout(dummyBar, notice: "游戏 ID: 123456 已复制到剪贴板");

        foreach (var layout in new[] { idleLayout, activeLayout })
        {
            Assert.NotNull(layout);
            Assert.Single(layout.Definition.Columns);
            Assert.Equal(GridSize.Star(), layout.Definition.Columns[0]);
            Assert.Equal(2, layout.Definition.Rows.Length);
            Assert.Equal(GridSize.Auto, layout.Definition.Rows[0]);
            Assert.Equal(GridSize.Auto, layout.Definition.Rows[1]);
        }

        Assert.Single(idleLayout.Children);
        Assert.Equal(2, activeLayout.Children.Length);

        Assert.Equal(0.0, idleLayout.RowSpacing);
        Assert.Equal(6.0, activeLayout.RowSpacing);

        var idleRow0 = Assert.IsAssignableFrom<BorderElement>(idleLayout.Children[0]);
        Assert.NotNull(idleRow0.Attached);
        Assert.Equal(0, Assert.IsType<GridAttached>(idleRow0.Attached[typeof(GridAttached)]).Row);

        var activeRow0 = Assert.IsAssignableFrom<BorderElement>(activeLayout.Children[0]);
        Assert.NotNull(activeRow0.Attached);
        Assert.Equal(0, Assert.IsType<GridAttached>(activeRow0.Attached[typeof(GridAttached)]).Row);
        var noticeTextBlock = Assert.IsAssignableFrom<TextBlockElement>(activeLayout.Children[1]);
        Assert.Equal("游戏 ID: 123456 已复制到剪贴板", noticeTextBlock.Content);
    }

    [Fact]
    public void CopyGameId_RoutesThroughToastService()
    {
        var toastQueue = new Composition.ToastQueue();
        SearchEffects.CopyGameId(401171480376, toastQueue);

        var snapshot = toastQueue.Snapshot;
        Assert.NotEmpty(snapshot);
        var entry = snapshot[0];
        Assert.True(
            entry.Message == "游戏 ID: 401171480376 已复制到剪贴板" ||
            entry.Message == "复制到剪贴板失败");
    }

    [Fact]
    public void CopyRiotId_RoutesThroughToastService()
    {
        var toastQueue = new Composition.ToastQueue();
        bool copied = false;
        var profile = new Core.Features.Summoner.Models.SummonerProfile
        {
            AccountId = 1,
            DisplayName = "测试召唤师",
            GameName = "测试召唤师",
            TagLine = "12345",
            Puuid = "puuid-1",
            SummonerId = 1001,
            ProfileIconId = 1,
            SummonerLevel = 50,
            PercentCompleteForNextLevel = 0,
            XpSinceLastLevel = 0,
            XpUntilNextLevel = 1000,
        };

        Pages.Career.CareerCommands.CopyRiotId(profile, state => copied = state, toastQueue);

        var snapshot = toastQueue.Snapshot;
        Assert.NotEmpty(snapshot);
        var entry = snapshot[0];
        Assert.True(
            entry.Message == "已复制 Riot ID: 测试召唤师#12345" ||
            entry.Message == "复制到剪贴板失败");
    }
}
