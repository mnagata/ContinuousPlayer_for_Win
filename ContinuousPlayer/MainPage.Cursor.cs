using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ContinuousPlayer.Core;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private static readonly TimeSpan CursorIdleDelay = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer cursorIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly InputSystemCursor visibleCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly InputCursor hiddenCursor = Interop.PlaybackCursor.CreateTransparent();
    private nint cursorWindow;
    private bool windowActive = true;
    private bool cursorHidden;
    private CursorPoint? lastMousePosition;
    private long lastMouseActivity;

    private void InitializeCursorAutoHide()
    {
        cursorWindow = App.WindowHandle;
        ProtectedCursor = visibleCursor;
        TouchSurface.SetCursor(visibleCursor);
        cursorIdleTimer.Tick += CursorIdleTimer_Tick;
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(Cursor_PointerButtonActivity), true);
    }

    private bool CanHidePlaybackChrome => !disposed && isPlayback &&
        PlaybackPresentation.CanAutoHide(playbackEngine?.HasVideo == true, userPaused,
            dialogOpen || BrowserOverlay.Visibility == Visibility.Visible, windowActive && ControlsHaveKeyboardFocus,
            AccessibilityControlsActive, preparing: playbackState.Phase == PlaybackPhase.Preparing);

    private void Cursor_PointerButtonActivity(object sender, PointerRoutedEventArgs e)
    {
        if (!disposed && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse) ShowPlaybackChrome();
    }

    private void SetPlaybackCursorHidden(bool hidden)
    {
        if (cursorHidden == hidden || hidden && !PointerIsOverPlayback()) return;
        cursorHidden = hidden;
        // WinUI treats null as "no override", so use an explicit transparent resource.
        InputCursor cursor = hidden ? hiddenCursor : visibleCursor;
        ProtectedCursor = cursor;
        TouchSurface.SetCursor(cursor);
        if (XamlRoot?.ContentIsland is { } island) InputPointerSource.GetForIsland(island).Cursor = cursor;
        if (hidden && rootTouches.Count == 0 && !gestures.HasActiveContact) Interop.PlaybackCursor.RefreshPointer(cursorWindow);
    }

    private void UpdateCursorAutoHide()
    {
        cursorIdleTimer.Stop();
        if (!isPlayback)
        {
            playbackWasPaused = true;
            playbackChromeRequested = false;
            SetPlaybackCursorHidden(false);
            return;
        }
        // Resuming video clears any pause menu immediately, including mouse/keyboard button focus.
        if (playbackWasPaused && !userPaused)
        {
            playbackChromeRequested = false;
            if (windowActive && ControlsHaveFocus) TouchSurface.Focus(FocusState.Programmatic);
        }
        playbackWasPaused = userPaused;
        lastMousePosition = GetCursorPos(out var point) ? point : null;
        RefreshPlaybackChrome();
        cursorIdleTimer.Start();
    }

    private void CursorIdleTimer_Tick(object? sender, object e)
    {
        if (!isPlayback || disposed) { UpdateCursorAutoHide(); return; }
        RecordConfirmedPosition();
        CheckPlaybackProgress();
        if (ForwardButton.IsEnabled != (PlaybackInputAllowed && CanSeekCurrent))
            UpdatePlaybackButtons();
        if (!CanHidePlaybackChrome) { RefreshPlaybackChrome(); return; }
        if (!windowActive || !PointerIsOverPlayback() || !GetCursorPos(out var point))
        {
            lastMousePosition = null;
            SetPlaybackCursorHidden(false);
            // Leaving the video must not suspend the overlay's idle countdown.
            RefreshPlaybackChrome();
            return;
        }
        // Use actual coordinates, not synthetic/missing routed move events.
        if (lastMousePosition is not { } previous || previous.X != point.X || previous.Y != point.Y ||
            (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0)
        {
            lastMousePosition = point;
            lastMouseActivity = Stopwatch.GetTimestamp();
            ShowPlaybackChrome();
        }
        else RefreshPlaybackChrome();
    }

    private void Player_PlaybackStateChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (disposed || sender is not IPlaybackEngine engine || playbackEngine != engine) return;
        TracePlaybackSession("engine-state");
        UpdateAppliedAudioStatus();
        if (engine.State == PlaybackEngineState.Playing)
        {
            playerHasPlayed = true;
            expectedEnginePause = false;
        }
        else if (engine.State == PlaybackEngineState.Paused && expectedEnginePause)
            expectedEnginePause = false;
        else if (engine.State == PlaybackEngineState.Paused && playerHasPlayed && playbackState.CanPlay)
        {
            var stamp = playbackState.Stamp;
            // Let a queued MediaEnded run first. Otherwise a natural end can be
            // mistaken for an interruption and suppress continuous playback.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (disposed || !playbackState.IsCurrent(stamp) || playbackEngine != engine ||
                    engine.State != PlaybackEngineState.Paused || expectedEnginePause) return;
                playbackState.ObserveUnexpectedPause(engine.Position, engine.Duration);
                TracePlaybackSession("unexpected-pause");
                if (!playbackState.CanPlay) ApplyPlaybackState();
            });
        }
        // Buffering is still playback, and must not reveal a pause menu by itself.
        if (engine.State != PlaybackEngineState.Buffering) UpdateCursorAutoHide();
    });

    private void DisposeCursorAutoHide()
    {
        cursorIdleTimer.Stop();
        cursorIdleTimer.Tick -= CursorIdleTimer_Tick;
        SetPlaybackCursorHidden(false);
        ProtectedCursor = null;
        TouchSurface.SetCursor(null);
        if (XamlRoot?.ContentIsland is { } island) InputPointerSource.GetForIsland(island).Cursor = null;
        visibleCursor.Dispose();
        hiddenCursor.Dispose();
    }

    private bool PointerIsOverPlayback()
    {
        if (XamlRoot is not { } root || !GetCursorPos(out var point)) return false;
        var target = WindowFromPoint(point);
        if (target != cursorWindow && !IsChild(cursorWindow, target)) return false;
        if (!ScreenToClient(cursorWindow, ref point)) return false;
        var origin = Playback.TransformToVisual(root.Content).TransformPoint(default);
        var x = point.X / root.RasterizationScale - origin.X;
        var y = point.Y / root.RasterizationScale - origin.Y;
        return x >= 0 && y >= 0 && x < Playback.ActualWidth && y < Playback.ActualHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ScreenToClient(nint window, ref CursorPoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
