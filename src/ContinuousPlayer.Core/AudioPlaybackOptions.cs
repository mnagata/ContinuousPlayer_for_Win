namespace ContinuousPlayer.Core;

/// <summary>Session overrides and optional diagnostics; normal launches use saved settings.</summary>
public sealed record AudioPlaybackOptions(bool Exclusive = false, string? DeviceId = null, string? LogPath = null, bool Standard = false)
{
    public static AudioPlaybackOptions Parse(IReadOnlyList<string> arguments)
    {
        var exclusive = false;
        var standard = false;
        string? device = null, log = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            string Value()
            {
                if (++index >= arguments.Count || arguments[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("音声出力オプションの値がありません。");
                return arguments[index];
            }
            switch (arguments[index])
            {
                case "--exclusive-audio": exclusive = true; break;
                case "--standard-audio": standard = true; break;
                case "--audio-device": device = Value(); break;
                case "--audio-log": log = Value(); break;
            }
        }
        if (device != null && !exclusive) throw new ArgumentException("--audio-device は --exclusive-audio と同時に指定してください。");
        if (exclusive && standard) throw new ArgumentException("標準と排他を同時に指定できません。");
        return new(exclusive, device, log, standard);
    }

    public bool ProbesLocalAudio(string location) => Exclusive && Path.IsPathFullyQualified(location) &&
        !location.StartsWith(@"\\", StringComparison.Ordinal) &&
        new[] { ".wav", ".flac", ".m4a", ".mp3", ".aac", ".ogg", ".opus" }.Contains(Path.GetExtension(location), StringComparer.OrdinalIgnoreCase);

    public AudioOutputSettings ApplyTo(AudioOutputSettings saved) => Exclusive ? new(true, DeviceId)
        : Standard ? saved with { Exclusive = false } : saved;

    public bool ProbesAudio(MediaFile file)
    {
        if (!Exclusive || file.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return false;
        if (ProbesLocalAudio(file.Location)) return true;
        if (!Uri.TryCreate(file.Location, UriKind.Absolute, out var uri) || !DlnaAddress.IsHttpTarget(uri)) return false;
        return file.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
            IsAudioName(file.Name) || IsAudioName(uri.AbsolutePath);
    }
    private static bool IsAudioName(string name) => new[] { ".wav", ".flac", ".m4a", ".mp3", ".aac", ".ogg", ".opus" }
        .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
}
