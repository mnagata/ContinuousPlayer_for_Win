using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private readonly AudioOutputSettingsStore audioSettingsStore = new(Windows.Storage.ApplicationData.Current.LocalFolder.Path);
    private AudioOutputSettings audioSettings = new();
    private bool audioSettingsReady;
    private AudioSettingsDialog? activeAudioSettingsDialog;
    private bool standardForCurrent;
    private string standardReason = "";
    private PlaybackOutputInfo? lastInitializedOutput;

    private async void AudioSettings_Click(object sender, RoutedEventArgs e) => await ShowAudioSettingsAsync();

    private async Task ShowAudioSettingsAsync()
    {
        if (!audioSettingsReady || disposed || dialogOpen || BrowserOverlay.Visibility == Visibility.Visible || isPlayback && !PlaybackInputAllowed) return;
        var saved = isPlayback ? playbackState.Suspend(CurrentPosition) : null;
        if (isPlayback) ApplyPlaybackState();
        dialogOpen = true;
        UpdatePlaybackButtons(); UpdateCursorAutoHide();
        var previous = audioSettings;
        var dialog = new AudioSettingsDialog(audioSettings, audioSettingsStore.SaveAsync) { XamlRoot = XamlRoot };
        activeAudioSettingsDialog = dialog;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { activeAudioSettingsDialog = null; dialogOpen = false; }
        if (disposed) return;
        if (result == ContentDialogResult.Primary) audioSettings = dialog.Settings;
        var changed = previous.Exclusive != audioSettings.Exclusive || previous.DeviceId != audioSettings.DeviceId;
        if (saved != null && changed && playbackState.IsActive)
        {
            var restored = playbackState.Restore(saved);
            if (restored.Restored && queue.Current is { } current)
            {
                playbackState.RequestPlayback(false);
                recovery.Observe(current.Id, restored.Position);
                standardForCurrent = false;
                await LoadCurrentAsync(true);
            }
        }
        else if (saved != null) RestoreSuspension(saved);
        UpdatePlaybackButtons(); UpdateCursorAutoHide();
        if (!isPlayback) ChooseButton.Focus(FocusState.Programmatic);
        await ShowPendingFailureAsync();
    }

    private string RequestedAudioMode => audioSettings.Exclusive ? "曲に合わせる（排他）" : "標準";
}
