using ContinuousPlayer.Core;

static class PlaybackInputTests
{
    public static IEnumerable<(string Name, Action Test)> Cases()
    {
        yield return ("Focused UI and text never turn navigation or typing into playback commands", () =>
        {
            foreach (var target in new[] { PlaybackInputTarget.TextEntry, PlaybackInputTarget.Overlay, PlaybackInputTarget.Inactive })
                foreach (var key in Enum.GetValues<PlaybackInputKey>())
                    Equal(PlaybackCommand.None, PlaybackInput.Resolve(key, target));
            foreach (var key in new[] { PlaybackInputKey.Left, PlaybackInputKey.Right, PlaybackInputKey.Up, PlaybackInputKey.Down, PlaybackInputKey.Enter, PlaybackInputKey.Space })
                Equal(PlaybackCommand.None, PlaybackInput.Resolve(key, PlaybackInputTarget.Control));
        });
        yield return ("Surface arrows and shifted horizontal arrows match common playback semantics", () =>
        {
            Equal(PlaybackCommand.Previous, PlaybackInput.Resolve(PlaybackInputKey.Left, PlaybackInputTarget.Surface));
            Equal(PlaybackCommand.Next, PlaybackInput.Resolve(PlaybackInputKey.Right, PlaybackInputTarget.Surface));
            Equal(PlaybackCommand.SeekBackward, PlaybackInput.Resolve(PlaybackInputKey.Left, PlaybackInputTarget.Surface, true));
            Equal(PlaybackCommand.SeekForward, PlaybackInput.Resolve(PlaybackInputKey.Right, PlaybackInputTarget.Surface, true));
            Equal(PlaybackCommand.SeekBackward, PlaybackInput.Resolve(PlaybackInputKey.Up, PlaybackInputTarget.Surface));
            Equal(PlaybackCommand.SeekForward, PlaybackInput.Resolve(PlaybackInputKey.Down, PlaybackInputTarget.Surface));
        });
        yield return ("Mouse clicks toggle at every horizontal position and mouse drags never change files", () =>
        {
            foreach (var x in new[] { 10d, 150d, 290d })
            {
                var g = new PlaybackGestureRecognizer(); g.Press(1, PlaybackPointerKind.Mouse, x, 10, 300);
                Equal(PlaybackCommand.Toggle, g.Release(1, x, 10, 0));
                g.Press(1, PlaybackPointerKind.Mouse, x, 10, 300);
                Equal(PlaybackCommand.None, g.Release(1, x + 100, 10, 1000));
            }
        });
        yield return ("Direct touch has three tap regions while a double tap performs one immediate action", () =>
        {
            foreach (var (x, expected) in new[] { (10d, PlaybackCommand.SeekBackward), (150d, PlaybackCommand.Toggle), (290d, PlaybackCommand.SeekForward) })
            {
                var g = new PlaybackGestureRecognizer(); g.Press(1, PlaybackPointerKind.Touch, x, 10, 300);
                Equal(expected, g.Release(1, x, 10, 0));
                g.Press(1, PlaybackPointerKind.Touch, x, 10, 300);
                Equal(PlaybackCommand.None, g.Release(1, x, 10, 299));
                g.Press(1, PlaybackPointerKind.Touch, x, 10, 300);
                Equal(expected, g.Release(1, x, 10, 450));
            }
        });
        yield return ("Mouse double clicks follow OS timing and a separate region or device is not deduplicated", () =>
        {
            var g = new PlaybackGestureRecognizer { MouseDoubleClickMilliseconds = 700 };
            g.Press(1, PlaybackPointerKind.Mouse, 150, 10, 300); Equal(PlaybackCommand.Toggle, g.Release(1,150,10,0));
            g.Press(1, PlaybackPointerKind.Mouse, 150, 10, 300); Equal(PlaybackCommand.None, g.Release(1,150,10,600));
            g.Press(1, PlaybackPointerKind.Touch, 10, 10, 300); Equal(PlaybackCommand.SeekBackward, g.Release(1,10,10,700));
            g.Press(1, PlaybackPointerKind.Touch, 290, 10, 300); Equal(PlaybackCommand.SeekForward, g.Release(1,290,10,800));
        });
        yield return ("Slow swipes use the 70 DIP and 1.5 axis thresholds and cannot also tap", () =>
        {
            var g = new PlaybackGestureRecognizer();
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); Equal(PlaybackCommand.Next,g.Release(1,80,10,10000));
            Equal(PlaybackCommand.None,g.Release(1,80,10,10001));
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); Equal(PlaybackCommand.Previous,g.Release(1,220,10,20000));
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); Equal(PlaybackCommand.None,g.Release(1,81,10,30000));
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); Equal(PlaybackCommand.None,g.Release(1,80,60,40000));
        });
        yield return ("Multiple fingers, cancellation and invalidation cannot turn into a late tap", () =>
        {
            var g = new PlaybackGestureRecognizer();
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); g.Press(2,PlaybackPointerKind.Touch,160,10,300);
            Equal(PlaybackCommand.None,g.Release(2,160,10,0)); Equal(PlaybackCommand.None,g.Release(1,150,10,1));
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); g.Cancel(1);
            Equal(PlaybackCommand.None,g.Release(1,150,10,100));
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); g.Invalidate(); Equal(PlaybackCommand.None,g.Release(1,150,10,200));
            g.Press(1,PlaybackPointerKind.Touch,150,10,300); Equal(PlaybackCommand.Toggle,g.Release(1,150,10,300));
        });
        yield return ("Page selection depends on visible count and clamps without wrapping", () =>
        {
            Equal(12,PlaybackInput.PageSelection(4,40,8,1)); Equal(4,PlaybackInput.PageSelection(12,40,8,-1));
            Equal(39,PlaybackInput.PageSelection(38,40,8,1)); Equal(0,PlaybackInput.PageSelection(1,40,8,-1));
            Equal(0,PlaybackInput.PageSelection(-1,40,8,1)); Equal(-1,PlaybackInput.PageSelection(0,0,8,1));
            Equal(5,PlaybackInput.PageSelection(4,40,0,1));
        });
        yield return ("A held media key executes once and release permits the next deliberate press", () =>
        {
            var latch = new MediaKeyPressLatch(); latch.KeyDown(0xB0); Require(latch.TryAccept(0xB0));
            latch.KeyDown(0xB0); Require(!latch.TryAccept(0xB0)); Require(!latch.TryAccept(0xB0));
            latch.KeyUp(0xB0); latch.KeyDown(0xB0); Require(latch.TryAccept(0xB0));
            latch.KeyUp(0xB0); Require(latch.TryAccept(0xB0)); Require(latch.TryAccept(0xB0));
            latch.KeyDown(0xFA); Require(latch.TryAccept(0xB3,0xFA)); Require(!latch.TryAccept(0xB3,0xFA));
        });
    }
    private static void Require(bool value) { if(!value) throw new InvalidOperationException("Input invariant failed."); }
    private static void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, actual {actual}"); }
}
