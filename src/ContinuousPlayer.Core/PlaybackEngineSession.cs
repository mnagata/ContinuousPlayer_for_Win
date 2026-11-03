namespace ContinuousPlayer.Core;

/// <summary>Owns the current engine and cancels work that belongs to a replaced source.</summary>
public sealed class PlaybackEngineSession : IDisposable
{
    private CancellationTokenSource? lifetime;
    private Task released = Task.CompletedTask;
    public IPlaybackEngine? Engine { get; private set; }

    public void Replace(IPlaybackEngine? next)
    {
        if (ReferenceEquals(next, Engine)) return;
        var previous = Engine;
        Engine = null;
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        if (previous != null) released = Task.WhenAll(released, previous.DisposeAsync().AsTask());
        Engine = next;
        if (next != null) lifetime = new();
    }

    public async Task<bool> PrepareAsync(PlaybackSource source, CancellationToken cancellationToken)
    {
        var engine = Engine ?? throw new InvalidOperationException("No playback engine.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime!.Token);
        await released.WaitAsync(linked.Token);
        if (!ReferenceEquals(Engine, engine)) return false;
        await engine.PrepareAsync(source, linked.Token);
        return !linked.IsCancellationRequested && ReferenceEquals(Engine, engine);
    }

    public void Apply(PlaybackSessionState state)
    {
        if (!state.IsActive || Engine is not { } engine) return;
        if (state.CanPlay) engine.Play(); else engine.Pause();
    }

    public void RestorePosition(TimeSpan position)
    {
        if (Engine is not { CanSeek: true } engine) return;
        engine.Seek(Clamp(position, engine.Duration));
    }

    public async Task<bool> RestorePositionAsync(TimeSpan position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Engine is not { CanSeek: true } engine) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime!.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, EventArgs args)
        {
            if (ReferenceEquals(sender, engine) && ReferenceEquals(Engine, engine)) completion.TrySetResult();
        }
        engine.SeekCompleted += Completed;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var target = Clamp(position, engine.Duration);
            engine.Seek(target);
            await completion.Task.WaitAsync(timeout, linked.Token);
            return ReferenceEquals(Engine, engine) && Math.Abs((engine.Position - target).TotalSeconds) <= 1;
        }
        catch (TimeoutException) { return false; }
        finally { engine.SeekCompleted -= Completed; }
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration) => position < TimeSpan.Zero ? TimeSpan.Zero
        : duration > TimeSpan.Zero && position > duration ? duration : position;

    public void Dispose() => Replace(null);
}
