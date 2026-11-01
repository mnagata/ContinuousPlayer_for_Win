using ContinuousPlayer.Core;

static class PlaybackPresentationTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("Playback titles remove only supported final extensions and preserve internal periods", () =>
        {
            foreach (var (source, expected) in new[] { ("作品.ver.2.MP4", "作品.ver.2"), ("作品", "作品"),
                ("作品.final", "作品.final"), ("作品.wav", "作品") })
                if (PlaybackPresentation.Title(source) != expected) throw new Exception(source);
        });
        yield return ("Video chrome auto-hide preserves pause, foreground UI, keyboard focus and screen-reader access", () =>
        {
            if (!PlaybackPresentation.CanAutoHide(true, false, false, false, false)) throw new Exception("Idle video");
            for (var mask = 0; mask < 32; mask++)
            {
                var actual = PlaybackPresentation.CanAutoHide((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
                    (mask & 8) != 0, (mask & 16) != 0);
                if (actual != (mask == 1)) throw new Exception($"Guard combination {mask}");
            }
        });
        yield return ("Replacing a playing video keeps chrome hidden before the new dimensions are known", () =>
        {
            var state = new PlaybackSessionState();
            state.Start(); state.Ready();
            if (!PlaybackPresentation.CanAutoHide(true, false, false, false, false)) throw new Exception("Playing video");
            state.BeginMedia();
            if (!PlaybackPresentation.CanAutoHide(false, !state.WantsPlayback, false, false, false,
                preparing: state.Phase == PlaybackPhase.Preparing)) throw new Exception("Source replacement revealed chrome");
            state.Ready();
            if (!PlaybackPresentation.CanAutoHide(true, false, false, false, false)) throw new Exception("Opened video");
            if (PlaybackPresentation.CanAutoHide(false, false, false, false, false)) throw new Exception("Confirmed audio lost controls");
        });
        yield return ("Preparing media still shows controls for pause, foreground UI and accessibility", () =>
        {
            for (var mask = 1; mask < 16; mask++)
                if (PlaybackPresentation.CanAutoHide(false, (mask & 1) != 0, (mask & 2) != 0,
                    (mask & 4) != 0, (mask & 8) != 0, preparing: true))
                    throw new Exception($"Preparation bypassed interaction guard {mask}");
        });
    }
}
