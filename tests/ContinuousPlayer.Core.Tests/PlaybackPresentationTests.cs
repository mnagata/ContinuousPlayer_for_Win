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
        yield return ("Video chrome hides only when playback and every interaction guard permit it", () =>
        {
            if (!PlaybackPresentation.CanAutoHide(true, false, false, false, false, false)) throw new Exception("Idle video");
            for (var mask = 0; mask < 64; mask++)
            {
                var actual = PlaybackPresentation.CanAutoHide((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0,
                    (mask & 8) != 0, (mask & 16) != 0, (mask & 32) != 0);
                if (actual != (mask == 1)) throw new Exception($"Guard combination {mask}");
            }
        });
    }
}
