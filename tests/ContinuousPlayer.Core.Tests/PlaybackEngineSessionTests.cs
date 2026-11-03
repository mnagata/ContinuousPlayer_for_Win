using ContinuousPlayer.Core;

static class PlaybackEngineSessionTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("Engine starts only after ready and preserves pause intent across next media", async () =>
        {
            using var session = new PlaybackEngineSession();
            var state = new PlaybackSessionState(); state.Start();
            var first = new FakeEngine(); session.Replace(first);
            True(await session.PrepareAsync(new("first.wav"), CancellationToken.None));
            session.Apply(state); Equal(0, first.Plays);
            state.Ready(); session.Apply(state); Equal(1, first.Plays);
            state.RequestPlayback(false); session.Apply(state); Equal(PlaybackEngineState.Paused, first.State);
            state.BeginMedia(); var next = new FakeEngine(); session.Replace(next);
            True(first.Disposed); True(await session.PrepareAsync(new("next.flac"), CancellationToken.None));
            state.Ready(); session.Apply(state); Equal(0, next.Plays); Equal(PlaybackEngineState.Paused, next.State);
        });
        yield return ("Temporary UI restoration seeks the engine and keeps playing or paused intent", () =>
        {
            foreach (var play in new[] { true, false })
            {
                using var session = new PlaybackEngineSession();
                var engine = new FakeEngine { Position = TimeSpan.FromSeconds(30) }; session.Replace(engine);
                var state = new PlaybackSessionState(); state.Start(play); state.Ready(); session.Apply(state);
                var saved = state.Suspend(engine.Position); session.Apply(state);
                Equal(PlaybackEngineState.Paused, engine.State);
                engine.Position = TimeSpan.FromSeconds(80);
                var restored = state.Restore(saved); True(restored.Restored);
                session.RestorePosition(restored.Position); session.Apply(state);
                Equal(TimeSpan.FromSeconds(30), engine.Position);
                Equal(play ? PlaybackEngineState.Playing : PlaybackEngineState.Paused, engine.State);
            }
            return Task.CompletedTask;
        });
        yield return ("Interruption during temporary UI restoration requires manual engine resume", () =>
        {
            using var session = new PlaybackEngineSession(); var engine = new FakeEngine(); session.Replace(engine);
            var state = new PlaybackSessionState(); state.Start(); state.Ready(); session.Apply(state);
            var saved = state.Suspend(TimeSpan.FromSeconds(12)); session.Apply(state);
            state.SetInterruption(PlaybackInterruption.Locked, true); session.Apply(state);
            state.SetInterruption(PlaybackInterruption.Locked, false);
            var restored = state.Restore(saved); session.RestorePosition(restored.Position); session.Apply(state);
            Equal(1, engine.Plays); Equal(PlaybackEngineState.Paused, engine.State);
            state.RequestPlayback(true); session.Apply(state); Equal(2, engine.Plays);
            return Task.CompletedTask;
        });
        yield return ("Retry waits for engine seek confirmation before applying playback intent", async () =>
        {
            using var session = new PlaybackEngineSession();
            var engine = new FakeEngine { AutoCompleteSeek = false }; session.Replace(engine);
            var state = new PlaybackSessionState(); state.Start();
            var restore = session.RestorePositionAsync(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(5), CancellationToken.None);
            session.Apply(state); True(!restore.IsCompleted); Equal(0, engine.Plays);
            engine.CompleteSeek(); True(await restore);
            state.Ready(); session.Apply(state); Equal(1, engine.Plays);
        });
        yield return ("Replacing the engine cancels old seeks and cannot complete a new seek", async () =>
        {
            using var session = new PlaybackEngineSession();
            var old = new FakeEngine { AutoCompleteSeek = false }; session.Replace(old);
            var pending = session.RestorePositionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), CancellationToken.None);
            var next = new FakeEngine { AutoCompleteSeek = false }; session.Replace(next);
            await Cancelled(pending); True(old.Disposed);
            var current = session.RestorePositionAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), CancellationToken.None);
            old.CompleteSeek(); True(!current.IsCompleted);
            next.CompleteSeek(); True(await current); Equal(TimeSpan.FromSeconds(20), next.Position);
        });
        yield return ("Late preparation of a replaced engine cannot commit into the current engine", async () =>
        {
            using var session = new PlaybackEngineSession();
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var old = new FakeEngine { Prepare = _ => pending.Task }; session.Replace(old);
            var prepare = session.PrepareAsync(new("slow.wav"), CancellationToken.None);
            var next = new FakeEngine(); session.Replace(next);
            True(await session.PrepareAsync(new("new.wav"), CancellationToken.None));
            pending.SetResult(); True(!await prepare); True(old.Disposed);
            Equal("new.wav", next.Location); Equal(0, next.Plays);
        });
        yield return ("Unseekable, timed out and inaccurate retry restoration do not report success", async () =>
        {
            using var session = new PlaybackEngineSession(); var engine = new FakeEngine(); session.Replace(engine);
            engine.CanSeek = false;
            True(!await session.RestorePositionAsync(TimeSpan.FromSeconds(10), TimeSpan.Zero, CancellationToken.None));
            Equal(0, engine.Seeks);
            engine.CanSeek = true; engine.AutoCompleteSeek = false;
            True(!await session.RestorePositionAsync(TimeSpan.FromSeconds(10), TimeSpan.Zero, CancellationToken.None));
            var inaccurate = session.RestorePositionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), CancellationToken.None);
            engine.Position = TimeSpan.Zero; engine.CompleteSeek(); True(!await inaccurate);
        });
        yield return ("Retry cancellation and home release the engine without autoplay", async () =>
        {
            using var session = new PlaybackEngineSession();
            var engine = new FakeEngine { AutoCompleteSeek = false }; session.Replace(engine);
            using var cancellation = new CancellationTokenSource();
            var restore = session.RestorePositionAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), cancellation.Token);
            cancellation.Cancel(); await Cancelled(restore);
            session.Replace(null); session.Replace(null); True(engine.Disposed);
            Equal<IPlaybackEngine?>(null, session.Engine); Equal(0, engine.Plays); Equal(1, engine.DisposeCount);
        });
        yield return ("Position restoration clamps to duration and preserves unknown-duration seek", () =>
        {
            using var session = new PlaybackEngineSession(); var engine = new FakeEngine(); session.Replace(engine);
            session.RestorePosition(TimeSpan.FromSeconds(500)); Equal(engine.Duration, engine.Position);
            session.RestorePosition(TimeSpan.FromSeconds(-10)); Equal(TimeSpan.Zero, engine.Position);
            engine.Duration = TimeSpan.Zero; session.RestorePosition(TimeSpan.FromSeconds(500));
            Equal(TimeSpan.FromSeconds(500), engine.Position);
            return Task.CompletedTask;
        });
    }

    private static async Task Cancelled(Task task)
    {
        try { await task; } catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancelled operation.");
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}.");
    }

    private sealed class FakeEngine : IPlaybackEngine
    {
        public event EventHandler? Opened { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler<PlaybackEngineFailureEventArgs>? Failed { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler? OutputChanged { add { } remove { } }
        public event EventHandler? SeekCompleted;
        public PlaybackEngineState State { get; private set; } = PlaybackEngineState.Paused;
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(100);
        public bool CanSeek { get; set; } = true;
        public bool HasVideo => false;
        public VideoStreamInfo? Video => null;
        public AudioStreamInfo? Audio => null;
        public PcmFormat? DecodedPcm => null;
        public PlaybackOutputInfo Output => new("Fake", PlaybackOutputMode.Standard, null, null, true);
        public bool AutoCompleteSeek { get; set; } = true;
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public int Plays { get; private set; }
        public int Seeks { get; private set; }
        public string? Location { get; private set; }
        public Func<CancellationToken, Task>? Prepare { get; init; }
        public Task PrepareAsync(PlaybackSource source, CancellationToken cancellationToken)
        {
            Location = source.Location;
            return Prepare?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public void Play() { True(!Disposed); Plays++; State = PlaybackEngineState.Playing; }
        public void Pause() { True(!Disposed); State = PlaybackEngineState.Paused; }
        public void Stop() { Pause(); Position = TimeSpan.Zero; }
        public void Seek(TimeSpan position) { True(!Disposed); Seeks++; Position = position; if (AutoCompleteSeek) CompleteSeek(); }
        public void CompleteSeek() => SeekCompleted?.Invoke(this, EventArgs.Empty);
        public void Dispose() { Disposed = true; DisposeCount++; }
    }
}
