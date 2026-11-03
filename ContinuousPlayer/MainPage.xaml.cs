using ContinuousPlayer.Core;
using ContinuousPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace ContinuousPlayer;

public sealed partial class MainPage : Page
{
    public MainPageViewModel Vm { get; } = new();

    // Geometry has a visual owner. Each toolbar needs its own binding factory because x:Bind
    // reuses the result of identical function bindings within a page.
    public static Geometry IconGeometry(string key) =>
        (Geometry)XamlBindingHelper.ConvertValue(
            typeof(Geometry), Application.Current.Resources[key]);
    public static Geometry TitleBarIconGeometry(string key) => IconGeometry(key);

    private readonly LibraryStore library = new(Windows.Storage.ApplicationData.Current.LocalFolder.Path);
    private readonly DlnaClient dlna = new();
    private bool loaded;
    private bool disposed;
    private bool dialogOpen;

    public MainPage()
    {
        InitializeComponent();
        InitializeCursorAutoHide();
        InitializeInput();
        InitializePlaybackChrome();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        ChooseButton.IsEnabled = false;
        App.Window.Closed += (_, _) => Cleanup();
        App.Window.Activated += Window_Activated;
        ((MainWindow)App.Window).FullscreenChanged += Window_FullscreenChanged;
        UpdateFullscreenButtons();
        try
        {
            interruptionMonitor = new(App.Window, PlaybackInterruptionChanged);
            inputMonitor = new(App.WindowHandle);
            transportControls = new(App.WindowHandle, key => inputMonitor?.TryAcceptMediaKey(key) != false, Transport_Command);
            await interruptionMonitor.InitializeAudioAsync();
            if (disposed) return;
        }
        catch (Exception ex)
        {
            if (disposed) return;
            await ShowMessageAsync("中断監視", "ロック・スリープ・音声出力の監視を開始できませんでした。\n" + ex.Message);
        }
        await library.LoadAsync();
        audioSettings = App.AudioOptions.ApplyTo(await audioSettingsStore.LoadAsync());
        if (disposed) return;
        audioSettingsReady = true;
        UpdatePlaybackButtons();
        ChooseButton.IsEnabled = true;
        ChooseButton.Focus(FocusState.Programmatic);
        if (library.LoadWarning != null) await ShowMessageAsync("保存データ", library.LoadWarning);
        if (audioSettingsStore.LoadWarning != null) await ShowMessageAsync("音声出力設定", audioSettingsStore.LoadWarning);
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateCursorAutoHide();
        if (windowActive) FocusPlaybackSurface();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BrowserCard == null) return;
        var compact = e.NewSize.Height < 560;
        BrowserOverlay.Padding = new Thickness(compact ? 12 : 24);
        BrowserCard.Padding = new Thickness(compact ? 12 : 20);
        BrowserHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Choose_Click(object sender, RoutedEventArgs e) => await OpenLibraryAsync();
    private async void Home_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.Home);
    private async void Resume_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.Toggle);
    private async void Previous_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.Previous);
    private async void Next_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.Next);
    private async void Rewind_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.SeekBackward);
    private async void Forward_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.SeekForward);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ((MainWindow)App.Window).ToggleFullscreen();
    private async void Info_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.Information);
    private async void PlaybackFolder_Click(object sender, RoutedEventArgs e) => await ExecutePlaybackCommandAsync(PlaybackCommand.Files);

    private void GoHome()
    {
        CloseBrowser(BrowserCloseReason.Home);
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
        if (dialogOpen || e.Handled) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsTextEntry(focused)) return;
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) return;
        if (e.Key == VirtualKey.F11 && !IsKeyDown(VirtualKey.Menu))
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown)
            {
                KeyboardPlaybackActivity();
                ((MainWindow)App.Window).ToggleFullscreen();
                KeyboardPlaybackActivity();
            }
            return;
        }
        if (BrowserOverlay.Visibility == Visibility.Visible)
        {
            await HandleBrowserKeyAsync(e, focused);
            return;
        }
        if (IsKeyDown(VirtualKey.Menu)) return;
        if (isPlayback && e.Key == VirtualKey.Tab) ShowPlaybackChrome();
        if (e.KeyStatus.WasKeyDown)
        {
            if (isPlayback && InputKey(e.Key) != PlaybackInputKey.Other) e.Handled = true;
            return;
        }
        if (!isPlayback) return;
        var toolbarFocus = PlaybackToolbarHasFocus;
        if (toolbarFocus && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            MoveToolbarFocus(e.Key);
            e.Handled = true;
            return;
        }
        var command = PlaybackInput.Resolve(InputKey(e.Key), IsWithin(focused, TouchSurface)
            ? PlaybackInputTarget.Surface : PlaybackInputTarget.Control, IsKeyDown(VirtualKey.Shift));
        if (command == PlaybackCommand.None) return;
        e.Handled = true;
        await ExecutePlaybackKeyboardCommandAsync(command);
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
        Vm.PropertyChanged -= PlaybackTitleBar_PropertyChanged;
        if (keyboardRoot != null && keyboardHandler != null)
            keyboardRoot.RemoveHandler(PreviewKeyDownEvent, keyboardHandler);
        keyboardRoot = null;
        keyboardHandler = null;
        if (captionPointerSource != null)
        {
            captionPointerSource.CaptionTapped -= Caption_Tapped;
            captionPointerSource.ExitedMoveSize -= Caption_ExitedMoveSize;
            captionPointerSource = null;
        }
        App.Window.Activated -= Window_Activated;
        activeInfoDialog?.Hide();
        activeAudioSettingsDialog?.Hide();
        activeConnectionDialog?.Hide();
        activeErrorDialog?.Hide();
        CloseBrowser(BrowserCloseReason.WindowClosed);
        browserCancellation?.Cancel();
        StopPlayback();
        transportControls?.Dispose();
        transportControls = null;
        interruptionMonitor?.Dispose();
        interruptionMonitor = null;
        inputMonitor?.Dispose();
        inputMonitor = null;
        ((MainWindow)App.Window).FullscreenChanged -= Window_FullscreenChanged;
        DisposeCursorAutoHide();
        dlna.Dispose();
    }
}
