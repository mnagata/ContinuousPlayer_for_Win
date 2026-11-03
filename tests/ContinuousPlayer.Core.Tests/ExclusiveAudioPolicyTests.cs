using ContinuousPlayer.Core;

static class ExclusiveAudioPolicyTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Exclusive candidates preserve rate, channels and precision including integer32 widening", () =>
        {
            foreach (var rate in new[] { 44100, 48000, 88200, 96000, 176400, 192000 })
            foreach (var bits in new[] { 16, 24, 32 })
            {
                var source = new PcmFormat(rate, 2, PcmEncoding.Integer, bits, bits, 3);
                var candidates = ExclusiveAudioPolicy.Candidates(source);
                True(candidates.Count > 0);
                True(candidates.All(x => x.Format.SampleRate == rate && x.Format.Channels == 2 &&
                    x.Format.ValidBits >= bits && x.Format.ContainerBits >= bits));
                True(candidates.Any(x => x.Format.ContainerBits == 32 && x.Format.ValidBits == 32 && x.Format.Encoding == PcmEncoding.Integer));
            }
            return Task.CompletedTask;
        });
        yield return ("Mono remains mono and 24-in-32 is distinct from 32 valid integer bits", () =>
        {
            var formats = ExclusiveAudioPolicy.Candidates(new(96000, 1, PcmEncoding.Integer, 24, 24));
            True(formats.All(x => x.Format.Channels == 1));
            True(formats.Any(x => x.Extensible && x.Format.ContainerBits == 32 && x.Format.ValidBits == 24 && x.Format.ChannelMask == 4));
            True(formats.Any(x => x.Format.ContainerBits == 32 && x.Format.ValidBits == 32));
            return Task.CompletedTask;
        });
        yield return ("PCM widening retains low bits and signed boundary samples without truncation", () =>
        {
            Equal("0001000000FFFFFF00FFFF7F00000080", Convert.ToHexString(
                ExclusiveAudioPolicy.Widen([1,0,0, 255,255,255, 255,255,127, 0,0,128],24,32)));
            Equal("000001000000FFFF", Convert.ToHexString(ExclusiveAudioPolicy.Widen([1,0,255,255],16,32)));
            Throws(() => ExclusiveAudioPolicy.Widen([0,0,0],24,16));
            Throws(() => ExclusiveAudioPolicy.Widen([0],16,32));
            Throws(() => ExclusiveAudioPolicy.Candidates(new(96000,2,PcmEncoding.Float,32,32)));
            return Task.CompletedTask;
        });
        yield return ("WinRT device paths and WASAPI endpoint IDs match without matching another endpoint", () =>
        {
            const string endpoint = "{0.0.0.00000000}.{20f57f8f-d61c-4c73-892a-fe2bcdf376e9}";
            True(ExclusiveAudioPolicy.SameEndpoint(endpoint, @"\\?\SWD#MMDEVAPI#" + endpoint.ToUpperInvariant() + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}"));
            True(!ExclusiveAudioPolicy.SameEndpoint(endpoint,"{0.0.0.00000000}.{2b055c2e-fc27-49ac-b510-a84ba907bdb8}"));
            True(!ExclusiveAudioPolicy.SameEndpoint(null,null));
            return Task.CompletedTask;
        });
        yield return ("Exclusive audio covers all seven audio extensions and excludes network/video", () =>
        {
            True(!AudioPlaybackOptions.Parse([]).Exclusive);
            var options = AudioPlaybackOptions.Parse(["--exclusive-audio","--audio-device","dac"]);
            Equal("dac", options.DeviceId);
            True(options.ProbesLocalAudio(@"C:\Music\test.M4A"));
            foreach (var ext in new[] { ".wav", ".flac", ".m4a", ".mp3", ".aac", ".ogg", ".opus" })
                True(options.ProbesLocalAudio(@"C:\Music\test" + ext.ToUpperInvariant()));
            foreach (var file in new[] { "https://server/test.flac", @"\\nas\music\test.wav", @"C:\Music\video.mp4" })
                True(!options.ProbesLocalAudio(file));
            Throws(() => AudioPlaybackOptions.Parse(["--audio-device","dac"]));
            Throws(() => AudioPlaybackOptions.Parse(["--exclusive-audio","--audio-device"]));
            return Task.CompletedTask;
        });
        yield return ("New preparation waits for the previous engine to release its output asynchronously", async () =>
        {
            using var session = new PlaybackEngineSession();
            var old = new ReleaseEngine(); var next = new ReleaseEngine();
            session.Replace(old); session.Replace(next);
            var prepare = session.PrepareAsync(new("next.wav"),CancellationToken.None);
            True(old.Disposed && !prepare.IsCompleted && !next.Prepared);
            old.Release.SetResult(); True(await prepare); True(next.Prepared);
            next.Release.SetResult();
        });
        yield return ("Canceled preparation waiting for old output release never prepares the new engine", async () =>
        {
            using var session = new PlaybackEngineSession();
            var old = new ReleaseEngine(); var next = new ReleaseEngine();
            session.Replace(old); session.Replace(next);
            using var cancellation = new CancellationTokenSource();
            var prepare = session.PrepareAsync(new("next.wav"),cancellation.Token);
            cancellation.Cancel();
            try { await prepare; throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
            True(!next.Prepared); old.Release.SetResult(); next.Release.SetResult();
        });
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected,T actual) { if (!Equals(expected,actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; } catch (NotSupportedException) { return; } throw new Exception("Expected rejection."); }
    private sealed class ReleaseEngine : IPlaybackEngine
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed, Prepared;
        public event EventHandler? Opened { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler<PlaybackEngineFailureEventArgs>? Failed { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler? SeekCompleted { add { } remove { } }
        public event EventHandler? OutputChanged { add { } remove { } }
        public PlaybackEngineState State => PlaybackEngineState.Paused;
        public TimeSpan Position => TimeSpan.Zero;
        public TimeSpan Duration => TimeSpan.Zero;
        public bool CanSeek => false;
        public bool HasVideo => false;
        public VideoStreamInfo? Video => null;
        public AudioStreamInfo? Audio => null;
        public PcmFormat? DecodedPcm => null;
        public PlaybackOutputInfo Output => new("fake",PlaybackOutputMode.Exclusive,"dac",null,false);
        public Task PrepareAsync(PlaybackSource source,CancellationToken token) { Prepared=true; return Task.CompletedTask; }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Seek(TimeSpan position) { }
        public void Dispose() => Disposed=true;
        public ValueTask DisposeAsync() { Dispose(); return new(Release.Task); }
    }
}
