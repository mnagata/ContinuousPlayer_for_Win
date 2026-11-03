using System.Text.Json;
using System.Text.Json.Serialization;
using ContinuousPlayer.Audio.Windows;

namespace ContinuousPlayer.Playback;

internal static class AudioPlaybackLog
{
    private static readonly object gate = new();
    private static readonly JsonSerializerOptions options = new() { Converters = { new JsonStringEnumConverter() } };
    public static void Write(AudioPlaybackTrace entry)
        => WriteEntry(entry);

    public static void Session(string kind, object detail)
        => WriteEntry(new { Event = "session-" + kind, Timestamp = DateTimeOffset.UtcNow, Detail = detail });

    private static void WriteEntry(object entry)
    {
        if (App.AudioOptions.LogPath is not { } log) return;
        try
        {
            lock (gate)
            {
                var path = Path.GetFullPath(log);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, options) + Environment.NewLine);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
