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
        if (disposed || !isPlayback) return;
        TracePlaybackSession("apply");
        if (!playbackState.CanPlay && playbackEngine != null && playbackEngine.State != PlaybackEngineState.Paused)
            expectedEnginePause = true;
        engineSession.Apply(playbackState);
        UpdateAppliedAudioStatus();
        UpdatePlaybackButtons();
        UpdateTransportState();
        UpdateCursorAutoHide();
        if (focus && !dialogOpen && BrowserOverlay.Visibility != Visibility.Visible)
        {
            // Pause and dialog restoration keep surface shortcuts available.
            // Explicit keyboard navigation of the toolbar retains its focus.
            FocusPlaybackSurface();
        }
    }

    private void RestoreSuspension(PlaybackSessionState.Suspension? saved)
    {
        var restored = playbackState.Restore(saved);
        if (!restored.Restored || disposed || playbackEngine == null) return;
        if (queue.Current is { } current && !IsNetworkMedia(current.Location) && !File.Exists(current.Location))
        {
            playbackState.Fail();
            Vm.PlaybackStatus = "元のファイルにアクセスできません。ドライブの再接続、アクセス許可、またはファイル一覧からの再選択を確認してください。";
        }
        if (playbackState.Phase == PlaybackPhase.Ready) engineSession.RestorePosition(restored.Position);
        ApplyPlaybackState(true);
    }

    private static bool IsNetworkMedia(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private void PlaybackInterruptionChanged(PlaybackInterruption reason, bool active)
    {
        if (disposed) return;
        TracePlaybackSession("interruption", new { reason, active });
        playbackState.SetInterruption(reason, active);
        ApplyPlaybackState();
        if (isPlayback && playbackState.Phase == PlaybackPhase.Ready &&
            (active || !playbackState.WantsPlayback))
            Vm.PlaybackStatus = active && reason == PlaybackInterruption.AudioOutputUnavailable
                ? "出力機器を利用できません" : "中断後は再生ボタンで再開";
    }

    private void TracePlaybackSession(string kind, object? cause = null)
    {
        if (App.AudioOptions.LogPath == null) return;
        global::ContinuousPlayer.Playback.AudioPlaybackLog.Session(kind, new { cause, playbackState.Stamp,
            playbackState.Phase, playbackState.WantsPlayback, playbackState.CanPlay,
            playbackState.HasTemporaryPause, playbackState.Interruptions, expectedEnginePause,
            EngineState = playbackEngine?.State, Position = playbackEngine?.Position });
    }

    private async Task ShowPendingFailureAsync()
    {
        if (disposed || !isPlayback || dialogOpen || BrowserOverlay.Visibility == Visibility.Visible || pendingFailure == null) return;
        pendingFailure = null;
        await ShowFailureDialogAsync();
    }
}
