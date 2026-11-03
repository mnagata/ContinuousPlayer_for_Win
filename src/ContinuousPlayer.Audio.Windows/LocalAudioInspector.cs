namespace ContinuousPlayer.Audio.Windows;

public static class LocalAudioInspector
{
    public static Task<LocalAudioInfo> InspectAsync(string path, CancellationToken token = default) =>
        PcmDecoder.InspectAsync(path, token);

    public static Task<LocalAudioInfo> InspectAsync(ContinuousPlayer.Core.PlaybackSource source, CancellationToken token = default) =>
        PcmDecoder.InspectAsync(source.Location, token, source.MimeType);
}
