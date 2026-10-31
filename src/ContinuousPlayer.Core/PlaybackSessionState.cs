namespace ContinuousPlayer.Core;

[Flags]
public enum PlaybackInterruption
{
    None = 0,
    Hidden = 1,
    Minimized = 2,
    Locked = 4,
    Suspended = 8,
    AudioOutputUnavailable = 16,
    PlaybackInterrupted = 32,
    SessionUnavailable = 64
}

public enum PlaybackPhase { Inactive, Preparing, Ready, Ended, Failed }
public readonly record struct PlaybackStamp(long Session, long Media);
public readonly record struct PlaybackRestoreResult(bool Restored, TimeSpan Position);

/// <summary>Owns playback intent and the one-shot restoration of temporary UI.</summary>
public sealed class PlaybackSessionState
{
    public sealed class Suspension
    {
        internal Suspension(PlaybackStamp stamp, long interruptions, TimeSpan position, bool wantsPlayback)
            => (Stamp, Interruptions, Position, WantsPlayback) = (stamp, interruptions, position, wantsPlayback);
        internal PlaybackStamp Stamp { get; }
        internal long Interruptions { get; }
        public TimeSpan Position { get; }
        internal bool WantsPlayback { get; }
    }

    private long session;
    private long media;
    private long interruptionGeneration;
    private Suspension? suspension;
    public PlaybackPhase Phase { get; private set; }
    public PlaybackInterruption Interruptions { get; private set; }
    public bool WantsPlayback { get; private set; }
    public bool IsActive => Phase != PlaybackPhase.Inactive;
    public bool HasTemporaryPause => suspension != null;
    public PlaybackStamp Stamp => new(session, media);
    public bool CanPlay => IsActive && Phase == PlaybackPhase.Ready && WantsPlayback &&
        Interruptions == PlaybackInterruption.None && !HasTemporaryPause;

    public void Start(bool wantsPlayback = true)
    {
        session++;
        media++;
        suspension = null;
        Phase = PlaybackPhase.Preparing;
        WantsPlayback = wantsPlayback && Interruptions == PlaybackInterruption.None;
    }

    public void BeginMedia()
    {
        if (!IsActive) throw new InvalidOperationException("No playback session.");
        media++;
        suspension = null;
        Phase = PlaybackPhase.Preparing;
    }

    public bool IsCurrent(PlaybackStamp stamp) => IsActive && stamp == Stamp;
    public void Ready() { if (Phase == PlaybackPhase.Preparing) Phase = PlaybackPhase.Ready; }
    public void Fail() { if (IsActive) { Phase = PlaybackPhase.Failed; WantsPlayback = false; } }
    public void End() { if (IsActive) { Phase = PlaybackPhase.Ended; WantsPlayback = false; } }

    public void RequestPlayback(bool play)
    {
        if (IsActive && !HasTemporaryPause)
            WantsPlayback = play && Interruptions == PlaybackInterruption.None;
    }

    public Suspension Suspend(TimeSpan position)
    {
        if (!IsActive) throw new InvalidOperationException("No playback session.");
        // Navigation and child dialogs must not overwrite the original position/intent.
        return suspension ??= new(Stamp, interruptionGeneration,
            position < TimeSpan.Zero ? TimeSpan.Zero : position, WantsPlayback);
    }

    public PlaybackRestoreResult Restore(Suspension? saved)
    {
        if (saved == null || !ReferenceEquals(saved, suspension) || !IsCurrent(saved.Stamp)) return default;
        suspension = null;
        WantsPlayback = saved.WantsPlayback && saved.Interruptions == interruptionGeneration &&
            Interruptions == PlaybackInterruption.None && Phase == PlaybackPhase.Ready;
        return new(true, saved.Position);
    }

    public void SetInterruption(PlaybackInterruption reason, bool active)
    {
        var next = active ? Interruptions | reason : Interruptions & ~reason;
        if (next == Interruptions) return;
        Interruptions = next;
        if (active) { interruptionGeneration++; WantsPlayback = false; }
        // Removing a reason never changes playback intent back to true.
    }

    public void ObserveUnexpectedPause(TimeSpan position, TimeSpan duration)
    {
        if (!CanPlay) return;
        // Some decoders report Paused at the last presentation timestamp before
        // MediaEnded. That is completion, not an external playback interruption.
        var endTolerance = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMilliseconds(250).Ticks, duration.Ticks / 4));
        if (duration > TimeSpan.Zero && position >= duration - endTolerance) return;
        SetInterruption(PlaybackInterruption.PlaybackInterrupted, true);
        SetInterruption(PlaybackInterruption.PlaybackInterrupted, false);
    }

    public void Stop()
    {
        session++;
        media++;
        suspension = null;
        Phase = PlaybackPhase.Inactive;
        WantsPlayback = false;
        // OS conditions survive media/session changes (e.g. selection while minimized).
    }
}
