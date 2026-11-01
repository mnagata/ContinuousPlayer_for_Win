using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private Button[] PlaybackButtons => [PlayerHomeButton, FilesButton, InfoButton, FullscreenButton,
        PreviousButton, RewindButton, ResumeButton, ForwardButton, NextButton];

    private void InitializePlaybackChrome()
    {
        // UIA focus listeners include Narrator, which does not set SPI_GETSCREENREADER.
        // Keep commands available for any interested accessibility client.
        UpdatePlaybackButtons();
    }

    private bool AccessibilityControlsActive =>
        AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged) ||
        (SystemParametersInfoW(0x46, 0, out var reader, 0) && reader);

    private bool ControlsHaveFocus => XamlRoot != null &&
        IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, PauseToolbar);

    private bool PointerOverPlaybackButton()
    {
        if (PauseToolbar.Visibility != Visibility.Visible || XamlRoot is not { } root || !GetCursorPos(out var point)) return false;
        if (!PointerIsOverPlayback() || !ScreenToClient(cursorWindow, ref point)) return false;
        var x = point.X / root.RasterizationScale;
        var y = point.Y / root.RasterizationScale;
        return PlaybackButtons.Where(b => b.Visibility == Visibility.Visible).Any(b =>
        {
            var origin = b.TransformToVisual(root.Content).TransformPoint(default);
            return x >= origin.X && y >= origin.Y && x < origin.X + b.ActualWidth && y < origin.Y + b.ActualHeight;
        });
    }

    private void ShowPlaybackChrome()
    {
        if (isPlayback) PauseToolbar.Visibility = Visibility.Visible;
        lastMouseActivity = System.Diagnostics.Stopwatch.GetTimestamp();
        SetPlaybackCursorHidden(false);
    }

    private void HidePlaybackChromeIfAllowed()
    {
        if (!CanHidePlaybackCursor) return;
        PauseToolbar.Visibility = Visibility.Collapsed;
        SetPlaybackCursorHidden(true);
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
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint parameter,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool value, uint flags);
}
