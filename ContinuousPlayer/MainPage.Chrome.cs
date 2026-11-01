using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private bool playbackChromeRequested;
    private bool playbackWasPaused = true;

    private Button[] PlaybackButtons => [PlayerHomeButton, FilesButton, InfoButton, FullscreenButton,
        WindowedHomeButton, WindowedFilesButton, WindowedInfoButton,
        PreviousButton, RewindButton, ResumeButton, ForwardButton, NextButton];

    private void InitializePlaybackChrome()
    {
        // Mount once, before automation peers are created. Keep each header in a stable parent.
        Root.Children.Remove(WindowedTopToolbar);
        ((MainWindow)App.Window).SetPlaybackTitleBar(WindowedTopToolbar, false, "", "");
        Vm.PropertyChanged += PlaybackTitleBar_PropertyChanged;
        UpdatePlaybackButtons();
    }

    // UIA listeners can belong to inspectors; their presence alone is not screen-reader activity.
    private bool AccessibilityControlsActive =>
        SystemParametersInfoW(0x46, 0, out var reader, 0) && reader;

    private bool ControlsHaveFocus => XamlRoot != null &&
        IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, PauseToolbar);

    private bool PlaybackToolbarHasFocus => XamlRoot != null &&
        (ControlsHaveFocus || IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, WindowedTopToolbar));

    private bool ControlsHaveKeyboardFocus => ControlsHaveFocus &&
        FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard };

    private void ShowPlaybackChrome()
    {
        playbackChromeRequested = true;
        if (isPlayback) PauseToolbar.Visibility = Visibility.Visible;
        lastMouseActivity = System.Diagnostics.Stopwatch.GetTimestamp();
        SetPlaybackCursorHidden(false);
    }

    private void RefreshPlaybackChrome()
    {
        if (!isPlayback) return;
        if (CanHidePlaybackChrome && (!playbackChromeRequested ||
            System.Diagnostics.Stopwatch.GetElapsedTime(lastMouseActivity) >= CursorIdleDelay))
            HidePlaybackChromeIfAllowed();
        else
        {
            PauseToolbar.Visibility = Visibility.Visible;
            SetPlaybackCursorHidden(false);
        }
    }

    private void KeyboardPlaybackActivity()
    {
        playbackChromeRequested = false;
        if (PlaybackInputAllowed && !userPaused && windowActive && ControlsHaveFocus)
            TouchSurface.Focus(FocusState.Programmatic);
        RefreshPlaybackChrome();
    }

    private void HidePlaybackChromeIfAllowed()
    {
        if (!CanHidePlaybackChrome) return;
        // A mouse click can leave focus on a button. Keep keyboard input on the video when it hides.
        if (windowActive && ControlsHaveFocus) TouchSurface.Focus(FocusState.Programmatic);
        PauseToolbar.Visibility = Visibility.Collapsed;
        SetPlaybackCursorHidden(windowActive && PointerIsOverPlayback());
    }

    private void Chrome_FocusChanged(object sender, RoutedEventArgs e)
    {
        if (ControlsHaveFocus) ShowPlaybackChrome();
    }

    private void UpdatePlaybackButtons()
    {
        var allowed = PlaybackInputAllowed;
        foreach (var button in PlaybackButtons) button.IsEnabled = allowed;
        PreviousButton.IsEnabled = allowed && queue.Index > 0;
        NextButton.IsEnabled = allowed && queue.Index + 1 < queue.Items.Count;
        RewindButton.IsEnabled = ForwardButton.IsEnabled = allowed && CanSeekCurrent;
        ResumeButton.IsEnabled = allowed;
        var label = playbackState.Phase == PlaybackPhase.Failed ? "再試行" : userPaused ? "再生" : "一時停止";
        AutomationProperties.SetName(ResumeButton, label);
        ToolTipService.SetToolTip(ResumeButton, label);
        PlayPauseIcon.Glyph = userPaused ? "\uE768" : "\uE769";
    }

    private void Window_FullscreenChanged(object? sender, EventArgs e)
    {
        UpdateFullscreenButtons();
        UpdateCursorAutoHide();
        if (isPlayback && !dialogOpen && BrowserOverlay.Visibility != Visibility.Visible)
            TouchSurface.Focus(FocusState.Programmatic);
    }

    private void UpdateFullscreenButtons()
    {
        var full = ((MainWindow)App.Window).IsFullscreen;
        FullscreenButton.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        HomeFullscreenButton.Visibility = full && !isPlayback ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaybackToolbarPlacement();
    }

    private void UpdatePlaybackToolbarPlacement()
    {
        var window = (MainWindow)App.Window;
        var titleBar = isPlayback && !window.IsFullscreen;
        window.SetPlaybackTitleBar(WindowedTopToolbar, titleBar, Vm.PlaybackTitle, Vm.PlaybackStatus, Vm.FileName);
        TopToolbarOverlayHost.Visibility = titleBar ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlaybackTitleBar_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Vm.PlaybackTitle) or nameof(Vm.PlaybackStatus))
            UpdatePlaybackToolbarPlacement();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool value, uint flags);
}
