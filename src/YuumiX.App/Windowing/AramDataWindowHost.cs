using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using YuumiX.App.Composition;
using YuumiX.App.Controls;
using YuumiX.App.Pages.AramData;
using YuumiX.App.Theming;
using static Microsoft.UI.Reactor.Factories;

namespace YuumiX.App.Windowing;

/// <summary>
/// 管理「海克斯与出装推荐」独立窗口的单例创建、紧贴主窗体右侧定位（与主窗体同高、较窄）及英雄联动。
/// </summary>
public static class AramDataWindowHost
{
    public const string Title = "海克斯与出装推荐";
    public const double DefaultWidth = 440;
    public const double DefaultHeight = 760;

    private static readonly Lock _syncLock = new();
    private static ReactorWindow? _window;
    private static bool _isAlwaysOnTop = true;

    public static bool IsAlwaysOnTop => _isAlwaysOnTop;
    public static event Action<int>? SelectedChampionRequested;

    /// <summary>
    /// 打开或聚焦独立窗口，并贴合主窗体右侧且保持同高；可选传入初始/目标英雄 ID。
    /// </summary>
    public static void ShowOrActivate(int? championId = null)
    {
        lock (_syncLock)
        {
            var (posX, posY, widthPx, heightPx, widthDip, heightDip) = ComputeDockedBounds();

            if (TryGetExistingHandleLocked(out var existingHwnd))
            {
                Win32WindowInterop.ShowWindow(existingHwnd);
                Win32WindowInterop.SetWindowBounds(
                    existingHwnd,
                    posX,
                    posY,
                    widthPx,
                    heightPx,
                    _isAlwaysOnTop,
                    preserveZOrderWhenNotTopmost: true,
                    noActivate: true);
                Win32WindowInterop.BringToFront(existingHwnd);
                if (championId is > 0)
                {
                    SelectedChampionRequested?.Invoke(championId.Value);
                }
                return;
            }

            var spec = new WindowSpec
            {
                Title = Title,
                Width = widthDip,
                Height = heightDip,
                MinWidth = widthDip,
                MaxWidth = widthDip,
                MinHeight = 480,
                StartPosition = WindowStartPosition.Manual,
                ManualPosition = (posX, posY),
                TitleBarHeight = WindowTitleBarHeight.Tall,
                IsMaximizable = false,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                Icon = File.Exists(AppAssets.AppIconPath) ? WindowIcon.FromPath(AppAssets.AppIconPath) : null,
            };

            AppHost.Services?.LoggerFactory
                .CreateLogger(typeof(AramDataWindowHost))
                .LogInformation("[AramDataWindowHost] 打开海克斯出装窗口 pos=({X},{Y}) size={W}x{H} champId={ChampId} alwaysOnTop={Top}",
                    posX, posY, widthDip, heightDip, championId, _isAlwaysOnTop);

            var window = ReactorApp.OpenWindow(spec, () => new AramDataWindowRoot(championId));
            window.Closing += OnWindowClosing;
            _window = window;

            if (_isAlwaysOnTop)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    UpdateAlwaysOnTop(true);
                    await Task.Delay(400).ConfigureAwait(false);
                    UpdateAlwaysOnTop(true);
                });
            }
        }
    }

    /// <summary>
    /// 若独立窗口已打开则静默切换目标英雄，不主动弹出或激活窗口。
    /// </summary>
    public static void NotifySelectedChampionIfOpen(int championId)
    {
        if (championId <= 0) return;

        bool isOpen;
        lock (_syncLock)
        {
            isOpen = TryGetExistingHandleLocked(out _);
        }

        if (isOpen)
        {
            SelectedChampionRequested?.Invoke(championId);
        }
    }

    /// <summary>
    /// 热键切换显隐：已显示则隐藏，未显示则置顶呼出（不抢游戏焦点）。
    /// </summary>
    public static void ToggleHoldPreview(int? championId = null)
    {
        lock (_syncLock)
        {
            if (TryGetExistingHandleLocked(out var existingHwnd) &&
                Win32WindowInterop.IsWindowVisible(existingHwnd))
            {
                Win32WindowInterop.HideWindow(existingHwnd);
                return;
            }
        }

        ShowHoldPreview(championId);
    }

    /// <summary>
    /// 热键呼出：不抢游戏焦点、置顶显示在屏幕/主窗体右侧。
    /// </summary>
    public static void ShowHoldPreview(int? championId = null)
    {
        lock (_syncLock)
        {
            var (posX, posY, widthPx, heightPx, widthDip, heightDip) = ComputeDockedBounds();

            if (TryGetExistingHandleLocked(out var existingHwnd))
            {
                Win32WindowInterop.ShowWindowNoActivate(existingHwnd);
                Win32WindowInterop.SetWindowBounds(
                    existingHwnd,
                    posX,
                    posY,
                    widthPx,
                    heightPx,
                    alwaysOnTop: true,
                    preserveZOrderWhenNotTopmost: false,
                    noActivate: true);
                if (championId is > 0)
                {
                    SelectedChampionRequested?.Invoke(championId.Value);
                }
                return;
            }

            var spec = new WindowSpec
            {
                Title = Title,
                Width = widthDip,
                Height = heightDip,
                MinWidth = widthDip,
                MaxWidth = widthDip,
                MinHeight = 480,
                StartPosition = WindowStartPosition.Manual,
                ManualPosition = (posX, posY),
                TitleBarHeight = WindowTitleBarHeight.Tall,
                IsMaximizable = false,
                ActivateOnOpen = false,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                Icon = File.Exists(AppAssets.AppIconPath) ? WindowIcon.FromPath(AppAssets.AppIconPath) : null,
            };

            _isAlwaysOnTop = true;
            var window = ReactorApp.OpenWindow(spec, () => new AramDataWindowRoot(championId));
            window.Closing += OnWindowClosing;
            _window = window;

            _ = Task.Run(async () =>
            {
                await Task.Delay(120).ConfigureAwait(false);
                UpdateAlwaysOnTop(true);
            });
        }
    }

    /// <summary>
    /// 热键松开隐藏：保留窗口实例在内存，实现下次秒开。
    /// </summary>
    public static void HideHoldPreview()
    {
        lock (_syncLock)
        {
            if (TryGetExistingHandleLocked(out var existingHwnd))
            {
                Win32WindowInterop.HideWindow(existingHwnd);
            }
        }
    }

    public static bool ToggleAlwaysOnTop()
    {
        var next = !_isAlwaysOnTop;
        UpdateAlwaysOnTop(next);
        return next;
    }

    public static void UpdateAlwaysOnTop(bool alwaysOnTop)
    {
        lock (_syncLock)
        {
            _isAlwaysOnTop = alwaysOnTop;
            if (!TryGetExistingHandleLocked(out var hwnd))
            {
                return;
            }

            Win32WindowInterop.SetAlwaysOnTop(hwnd, alwaysOnTop);
        }
    }

    internal static void NotifyClosed()
    {
        lock (_syncLock)
        {
            DetachWindowLocked();
        }
    }

    private static void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        lock (_syncLock)
        {
            if (!ReferenceEquals(_window, sender))
            {
                return;
            }

            DetachWindowLocked();
        }
    }

    private static bool TryGetExistingHandleLocked(out nint hwnd)
    {
        if (_window is null)
        {
            hwnd = 0;
            return false;
        }

        if (Win32WindowInterop.TryGetHandle(_window, out hwnd))
        {
            return true;
        }

        DetachWindowLocked();
        return false;
    }

    private static void DetachWindowLocked()
    {
        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;
            _window = null;
        }
    }

    private static (int PosX, int PosY, int WidthPx, int HeightPx, double WidthDip, double HeightDip) ComputeDockedBounds()
    {
        if (!Win32WindowInterop.TryGetWorkArea(out var workArea))
        {
            workArea = new Win32WindowInterop.Win32Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        }

        if (MainWindowRegistry.TryGetHandle(out var mainHwnd) &&
            Win32WindowInterop.IsWindowVisible(mainHwnd) &&
            !Win32WindowInterop.IsMinimized(mainHwnd) &&
            Win32WindowInterop.TryGetWindowRect(mainHwnd, out var mainRect))
        {
            var dpi = Win32WindowInterop.GetDpiForWindow(mainHwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;

            var heightPx = Math.Max(480, mainRect.Bottom - mainRect.Top);
            var widthPx = (int)Math.Round(DefaultWidth * scale);
            var heightDip = Math.Max(480, heightPx / scale);

            var posX = mainRect.Right;
            if (posX + widthPx > workArea.Right)
            {
                posX = Math.Max(workArea.Left, workArea.Right - widthPx);
            }

            var posY = Math.Clamp(mainRect.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - heightPx));
            return (posX, posY, widthPx, heightPx, DefaultWidth, heightDip);
        }

        var fallbackWidthPx = (int)Math.Round(DefaultWidth);
        var fallbackHeightPx = (int)Math.Round(DefaultHeight);
        var fallbackX = Math.Max(workArea.Left, workArea.Right - fallbackWidthPx - 8);
        var fallbackY = Math.Max(workArea.Top, workArea.Top + 24);
        return (fallbackX, fallbackY, fallbackWidthPx, fallbackHeightPx, DefaultWidth, DefaultHeight);
    }
}

