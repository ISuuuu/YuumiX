using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using YuumiX.App.Controls;
using YuumiX.App.Pages.Tools;
using YuumiX.App.Pages.Tools.Sections;
using YuumiX.App.Theming;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Lcu;
using Xunit;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Tests;

public class ToolsConfirmationTests
{
    private static readonly YuumiPalette Palette = new()
    {
        WinCard = "", LoseCard = "", RemakeCard = "", LightDeath = "", DarkDeath = "",
        ThemeMode = "Light", MicaEnabled = false,
    };

    [Fact]
    public void RestartRejectsFailedHttpResponse()
    {
        ToolsPage.EnsureRestartSucceeded(new LcuResponse(204, ""), "unused");
        var error = Assert.Throws<InvalidOperationException>(() =>
            ToolsPage.EnsureRestartSucceeded(new LcuResponse(500, ""), "HTTP 状态码 500"));
        Assert.Equal("HTTP 状态码 500", error.Message);
    }

    [Theory]
    [InlineData(ContentDialogResult.None, true, 0, 1)]
    [InlineData(ContentDialogResult.Primary, true, 1, 0)]
    [InlineData(ContentDialogResult.Primary, false, 0, 1)]
    public void DialogRoutesCloseResultOnce(ContentDialogResult result, bool enabled, int expectedConfirm, int expectedDismiss)
    {
        var confirmed = 0;
        var dismissed = 0;
        var dialog = Assert.IsType<ContentDialogElement>(ConfirmationDialog.Render(
            "title", Body("content"), "confirm", "cancel", true,
            () => confirmed++, () => dismissed++, Palette, enabled));

        Assert.Equal(ContentDialogButton.Close, dialog.DefaultButton);
        Assert.Equal(enabled, dialog.IsPrimaryButtonEnabled);
        Assert.Equal(0, confirmed);
        var onClosed = Assert.IsType<Action<ContentDialogResult>>(dialog.OnClosed);
        onClosed(result);
        Assert.Equal(expectedConfirm, confirmed);
        Assert.Equal(expectedDismiss, dismissed);
    }

    [Fact]
    public void DeleteSnapshotExcludesHiddenAndDoesNotFollowLaterSelectionChanges()
    {
        FriendEntry Entry(string id, FriendActivityStatus status) => new()
        {
            Friend = new LcuFriend { Id = id }, MatchStatus = new FriendMatchStatus(null, status),
        };
        var selected = new HashSet<string> { "safe", "hidden" };
        var snapshot = GameSection.SnapshotDeletableIds(
            [Entry("safe", FriendActivityStatus.Ok), Entry("hidden", FriendActivityStatus.Hidden), Entry("later", FriendActivityStatus.Ok)], selected);
        selected.Clear();
        selected.Add("later");
        Assert.Equal(new[] { "safe" }, snapshot);
    }
}
