using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private Interop.PlaybackInterruptionMonitor? interruptionMonitor;
    private PlaybackSessionState.Suspension? browserSuspension;
    private ContentDialog? activeInfoDialog;
    private (string Message, int Code)? pendingFailure;
    private bool playerHasPlayed;
    private bool expectedEnginePause;
    private bool PlaybackInputAllowed => !disposed && isPlayback && !dialogOpen &&
        !playbackState.HasTemporaryPause && BrowserOverlay.Visibility != Visibility.Visible;

    private void SetPlaybackInputEnabled(bool enabled)
    {
        TouchSurface.IsEnabled = enabled;
        UpdatePlaybackButtons();
    }

    private void ApplyPlaybackState(bool focus = false)
    {
        if (disposed || !isPlayback || mediaPlayer == null) return;
        if (playbackState.CanPlay) mediaPlayer.Play();
        else
        {
            if (mediaPlayer.PlaybackSession.PlaybackState != Windows.Media.Playback.MediaPlaybackState.Paused)
                expectedEnginePause = true;
            mediaPlayer.Pause();
        }
        UpdatePlaybackButtons();
        UpdateTransportState();
        UpdateCursorAutoHide();
        if (focus && !dialogOpen && BrowserOverlay.Visibility != Visibility.Visible)
        {
            if (userPaused) ResumeButton.Focus(FocusState.Programmatic);
            else TouchSurface.Focus(FocusState.Programmatic);
        }
    }

    private void RestoreSuspension(PlaybackSessionState.Suspension? saved)
    {
        var restored = playbackState.Restore(saved);
        if (!restored.Restored || disposed || mediaPlayer == null) return;
        if (queue.Current is { } current && !IsNetworkMedia(current.Location) && !File.Exists(current.Location))
        {
            playbackState.Fail();
            Vm.PlaybackStatus = "元のファイルにアクセスできません。ドライブの再接続、アクセス許可、またはファイル一覧からの再選択を確認してください。";
        }
        if (playbackState.Phase == PlaybackPhase.Ready && mediaPlayer.PlaybackSession.CanSeek)
        {
            var session = mediaPlayer.PlaybackSession;
            var position = restored.Position;
            if (session.NaturalDuration > TimeSpan.Zero && position > session.NaturalDuration)
                position = session.NaturalDuration;
            session.Position = position;
        }
        ApplyPlaybackState(true);
    }

    private static bool IsNetworkMedia(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void PlaybackInterruptionChanged(PlaybackInterruption reason, bool active)
    {
        if (disposed) return;
        playbackState.SetInterruption(reason, active);
        ApplyPlaybackState();
    }

    private async Task ShowPendingFailureAsync()
    {
        if (disposed || !isPlayback || dialogOpen || BrowserOverlay.Visibility == Visibility.Visible || pendingFailure == null) return;
        pendingFailure = null;
        await ShowFailureDialogAsync();
    }
}