/// <summary>
/// 独立窗口根壳件：注入与主窗口一致的 AppContext / ThemeContext，并同步关闭状态。
/// </summary>
internal sealed class AramDataWindowRoot(int? initialChampionId = null) : Component
{
    public override Element Render()
    {
        var window = UseWindow();
        var (palette, setPalette) = UseState(AppHost.Theme.Current, threadSafe: true);
        var (isPinned, setIsPinned) = UseState(AramDataWindowHost.IsAlwaysOnTop, threadSafe: true);

        UseEffect(
            () =>
            {
                var subscription = AppHost.Theme.Subscribe(setPalette);
                return () => subscription.Dispose();
            },
            0);

        UseEffect(() => () => AramDataWindowHost.NotifyClosed(), []);

        void SyncPinPassthrough(FrameworkElement fe)
        {
            if (window?.AppWindow is null || fe.XamlRoot is null || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
            {
                return;
            }

            var scale = fe.XamlRoot.RasterizationScale;
            var pt = fe.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var rect = new RectInt32(
                (int)Math.Floor(pt.X * scale),
                (int)Math.Floor(pt.Y * scale),
                (int)Math.Ceiling(fe.ActualWidth * scale),
                (int)Math.Ceiling(fe.ActualHeight * scale));

            var nonClient = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
        }

        var pinButton = Button(
                Caption(isPinned ? Icons.Pinned : Icons.Pin)
                    .FontFamily(Icons.FontFamily)
                    .Foreground(isPinned ? Theme.AccentText : Theme.SecondaryText)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center),
                () => setIsPinned(AramDataWindowHost.ToggleAlwaysOnTop()))
            .GhostButton()
            .Width(46)
            .Height(48)
            .Padding(0)
            .Margin(0, 0, 136, 0)
            .CornerRadius(YuumiTheme.SquareRadius)
            .HorizontalContentAlignment(HorizontalAlignment.Center)
            .VerticalContentAlignment(VerticalAlignment.Center)
            .HAlign(HorizontalAlignment.Right)
            .VAlign(VerticalAlignment.Center)
            .OnMountAdd(el =>
            {
                if (el is FrameworkElement fe)
                {
                    fe.Loaded += (_, _) => SyncPinPassthrough(fe);
                    fe.SizeChanged += (_, _) => SyncPinPassthrough(fe);
                }
            })
            .WithToolTip(TextBlock(isPinned ? "取消窗口置顶" : "窗口置顶"))
            .AutomationName(isPinned ? "取消窗口置顶" : "窗口置顶");

        var titleBar = TitleBar(AramDataWindowHost.Title) with
        {
            Icon = ImageIcon(new Uri(AppAssets.LogoUri)),
            IsBackButtonVisible = false,
            HeightOption = WindowTitleBarHeight.Tall,
        };

        return Grid(
                [GridSize.Star()],
                [GridSize.Auto, GridSize.Star()],
                [
                    titleBar.Grid(row: 0),
                    pinButton.Grid(row: 0),
                    Component<AramDataPage, AramDataPageProps>(new AramDataPageProps(initialChampionId)).Grid(row: 1),
                    Component<DropDownLayer>().Grid(row: 1),
                ])
            .RequestedTheme(palette.ElementTheme)
            .Provide(AppHost.Context, AppHost.Services)
            .Provide(AppHost.ThemeContext, AppHost.Theme);
    }
}
