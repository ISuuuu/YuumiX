using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
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
        var summoner = new AsyncValue<SummonerProfile?>.Data(null);
        var i18n = new I18nService();
        var failed = SearchChrome.ResourceState(summoner, new AsyncValue<SearchHistoryResult>.Error(new Exception()), false, true, false, YuumiTheme.Default, () => { }, i18n);
        var empty = SearchChrome.ResourceState(summoner, new AsyncValue<SearchHistoryResult>.Data(new SearchHistoryResult("key", MatchHistoryPage.Empty)), true, true, false, YuumiTheme.Default, () => { }, i18n);
        Assert.IsNotType<BorderElement>(failed);
        var border = Assert.IsAssignableFrom<BorderElement>(empty);
        Assert.Equal("暂无战绩", Assert.IsAssignableFrom<TextBlockElement>(border.Child).Content);
    }

    [Fact]
    public void ResourceState_LoadingDoesNotDisplayEmptyResult()
    {
        var element = SearchChrome.ResourceState(new AsyncValue<SummonerProfile?>.Data(null),
            new AsyncValue<SearchHistoryResult>.Loading(), false, true, false, YuumiTheme.Default, () => { }, new I18nService());
        Assert.IsAssignableFrom<GridElement>(element);
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
}
