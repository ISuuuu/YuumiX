using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Xunit;
using YuumiX.App.Pages.Opgg;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Persistence;

namespace YuumiX.App.Tests;

public sealed class OpggEffectsTests
{
    [Fact]
    public void CurrentTierItems_IgnoresStaleReloadingFromDifferentFilterKey()
    {
        var stateA = new OpggFilterState("tier", OpggRegion.Kr, OpggMode.Ranked, OpggTier.EmeraldPlus, OpggPosition.Mid, null);
        var stateB = stateA with { Position = OpggPosition.Top };

        string keyA = OpggEffects.TierKey(stateA);
        string keyB = OpggEffects.TierKey(stateB);
        var itemsA = (TierListItem[])[new TierListItem { Id = 1, Name = "Ahri" }];

        var dataA = new AsyncValue<OpggTierResult?>.Data(new OpggTierResult(keyA, itemsA));
        Assert.Same(itemsA, OpggEffects.CurrentTierItems(dataA, keyA));
        Assert.Empty(OpggEffects.CurrentTierItems(dataA, keyB));

        var reloadingWithA = new AsyncValue<OpggTierResult?>.Reloading(new OpggTierResult(keyA, itemsA));
        Assert.Same(itemsA, OpggEffects.CurrentTierItems(reloadingWithA, keyA));
        Assert.Empty(OpggEffects.CurrentTierItems(reloadingWithA, keyB));
    }

    [Fact]
    public void CurrentBuildPayload_IgnoresStaleHeroOrFilterKey()
    {
        var stateA = new OpggFilterState("build", OpggRegion.Kr, OpggMode.Ranked, OpggTier.EmeraldPlus, OpggPosition.Mid, 103);
        var stateB = stateA with { SelectedChampionId = 157 };

        string keyA = OpggEffects.BuildKey(stateA);
        string keyB = OpggEffects.BuildKey(stateB);
        var payloadA = new OpggBuildPayload { Data = new OpggBuildData() };

        var dataA = new AsyncValue<OpggBuildResult?>.Data(new OpggBuildResult(keyA, payloadA));
        Assert.Same(payloadA, OpggEffects.CurrentBuildPayload(dataA, keyA));
        Assert.Null(OpggEffects.CurrentBuildPayload(dataA, keyB));
    }

    [Fact]
    public void ApplyChampSelectEvent_UpdatesViewChampionModeAndPositionAtomically()
    {
        var initial = new OpggFilterState("tier", OpggRegion.Kr, OpggMode.Ranked, OpggTier.EmeraldPlus, OpggPosition.Mid, null);
        var next = OpggEffects.ApplyChampSelectEvent(initial, new OpggBuildEvent(238, OpggMode.Aram, OpggPosition.Top));

        Assert.Equal("build", next.View);
        Assert.Equal(238, next.SelectedChampionId);
        Assert.Equal(OpggMode.Aram, next.Mode);
        Assert.Equal(OpggPosition.Top, next.Position);
    }

    [Fact]
    public void ResolveInitialFilterState_RespectsInitialChampionAndAutoSelect()
    {
        var emptyConfig = new FunctionsConfig();
        var tierDefault = OpggEffects.ResolveInitialFilterState(null, emptyConfig);
        Assert.Equal("tier", tierDefault.View);
        Assert.Null(tierDefault.SelectedChampionId);

        var explicitChamp = OpggEffects.ResolveInitialFilterState(103, emptyConfig);
        Assert.Equal("build", explicitChamp.View);
        Assert.Equal(103, explicitChamp.SelectedChampionId);

        var autoConfig = new FunctionsConfig
        {
            EnableAutoSelectChampion = true,
            AutoSelectChampionMid = [157],
        };
        var autoChamp = OpggEffects.ResolveInitialFilterState(null, autoConfig);
        Assert.Equal("build", autoChamp.View);
        Assert.Equal(157, autoChamp.SelectedChampionId);
    }
}
