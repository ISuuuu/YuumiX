using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using YuumiX.App.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Controls;

internal static class ConfirmationDialog
{
    public static Element Render(
        string title,
        Element content,
        string primaryText,
        string closeText,
        bool isOpen,
        Action onConfirm,
        Action onDismiss,
        YuumiPalette palette,
        bool isPrimaryEnabled = true) =>
        (ContentDialog(title, content, primaryText) with
        {
            IsOpen = isOpen,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = isPrimaryEnabled,
        })
        .DialogTheme(palette.ElementTheme)
        .Closed(result =>
        {
            if (result == ContentDialogResult.Primary && isPrimaryEnabled)
            {
                onConfirm();
            }
            else
            {
                onDismiss();
            }
        });
}
