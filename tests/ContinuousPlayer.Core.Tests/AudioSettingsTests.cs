using ContinuousPlayer.Core;

internal static class AudioSettingsTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Audio defaults are standard and saved DAC identity survives mode changes", async () =>
        {
            var directory = Temporary();
            try
            {
                var store = new AudioOutputSettingsStore(directory);
                Equal(new AudioOutputSettings(), await store.LoadAsync());
                var settings = new AudioOutputSettings(true,"dac-exact-id","USB DAC");
                await store.SaveAsync(settings);
                Equal(settings, await new AudioOutputSettingsStore(directory).LoadAsync());
                await store.SaveAsync(settings with { Exclusive = false });
                Equal(settings with { Exclusive = false }, await new AudioOutputSettingsStore(directory).LoadAsync());
            }
            finally { Directory.Delete(directory,true); }
        });
        yield return ("Invalid audio settings preserve source and never change folder data", async () =>
        {
            var directory = Temporary();
            try
            {
                var library = Path.Combine(directory,"library.json");
                await File.WriteAllTextAsync(library,"existing library bytes");
                foreach (var invalid in new[] { "{", "null", "{\"Version\":99}", "{\"Version\":1,\"Settings\":null}", "{\"Version\":1,\"Settings\":{\"Exclusive\":true,\"DeviceId\":\" \"}}" })
                {
                    var path = Path.Combine(directory,"audio-settings.json");
                    await File.WriteAllTextAsync(path,invalid);
                    var store = new AudioOutputSettingsStore(directory);
                    Equal(new AudioOutputSettings(), await store.LoadAsync());
                    True(store.LoadWarning != null); Equal(invalid, await File.ReadAllTextAsync(path));
                }
                await new AudioOutputSettingsStore(directory).SaveAsync(new(true,"missing-dac","Missing DAC"));
                Equal("existing library bytes", await File.ReadAllTextAsync(library));
            }
            finally { Directory.Delete(directory,true); }
        });
        yield return ("Launch overrides do not silently inherit a saved specific DAC", () =>
        {
            var saved = new AudioOutputSettings(true,"saved-dac","Saved");
            Equal(saved, AudioPlaybackOptions.Parse([]).ApplyTo(saved));
            Equal(saved with { Exclusive=false }, AudioPlaybackOptions.Parse(["--standard-audio"]).ApplyTo(saved));
            Equal(new AudioOutputSettings(true), AudioPlaybackOptions.Parse(["--exclusive-audio"]).ApplyTo(saved));
            Equal(new AudioOutputSettings(true,"chosen"), AudioPlaybackOptions.Parse(["--exclusive-audio","--audio-device","chosen"]).ApplyTo(saved));
            try { AudioPlaybackOptions.Parse(["--standard-audio","--exclusive-audio"]); throw new Exception("Conflicting modes accepted."); }
            catch (ArgumentException) { }
            return Task.CompletedTask;
        });
        yield return ("Exclusive error causes have separate actionable guidance", () =>
        {
            True(AudioFailureGuidance.Describe(unchecked((int)0x8889000A)).Contains("他のアプリ"));
            True(AudioFailureGuidance.Describe(unchecked((int)0x8889000E)).Contains("許可"));
            True(AudioFailureGuidance.Describe(unchecked((int)0x88890004)).Contains("自動出力しません"));
            True(AudioFailureGuidance.Describe(unchecked((int)0x88890008)).Contains("同じレート"));
            True(AudioFailureGuidance.Describe(unchecked((int)0xC00D36C4)).Contains("デコーダー"));
            return Task.CompletedTask;
        });
        yield return ("Audio disappearance and sleep while settings are open never resume on dismissal", () =>
        {
            foreach (var reason in new[] { PlaybackInterruption.AudioOutputUnavailable, PlaybackInterruption.Suspended })
            {
                var state = new PlaybackSessionState(); state.Start(); state.Ready();
                var saved = state.Suspend(TimeSpan.FromSeconds(12));
                state.SetInterruption(reason,true); state.SetInterruption(reason,false);
                True(state.Restore(saved).Restored); True(!state.CanPlay && !state.WantsPlayback);
                state.RequestPlayback(true); True(state.CanPlay);
            }
            return Task.CompletedTask;
        });
    }
    private static string Temporary() { var path = Path.Combine(Path.GetTempPath(),"ContinuousPlayerAudio-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected,T actual) { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
}
