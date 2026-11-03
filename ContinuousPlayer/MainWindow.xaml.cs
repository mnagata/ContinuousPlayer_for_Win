using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ContinuousPlayer;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AccessibilitySettings accessibility = new();
    private readonly UISettings themeSettings = new();
    private bool initialWindowSizeApplied;
    private double titleBarActionsOffset;
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        UpdateCaptionColors();
        themeSettings.ColorValuesChanged += ThemeSettings_ColorValuesChanged;
        Closed += (_, _) => themeSettings.ColorValuesChanged -= ThemeSettings_ColorValuesChanged;

        AppWindow.SetIcon("Assets/AppIcon.ico");
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        // Include the title bar; native frame insets are corrected after the first layout.
        AppWindow.Resize(new SizeInt32((int)(1024 * scale), (int)((576 + AppTitleBar.Height) * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(440 * scale);
            presenter.PreferredMinimumHeight = (int)(380 * scale);
        }

    }

    public void ShowMainPage() => RootFrame.Navigate(typeof(MainPage));

    private void WindowLayout_Loaded(object sender, RoutedEventArgs e)
    {
        // The native title template explicitly uses CaptionTextBlockStyle, so FontSize does not inherit.
        if (FindTitleText(AppTitleBar) is { } titleText) titleText.FontSize = 16;
        DispatcherQueue.TryEnqueue(UpdateTitleBarPosition);
        if (initialWindowSizeApplied || RootFrame.ActualWidth <= 0 || RootFrame.ActualHeight <= 0) return;
        initialWindowSizeApplied = true;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = AppWindow.Size;
        // Measure the page below the title bar so custom-caption and DPI insets are counted once.
        AppWindow.Resize(new SizeInt32(
            size.Width + (int)Math.Round((1024 - RootFrame.ActualWidth) * scale),
            size.Height + (int)Math.Round((576 - RootFrame.ActualHeight) * scale)));
    }

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateTitleBarPosition);

    private void UpdateTitleBarPosition()
    {
        if (AppTitleBar.Visibility != Visibility.Visible ||
            AppTitleBar.XamlRoot is not { } root) return;
        AppTitleBar.UpdateLayout();
        if (AppTitleBar.ActualWidth <= 0) return;
        var captionLeft = AppTitleBar.ActualWidth - AppWindow.TitleBar.RightInset / root.RasterizationScale;
        // Measure commands independently: an unconstrained title can otherwise push them offscreen.
        PlaybackTitleBarActions.Measure(new Windows.Foundation.Size(double.PositiveInfinity, AppTitleBar.Height));
        var commandsWidth = PlaybackTitleBarActions.DesiredSize.Width;
        if (FindTitleText(AppTitleBar) is { } title)
        {
            var titleLeft = title.TransformToVisual(AppTitleBar).TransformPoint(new Windows.Foundation.Point()).X;
            var textWidth = Math.Max(0, captionLeft - titleLeft - commandsWidth - title.Margin.Right - 8);
            if (FindTitleText(AppTitleBar, "PART_SubtitleText") is { Visibility: Visibility.Visible } subtitle)
            {
                subtitle.MaxWidth = Math.Min(160, textWidth / 3);
                AppTitleBar.UpdateLayout();
                textWidth = Math.Max(0, textWidth - subtitle.ActualWidth - subtitle.Margin.Left - subtitle.Margin.Right);
            }
            title.MaxWidth = textWidth;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            AppTitleBar.UpdateLayout();
        }
        var right = PlaybackTitleBarActions.TransformToVisual(AppTitleBar)
            .TransformPoint(new Windows.Foundation.Point(PlaybackTitleBarActions.ActualWidth, 0)).X;
        // Shift by two button widths when space permits, leaving a gap before the native captions.
        var offset = Math.Clamp(captionLeft - (right - titleBarActionsOffset) - 8, 0, 64);
        if (Math.Abs(offset - titleBarActionsOffset) > 0.5)
        {
            titleBarActionsOffset = offset;
            PlaybackTitleBarActions.Margin = new Thickness(offset, 0, -offset, 0);
            AppTitleBar.UpdateLayout();
        }
        AppTitleBar.RecomputeDragRegions();
    }

    private static TextBlock? FindTitleText(DependencyObject element, string name = "PART_TitleText")
    {
        if (element is TextBlock title && title.Name == name) return title;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindTitleText(VisualTreeHelper.GetChild(element, i), name) is { } match) return match;
        return null;
    }

    public void SetPlaybackTitleBar(UIElement toolbar, bool visible, string title, string status)
    {
        if (!ReferenceEquals(PlaybackToolbarPresenter.Content, toolbar)) PlaybackToolbarPresenter.Content = toolbar;
        toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.Title = visible ? title : "ContinuousPlayer";
        AppTitleBar.Subtitle = visible ? status : "";
        if (visible) DispatcherQueue.TryEnqueue(UpdateTitleBarPosition);
    }

    private void ThemeSettings_ColorValuesChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(UpdateCaptionColors);

    private void UpdateCaptionColors()
    {
        // Native caption buttons do not inherit the XAML TitleBar's dark theme.
        Windows.UI.Color? Color(string key)
        {
            if (accessibility.HighContrast) return null;
            // Resolve the brand palette explicitly for native caption colors.
            foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
                if (dictionary.ThemeDictionaries.TryGetValue("Dark", out var palette) && palette is ResourceDictionary theme &&
                    theme.TryGetValue(key, out var value) && value is SolidColorBrush brush) return brush.Color;
            return null;
        }
        AppWindow.TitleBar.ButtonForegroundColor = Color("MainTextBrush");
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Color("MutedTextBrush");
        AppWindow.TitleBar.ButtonBackgroundColor = Color("BrowserBrush");
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Color("BrowserBrush");
        AppWindow.TitleBar.ButtonHoverForegroundColor = Color("MainTextBrush");
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Color("BrowserStrokeBrush");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    public void ToggleFullscreen() => SetFullscreen(AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen);
    public bool IsFullscreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    public event EventHandler? FullscreenChanged;
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    public void SetFullscreen(bool fullscreen)
    {
        if ((AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) == fullscreen) return;
        AppWindow.SetPresenter(fullscreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        FullscreenChanged?.Invoke(this, EventArgs.Empty);
    }
}
