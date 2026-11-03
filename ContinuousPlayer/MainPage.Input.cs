using ContinuousPlayer.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private Interop.PlaybackInputMonitor? inputMonitor;
    private UIElement? keyboardRoot;
    private KeyEventHandler? keyboardHandler;
    private InputNonClientPointerSource? captionPointerSource;
    private readonly PlaybackGestureRecognizer gestures = new();
    private readonly HashSet<uint> rootTouches = [];

    private void InitializeInput()
    {
        // Include the window title bar, which is a sibling of this page.
        keyboardRoot = App.Window.Content;
        keyboardHandler = Root_KeyDown;
        keyboardRoot.AddHandler(PreviewKeyDownEvent, keyboardHandler, true);
        captionPointerSource = InputNonClientPointerSource.GetForWindowId(App.Window.AppWindow.Id);
        captionPointerSource.CaptionTapped += Caption_Tapped;
        captionPointerSource.ExitedMoveSize += Caption_ExitedMoveSize;
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(RootPointerPressed), true);
        Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(RootPointerEnded), true);
        Root.AddHandler(PointerCanceledEvent, new PointerEventHandler(RootPointerEnded), true);
        Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(RootPointerEnded), true);
        var doubleClick = Interop.PlaybackInputMonitor.DoubleClickMilliseconds;
        gestures.MouseDoubleClickMilliseconds = doubleClick > 0 ? doubleClick : 300;
        // Windows' ordinary mouse double-click default is 500ms. Direct touch uses
        // the common 300ms baseline, while explicit OS timing adjustments take precedence.
        gestures.TouchDoubleTapMilliseconds = doubleClick is > 0 and not 500 ? doubleClick : 300;
    }

    private void FocusPlaybackSurface()
    {
        if (windowActive && PlaybackInputAllowed && !ControlsHaveKeyboardFocus)
            TouchSurface.Focus(FocusState.Programmatic);
    }

    private void Caption_Tapped(InputNonClientPointerSource sender, NonClientCaptionTappedEventArgs args) =>
        RestorePlaybackFocusAfterCaption();

    private void Caption_ExitedMoveSize(InputNonClientPointerSource sender, ExitedMoveSizeEventArgs args) =>
        RestorePlaybackFocusAfterCaption();

    private void RestorePlaybackFocusAfterCaption()
    {
        // Caption input bypasses XAML. Restore island focus after native click/move handling finishes.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (windowActive && PlaybackInputAllowed) TouchSurface.Focus(FocusState.Programmatic);
        });
    }

    private void RootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
        rootTouches.Add(e.Pointer.PointerId);
        if (rootTouches.Count > 1) gestures.Invalidate();
    }
    private void RootPointerEnded(object sender, PointerRoutedEventArgs e) => rootTouches.Remove(e.Pointer.PointerId);

    private static bool IsTextEntry(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is TextBox or PasswordBox or RichEditBox or AutoSuggestBox) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static PlaybackInputKey InputKey(VirtualKey key) => key switch
    {
        VirtualKey.Left => PlaybackInputKey.Left, VirtualKey.Right => PlaybackInputKey.Right,
        VirtualKey.Up => PlaybackInputKey.Up, VirtualKey.Down => PlaybackInputKey.Down,
        VirtualKey.Space => PlaybackInputKey.Space, VirtualKey.Enter => PlaybackInputKey.Enter,
        VirtualKey.Escape => PlaybackInputKey.Escape, VirtualKey.I => PlaybackInputKey.I,
        VirtualKey.O => PlaybackInputKey.O, VirtualKey.H => PlaybackInputKey.H, _ => PlaybackInputKey.Other
    };

    private async Task ExecutePlaybackCommandAsync(PlaybackCommand command)
    {
        TracePlaybackSession("command", command);
        if (!PlaybackInputAllowed) return;
        switch (command)
        {
            case PlaybackCommand.Toggle: SetPaused(!userPaused); break;
            case PlaybackCommand.Play: SetPaused(false); break;
            case PlaybackCommand.Pause: SetPaused(true); break;
            case PlaybackCommand.Previous: await MoveMediaAsync(-1); break;
            case PlaybackCommand.Next: await MoveMediaAsync(1); break;
            case PlaybackCommand.SeekBackward: await SeekAsync(-10); break;
            case PlaybackCommand.SeekForward: await SeekAsync(10); break;
            case PlaybackCommand.Files: await ReturnToFolderAsync(); break;
            case PlaybackCommand.Information: await ShowInfoAsync(); break;
            case PlaybackCommand.Home: GoHome(); break;
        }
    }

    private async Task ExecutePlaybackKeyboardCommandAsync(PlaybackCommand command)
    {
        KeyboardPlaybackActivity();
        await ExecutePlaybackCommandAsync(command);
        KeyboardPlaybackActivity();
    }

    private void MoveToolbarFocus(VirtualKey key)
    {
        var direction = key switch
        {
            VirtualKey.Left => FocusNavigationDirection.Left, VirtualKey.Right => FocusNavigationDirection.Right,
            VirtualKey.Up => FocusNavigationDirection.Up, _ => FocusNavigationDirection.Down
        };
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        FocusManager.TryMoveFocus(direction, new FindNextElementOptions
        {
            SearchRoot = IsWithin(focused, WindowedTopToolbar) ? WindowedTopToolbar : PauseToolbar
        });
    }

    private void PageBrowser(int direction)
    {
        if (browserBusy || Vm.Rows.Count == 0) return;
        BrowserList.UpdateLayout();
        var visible = BrowserList.ItemsPanelRoot is ItemsStackPanel panel && panel.FirstVisibleIndex >= 0
            ? panel.LastVisibleIndex - panel.FirstVisibleIndex + 1 : 1;
        var next = PlaybackInput.PageSelection(BrowserList.SelectedIndex, Vm.Rows.Count, visible, direction);
        if (next >= 0) SelectRow(Vm.Rows[next].Id);
    }
}
