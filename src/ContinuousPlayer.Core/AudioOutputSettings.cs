using System.Text.Json;

namespace ContinuousPlayer.Core;

public sealed record AudioOutputSettings(bool Exclusive = false, string? DeviceId = null, string? DeviceName = null);

/// <summary>Separate from library.json so existing folder registrations keep their schema.</summary>
public sealed class AudioOutputSettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "audio-settings.json");
    private readonly SemaphoreSlim writing = new(1, 1);
    public string? LoadWarning { get; private set; }

    public async Task<AudioOutputSettings> LoadAsync()
    {
        LoadWarning = null;
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path));
            if (state is not { Version: 1, Settings: { } settings } ||
                settings.DeviceId is { } id && (string.IsNullOrWhiteSpace(id) || id.Contains('\0')))
                throw new InvalidDataException("音声出力の保存形式が不正です。");
            return settings;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadWarning = "音声出力設定を読み込めないため標準再生を使用します。元の保存ファイルは保持しています。";
            return new();
        }
    }

    public async Task SaveAsync(AudioOutputSettings settings)
    {
        await writing.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new State(1, settings), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { writing.Release(); }
    }

    private sealed record State(int Version, AudioOutputSettings? Settings);
}
