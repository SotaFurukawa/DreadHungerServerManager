using System.Runtime.InteropServices;
using DreadHungerServerManager.App.Services;
using DreadHungerServerManager.App.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace DreadHungerServerManager.App.Views;

public sealed partial class MainWindow : Window
{
    private const double ToastOffScreenY = -160;
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _toastDismissTimer;
    private bool _toastVisible;
    private (string Message, NoticeSeverity Severity)? _pendingToast;
    private bool _closeConfirmed;
    private bool _closing;

    public MainWindow(ServerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppTitleBar.Subtitle = AppInfo.Version;
        ApplyBackdrop();
        ResizeToFitScreen();
        AppWindow.Closing += AppWindow_Closing;

        NavView.SelectedItem = ServerNavItem;
    }

    public ServerViewModel ViewModel { get; }

    public void ShowToast(string message, NoticeSeverity severity)
    {
        _toastDismissTimer?.Stop();
        if (_toastVisible)
        {
            // 表示中の通知を引っ込めてから次を出す。
            _pendingToast = (message, severity);
            AnimateToast(toOffScreen: true, onCompleted: () =>
            {
                if (_pendingToast is { } next)
                {
                    _pendingToast = null;
                    ApplyToastContent(next.Message, next.Severity);
                    AnimateToast(toOffScreen: false, onCompleted: StartToastDismissTimer);
                }
            });
            return;
        }

        ApplyToastContent(message, severity);
        ToastBorder.Visibility = Visibility.Visible;
        AnimateToast(toOffScreen: false, onCompleted: StartToastDismissTimer);
    }

    private void ApplyBackdrop()
    {
        // Mica非対応（Windows 10 / Windows Server 2019）では無地の背景にする。
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            RootGrid.Background = ViewHelpers.Brush("ApplicationPageBackgroundThemeBrush");
        }
    }

    private void ResizeToFitScreen()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)Math.Min(1400 * scale, workArea.Width * 0.94);
        var height = (int)Math.Min(920 * scale, workArea.Height * 0.94);
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(900 * scale);
            presenter.PreferredMinimumHeight = (int)(600 * scale);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (await ViewModel.PrepareForExitAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closing = false;
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "cloud" => typeof(CloudPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(ServerPage),
        };
        if (NavFrame.CurrentSourcePageType != page)
        {
            NavFrame.Navigate(page);
        }
    }

    private void ApplyToastContent(string message, NoticeSeverity severity)
    {
        var (background, accent, glyph) = severity switch
        {
            NoticeSeverity.Error => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush", ""),
            NoticeSeverity.Warning => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush", ""),
            NoticeSeverity.Informational => ("SystemFillColorAttentionBackgroundBrush", "SystemFillColorAttentionBrush", ""),
            _ => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush", ""),
        };

        ToastInner.Background = ViewHelpers.Brush(background);
        ToastInner.BorderBrush = ViewHelpers.Brush(accent);
        ToastIcon.Glyph = glyph;
        ToastIcon.Foreground = ViewHelpers.Brush(accent);
        ToastText.Text = message;
    }

    private void StartToastDismissTimer()
    {
        _toastDismissTimer?.Stop();
        _toastDismissTimer = new DispatcherTimer { Interval = ToastDuration };
        _toastDismissTimer.Tick += (_, _) =>
        {
            _toastDismissTimer.Stop();
            HideToast();
        };
        _toastDismissTimer.Start();
    }

    private void HideToast() => AnimateToast(toOffScreen: true, onCompleted: () => ToastBorder.Visibility = Visibility.Collapsed);

    private void AnimateToast(bool toOffScreen, Action? onCompleted)
    {
        _toastVisible = !toOffScreen;
        var animation = new DoubleAnimation
        {
            To = toOffScreen ? ToastOffScreenY : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = toOffScreen ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, ToastTransform);
        Storyboard.SetTargetProperty(animation, "Y");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (onCompleted is not null)
        {
            storyboard.Completed += (_, _) => onCompleted();
        }

        storyboard.Begin();
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e)
    {
        _toastDismissTimer?.Stop();
        HideToast();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr window);
}
