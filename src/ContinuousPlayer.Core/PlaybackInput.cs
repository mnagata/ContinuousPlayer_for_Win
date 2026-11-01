namespace ContinuousPlayer.Core;

public enum PlaybackCommand { None, Toggle, Play, Pause, Previous, Next, SeekBackward, SeekForward, Files, Information, Home }
public enum PlaybackInputKey { Other, Left, Right, Up, Down, Space, Enter, Escape, I, O, H }
public enum PlaybackInputTarget { Surface, Control, TextEntry, Overlay, Inactive }
public enum PlaybackPointerKind { Mouse, Touch, Pen }

public static class PlaybackInput
{
    public static PlaybackCommand Resolve(PlaybackInputKey key, PlaybackInputTarget target, bool shift = false)
    {
        if (target is PlaybackInputTarget.TextEntry or PlaybackInputTarget.Overlay or PlaybackInputTarget.Inactive)
            return PlaybackCommand.None;
        if (key is PlaybackInputKey.Escape or PlaybackInputKey.O) return PlaybackCommand.Files;
        if (key == PlaybackInputKey.I) return PlaybackCommand.Information;
        if (key == PlaybackInputKey.H) return PlaybackCommand.Home;
        if (target != PlaybackInputTarget.Surface) return PlaybackCommand.None;
        return key switch
        {
            PlaybackInputKey.Left => shift ? PlaybackCommand.SeekBackward : PlaybackCommand.Previous,
            PlaybackInputKey.Right => shift ? PlaybackCommand.SeekForward : PlaybackCommand.Next,
            PlaybackInputKey.Up => PlaybackCommand.SeekBackward,
            PlaybackInputKey.Down => PlaybackCommand.SeekForward,
            PlaybackInputKey.Space or PlaybackInputKey.Enter => PlaybackCommand.Toggle,
            _ => PlaybackCommand.None
        };
    }

    public static int PageSelection(int selected, int count, int visibleCount, int direction)
        => count <= 0 ? -1 : selected < 0 ? 0 :
            (int)Math.Clamp((long)selected + Math.Sign(direction) * Math.Max(1, visibleCount), 0, count - 1L);
}

/// <summary>Deduplicates transports while a physical media key is held; release permits a new press.</summary>
public sealed class MediaKeyPressLatch
{
    private readonly object sync = new();
    private readonly Dictionary<int, bool> pressed = [];
    public void KeyDown(int key) { lock (sync) pressed.TryAdd(key, false); }
    public void KeyUp(int key) { lock (sync) pressed.Remove(key); }
    public bool TryAccept(int key, int alternative = 0)
    {
        lock (sync)
        {
            if (!pressed.TryGetValue(key, out var consumed))
            {
                key = alternative;
                if (!pressed.TryGetValue(key, out consumed)) return true; // OS UI / remote command.
            }
            if (consumed) return false;
            pressed[key] = true;
            return true;
        }
    }
}

/// <summary>Recognizes direct-touch gestures separately from pointer clicks, in DIPs.</summary>
public sealed class PlaybackGestureRecognizer
{
    private readonly HashSet<uint> contacts = [];
    private uint primary;
    private double startX, startY, width;
    private PlaybackPointerKind kind;
    private bool invalid;
    public bool HasActiveContact => contacts.Count != 0;
    private (PlaybackPointerKind Kind, int Zone, double X, double Y, long Time)? lastTap;
    public int TouchDoubleTapMilliseconds { get; set; } = 300;
    public int MouseDoubleClickMilliseconds { get; set; } = 500;
    public double TouchTapDistance { get; set; } = 24;
    public double MouseTapDistance { get; set; } = 4;

    public void Press(uint id, PlaybackPointerKind device, double x, double y, double surfaceWidth)
    {
        if (!contacts.Add(id)) return;
        if (contacts.Count > 1) { Invalidate(); return; }
        (primary, kind, startX, startY, width, invalid) = (id, device, x, y, surfaceWidth, false);
    }

    public void Invalidate() { invalid = true; lastTap = null; }
    public void Cancel(uint id) { if (contacts.Remove(id)) Invalidate(); }
    public void Reset() { contacts.Clear(); Invalidate(); }

    public PlaybackCommand Release(uint id, double x, double y, long milliseconds)
    {
        if (!contacts.Remove(id) || id != primary || invalid || contacts.Count != 0 || width <= 0)
            return PlaybackCommand.None;
        var dx = x - startX; var dy = y - startY;
        if (kind == PlaybackPointerKind.Touch && Math.Abs(dx) >= 70 && Math.Abs(dx) >= Math.Abs(dy) * 1.5)
        {
            lastTap = null;
            return dx < 0 ? PlaybackCommand.Next : PlaybackCommand.Previous;
        }
        var slop = kind == PlaybackPointerKind.Touch ? TouchTapDistance : MouseTapDistance;
        if (Math.Abs(dx) >= slop || Math.Abs(dy) >= slop) { lastTap = null; return PlaybackCommand.None; }
        var zone = kind == PlaybackPointerKind.Touch ? x < width / 3 ? 0 : x > width * 2 / 3 ? 2 : 1 : 1;
        var window = kind == PlaybackPointerKind.Touch ? TouchDoubleTapMilliseconds : MouseDoubleClickMilliseconds;
        if (lastTap is { } previous && previous.Kind == kind && previous.Zone == zone &&
            milliseconds >= previous.Time && milliseconds - previous.Time <= window &&
            Math.Abs(x - previous.X) <= slop && Math.Abs(y - previous.Y) <= slop)
        {
            lastTap = null; // Consume the pair; a deliberate third tap starts a new pair.
            return PlaybackCommand.None;
        }
        lastTap = (kind, zone, x, y, milliseconds);
        return zone switch { 0 => PlaybackCommand.SeekBackward, 2 => PlaybackCommand.SeekForward, _ => PlaybackCommand.Toggle };
    }
}
