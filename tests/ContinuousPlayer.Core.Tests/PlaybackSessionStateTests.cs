using ContinuousPlayer.Core;

static class PlaybackSessionStateTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("Natural-end pause preserves intent, but an unexpected mid-file pause requires manual resume", () =>
        {
            var state = Playing();
            state.ObserveUnexpectedPause(TimeSpan.FromMilliseconds(44990), TimeSpan.FromSeconds(45));
            Require(state.CanPlay);
            state.ObserveUnexpectedPause(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45));
            Require(!state.CanPlay && !state.WantsPlayback);
        });
        yield return ("Explicit replay after final completion starts a new media generation", () =>
        {
            var state = Playing(); var ended = state.Stamp; state.End();
            state.RequestPlayback(true); state.BeginMedia(); state.Ready();
            Require(state.CanPlay && !state.IsCurrent(ended));
        });
        yield return ("Playing browser cancellation restores 30 seconds and playback intent", () =>
        {
            var state = Playing();
            var saved = state.Suspend(TimeSpan.FromSeconds(30));
            Require(!state.CanPlay && state.WantsPlayback);
            var result = state.Restore(saved);
            Require(result.Restored && result.Position == TimeSpan.FromSeconds(30) && state.CanPlay);
        });
        yield return ("Paused information cancellation remains paused and restoration is one-shot", () =>
        {
            var state = Playing(); state.RequestPlayback(false);
            var saved = state.Suspend(TimeSpan.FromSeconds(30));
            Require(state.Restore(saved).Restored && !state.CanPlay);
            state.RequestPlayback(true);
            Require(!state.Restore(saved).Restored && state.CanPlay);
        });
        yield return ("Browsing children and registration dialogs cannot overwrite the original snapshot", () =>
        {
            var state = Playing(); var original = state.Suspend(TimeSpan.FromSeconds(30));
            state.RequestPlayback(false);
            Require(ReferenceEquals(original, state.Suspend(TimeSpan.FromSeconds(99))));
            Require(state.Restore(original).Position == TimeSpan.FromSeconds(30) && state.CanPlay);
        });
        yield return ("Every interruption prevents cancellation from automatically restarting", () =>
        {
            foreach (var reason in Enum.GetValues<PlaybackInterruption>().Where(x => x != PlaybackInterruption.None))
            {
                var state = Playing(); var saved = state.Suspend(TimeSpan.FromSeconds(30));
                state.SetInterruption(reason, true); state.SetInterruption(reason, false);
                Require(state.Restore(saved).Restored && !state.CanPlay && !state.WantsPlayback);
                state.RequestPlayback(true); Require(state.CanPlay);
            }
        });
        yield return ("Overlapping OS interruptions must all clear before explicit playback", () =>
        {
            var state = Playing();
            state.SetInterruption(PlaybackInterruption.Locked, true);
            state.SetInterruption(PlaybackInterruption.Suspended, true);
            state.SetInterruption(PlaybackInterruption.Locked, false);
            state.RequestPlayback(true); Require(!state.CanPlay);
            state.SetInterruption(PlaybackInterruption.Suspended, false); Require(!state.CanPlay);
            state.RequestPlayback(true); Require(state.CanPlay);
        });
        yield return ("Selecting same or different media starts fresh and rejects the old cancellation", () =>
        {
            var state = Playing(); state.RequestPlayback(false);
            var oldStamp = state.Stamp; var saved = state.Suspend(TimeSpan.FromSeconds(30));
            state.Start(); state.Ready();
            Require(!state.IsCurrent(oldStamp) && !state.Restore(saved).Restored && state.CanPlay);
        });
        yield return ("Home invalidates queued media completion and all restoration tokens", () =>
        {
            var state = Playing(); var stamp = state.Stamp; var saved = state.Suspend(TimeSpan.FromSeconds(30));
            state.Stop(); state.Ready();
            Require(!state.IsCurrent(stamp) && !state.Restore(saved).Restored && !state.IsActive);
        });
        yield return ("Changing a file rejects old completions and preserves paused intent", () =>
        {
            var state = Playing(); state.RequestPlayback(false); var stamp = state.Stamp;
            state.BeginMedia(); Require(!state.IsCurrent(stamp)); state.Ready(); Require(!state.CanPlay);
        });
        yield return ("Failure or natural final ending during a temporary UI cannot resume on close", () =>
        {
            foreach (var fail in new[] { true, false })
            {
                var state = Playing(); var saved = state.Suspend(TimeSpan.FromSeconds(30));
                if (fail) state.Fail(); else state.End();
                state.Ready(); // A delayed readiness signal cannot revive a terminal state.
                Require(state.Restore(saved).Restored && !state.CanPlay && !state.WantsPlayback);
                Require(state.Phase == (fail ? PlaybackPhase.Failed : PlaybackPhase.Ended));
            }
        });
        yield return ("Closing temporary UI before preparation completes does not schedule a late auto-resume", () =>
        {
            var state = new PlaybackSessionState(); state.Start(); var saved = state.Suspend(TimeSpan.Zero);
            state.Restore(saved); state.Ready(); Require(!state.CanPlay);
        });
        yield return ("A new selection while locked cannot discard OS interruptions", () =>
        {
            var state = Playing(); state.SetInterruption(PlaybackInterruption.Locked, true);
            state.Stop(); state.Start(); state.Ready(); Require(!state.CanPlay);
            state.SetInterruption(PlaybackInterruption.Locked, false); Require(!state.CanPlay);
        });
        yield return ("Repeated notifications and late clearing do not revive consumed playback intent", () =>
        {
            var state = Playing(); var saved = state.Suspend(TimeSpan.FromSeconds(30));
            state.SetInterruption(PlaybackInterruption.Minimized, true);
            state.SetInterruption(PlaybackInterruption.Minimized, true);
            Require(state.Restore(saved).Restored && !state.CanPlay);
            state.SetInterruption(PlaybackInterruption.Minimized, false);
            Require(!state.CanPlay && !state.Restore(saved).Restored);
        });
    }

    private static PlaybackSessionState Playing()
    {
        var state = new PlaybackSessionState(); state.Start(); state.Ready(); return state;
    }
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Playback restoration invariant failed.");
    }
}
