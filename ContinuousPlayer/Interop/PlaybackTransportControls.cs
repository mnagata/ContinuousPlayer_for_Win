using ContinuousPlayer.Core;
using Windows.Media;

namespace ContinuousPlayer.Interop;

/// <summary>An HWND-owned SMTC session, independent of the current playback engine.</summary>
internal sealed class PlaybackTransportControls : IDisposable
{
    private readonly SystemMediaTransportControls controls;
    private readonly Func<int, bool> acceptKey;
    private readonly Action<PlaybackCommand> execute;
    private bool disposed;

    public PlaybackTransportControls(nint hwnd, Func<int, bool> acceptKey, Action<PlaybackCommand> execute)
    {
        this.acceptKey = acceptKey;
        this.execute = execute;
        controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        controls.IsEnabled = false;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.ButtonPressed += ButtonPressed;
    }

    public void Update(bool active, bool paused, bool previous, bool next, string title, bool hasVideo)
    {
        if (disposed) return;
        controls.IsEnabled = active;
        if (!active)
        {
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            return;
        }
        controls.IsPreviousEnabled = previous;
        controls.IsNextEnabled = next;
        controls.PlaybackStatus = paused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
        var updater = controls.DisplayUpdater;
        updater.Type = hasVideo ? MediaPlaybackType.Video : MediaPlaybackType.Music;
        if (hasVideo) updater.VideoProperties.Title = title;
        else updater.MusicProperties.Title = title;
        updater.Update();
    }

    private void ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (disposed) return;
        Playback.AudioPlaybackLog.Session("transport-button", new { Button = args.Button });
        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => PlaybackCommand.Play,
            SystemMediaTransportControlsButton.Pause or SystemMediaTransportControlsButton.Stop => PlaybackCommand.Pause,
            SystemMediaTransportControlsButton.Previous => PlaybackCommand.Previous,
            SystemMediaTransportControlsButton.Next => PlaybackCommand.Next,
            SystemMediaTransportControlsButton.Rewind => PlaybackCommand.SeekBackward,
            SystemMediaTransportControlsButton.FastForward => PlaybackCommand.SeekForward,
            _ => PlaybackCommand.None
        };
        var key = args.Button switch
        {
            SystemMediaTransportControlsButton.Next => 0xB0, SystemMediaTransportControlsButton.Previous => 0xB1,
            SystemMediaTransportControlsButton.Stop => 0xB2,
            SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Pause => 0xB3, _ => 0
        };
        // Preserve the physical-key latch before dispatching to the UI thread.
        if (key != 0 && !acceptKey(key)) return;
        execute(command);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        controls.ButtonPressed -= ButtonPressed;
        controls.IsEnabled = false;
        controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        controls.DisplayUpdater.ClearAll();
    }
}
