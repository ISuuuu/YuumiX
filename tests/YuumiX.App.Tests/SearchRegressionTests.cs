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
    public void DetailLoading_RendersNativeProgressRingWithAccessibleLabel()
    {
        const string loadingText = "正在读取对局详情…";
        var element = SearchChrome.DetailLoading(loadingText);

        var border = Assert.IsAssignableFrom<BorderElement>(element);
        var stack = Assert.IsAssignableFrom<StackElement>(border.Child);
        Assert.Equal(2, stack.Children.Length);

        var ring = Assert.IsAssignableFrom<ProgressRingElement>(stack.Children[0]);
        Assert.True(ring.IsActive);
        Assert.NotNull(ring.Modifiers);
        Assert.Equal(24.0, ring.Modifiers.Width);
        Assert.Equal(24.0, ring.Modifiers.Height);
        Assert.Equal(loadingText, ring.Modifiers.AutomationName);

        var caption = Assert.IsAssignableFrom<TextBlockElement>(stack.Children[1]);
        Assert.Equal(loadingText, caption.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("很长的召唤师名称ABCDEFGHIJKLMNOPQRSTUVWXYZ#12345")]
    public void SearchInput_ReservesSeparateColumnAndPreservesInputCallbacks(string query)
    {
        string? changed = null;
        string? submitted = null;
        var input = AutoSuggestBox(query, value => changed = value, value => submitted = value)
            .AutomationName("召唤师名称搜索");
        var idle = SearchChrome.SearchInput(input, searching: false);
        var pending = SearchChrome.SearchInput(input, searching: true);

        Assert.Equal(idle.Definition.Columns, pending.Definition.Columns);
        Assert.Equal(GridSize.Star(), pending.Definition.Columns[0]);
        Assert.Equal(GridSize.Px(20), pending.Definition.Columns[1]);
        Assert.Equal(4, pending.ColumnSpacing);

        foreach (var layout in new[] { idle, pending })
        {
            Assert.Equal(2, layout.Children.Length);
            var searchBox = Assert.IsAssignableFrom<AutoSuggestBoxElement>(layout.Children[0]);
            var slot = Assert.IsAssignableFrom<BorderElement>(layout.Children[1]);
            Assert.NotNull(searchBox.Attached);
            Assert.NotNull(slot.Attached);
            Assert.Equal(0, Assert.IsType<GridAttached>(searchBox.Attached[typeof(GridAttached)]).Column);
            Assert.Equal(1, Assert.IsType<GridAttached>(slot.Attached[typeof(GridAttached)]).Column);
            Assert.Equal(query, searchBox.Text.Value);
            Assert.NotNull(slot.Modifiers);
            Assert.Equal(20.0, slot.Modifiers.Width);
            Assert.Equal(32.0, slot.Modifiers.Height);
            Assert.Equal(default, slot.Modifiers.Margin);
            Assert.NotNull(searchBox.OnTextChanged);
            Assert.NotNull(searchBox.OnQuerySubmitted);
            searchBox.OnTextChanged(query);
            searchBox.OnQuerySubmitted(query);
            Assert.Equal(query, changed);
            Assert.Equal(query, submitted);
        }

        var idleSlot = Assert.IsAssignableFrom<BorderElement>(idle.Children[1]);
        var pendingSlot = Assert.IsAssignableFrom<BorderElement>(pending.Children[1]);
        var idleRing = Assert.IsAssignableFrom<ProgressRingElement>(idleSlot.Child);
        var pendingRing = Assert.IsAssignableFrom<ProgressRingElement>(pendingSlot.Child);
        Assert.False(idleRing.IsActive);
        Assert.True(pendingRing.IsActive);
        Assert.NotNull(idleRing.Modifiers);
        Assert.NotNull(pendingRing.Modifiers);
        Assert.False(idleRing.Modifiers.IsVisible);
        Assert.True(pendingRing.Modifiers.IsVisible);
        Assert.Equal("正在解析召唤师…", pendingRing.Modifiers.AutomationName);
    }
}
