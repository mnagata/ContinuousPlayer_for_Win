using Microsoft.UI.Xaml;
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
        if (initialWindowSizeApplied || RootFrame.ActualWidth <= 0 || RootFrame.ActualHeight <= 0) return;
        initialWindowSizeApplied = true;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = AppWindow.Size;
        // Measure the page below the title bar so custom-caption and DPI insets are counted once.
        AppWindow.Resize(new SizeInt32(
            size.Width + (int)Math.Round((1024 - RootFrame.ActualWidth) * scale),
            size.Height + (int)Math.Round((576 - RootFrame.ActualHeight) * scale)));
    }

    public void SetPlaybackTitleBar(UIElement toolbar, bool visible, string title, string status, string? fullName = null)
    {
        if (!ReferenceEquals(PlaybackTitleBarActions.Content, toolbar)) PlaybackTitleBarActions.Content = toolbar;
        toolbar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.Title = visible ? title : "ContinuousPlayer";
        AppTitleBar.Subtitle = visible ? status : "";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(AppTitleBar, visible ? fullName ?? title : null);
        if (visible) AppTitleBar.RecomputeDragRegions();
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
