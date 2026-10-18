using ContinuousPlayer.Core;
using ContinuousPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ContinuousPlayer;

public sealed partial class MainPage : Page
{
    public MainPageViewModel Vm { get; } = new();
    private readonly LibraryStore library = new(Windows.Storage.ApplicationData.Current.LocalFolder.Path);
    private readonly DlnaClient dlna = new();
    private bool loaded;
    private bool disposed;
    private bool dialogOpen;

    public MainPage()
    {
        InitializeComponent();
        InitializeCursorAutoHide();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        App.Window.Closed += (_, _) => Cleanup();
        App.Window.Activated += (_, args) =>
        {
            windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            if (args.WindowActivationState == WindowActivationState.Deactivated && isPlayback && !dialogOpen && BrowserOverlay.Visibility != Visibility.Visible)
                SetPaused(true, false);
            UpdateCursorAutoHide();
        };
        await library.LoadAsync();
        ChooseButton.Focus(FocusState.Programmatic);
        if (library.LoadWarning != null) await ShowMessageAsync("保存データ", library.LoadWarning);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BrowserCard == null) return;
        var compact = e.NewSize.Height < 560;
        BrowserOverlay.Padding = new Thickness(compact ? 12 : 24);
        BrowserCard.Padding = new Thickness(compact ? 12 : 20);
        BrowserHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PortraitInfo.Visibility = e.NewSize.Height > e.NewSize.Width ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Choose_Click(object sender, RoutedEventArgs e) => await OpenLibraryAsync();
    private void Exit_Click(object sender, RoutedEventArgs e) => App.Window.Close();
    private void Home_Click(object sender, RoutedEventArgs e) => GoHome();
    private void Resume_Click(object sender, RoutedEventArgs e) => SetPaused(false);
    private async void Info_Click(object sender, RoutedEventArgs e) => await ShowInfoAsync();
    private async void PlaybackFolder_Click(object sender, RoutedEventArgs e) => await ReturnToFolderAsync();

    private void GoHome()
    {
        CloseBrowser();
        StopPlayback();
        ChooseButton.Focus(FocusState.Programmatic);
    }

    private async Task ShowMessageAsync(string title, string text)
    {
        if (dialogOpen || disposed) return;
        dialogOpen = true;
        UpdateCursorAutoHide();
        try
        {
            await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = title,
                Content = new ScrollViewer { MaxHeight = 400, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                CloseButtonText = "閉じる" }.ShowAsync();
        }
        finally { dialogOpen = false; UpdateCursorAutoHide(); }
    }

    private async Task<bool> SaveLibraryAsync()
    {
        try { await library.SaveAsync(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (BrowserOverlay.Visibility == Visibility.Visible) ShowBrowserError("保存に失敗しました。" + ex.Message);
            else Vm.PlaybackStatus = "フォルダー情報の保存に失敗しました。";
            return false;
        }
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (dialogOpen || e.Handled || e.KeyStatus.WasKeyDown) return;
        if (e.Key == VirtualKey.F11)
        {
            ((MainWindow)App.Window).ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (BrowserOverlay.Visibility == Visibility.Visible || !isPlayback) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        var toolbarFocus = IsWithin(focused, PauseToolbar) && userPaused;
        switch (e.Key)
        {
            case VirtualKey.Space:
                SetPaused(!userPaused); break;
            case VirtualKey.Enter:
                if (toolbarFocus) return;
                SetPaused(!userPaused); break;
            case VirtualKey.Left:
            case VirtualKey.Right:
                await MoveMediaAsync(e.Key == VirtualKey.Left ? -1 : 1); break;
            case VirtualKey.Up: await SeekAsync(-10); break;
            case VirtualKey.Down: await SeekAsync(10); break;
            case VirtualKey.Escape: await ReturnToFolderAsync(); break;
            case VirtualKey.I: await ShowInfoAsync(); break;
            case VirtualKey.O: await ReturnToFolderAsync(); break;
            case VirtualKey.H: GoHome(); break;
            default: return;
        }
        e.Handled = true;
    }

    private static bool IsWithin(DependencyObject? node, DependencyObject parent)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, parent)) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void Cleanup()
    {
        if (disposed) return;
        disposed = true;
        browserCancellation?.Cancel();
        StopPlayback();
        DisposeCursorAutoHide();
        dlna.Dispose();
    }
}
