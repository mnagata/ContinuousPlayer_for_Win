namespace ContinuousPlayer.Core;

public sealed class PlaybackQueue
{
    public IReadOnlyList<MediaFile> Items { get; private set; } = [];
    public int Index { get; private set; } = -1;
    public MediaFile? Current => Index >= 0 && Index < Items.Count ? Items[Index] : null;
    public void Start(IReadOnlyList<MediaFile> items, int index)
    {
        if (index < 0 || index >= items.Count) throw new ArgumentOutOfRangeException(nameof(index));
        Items = items;
        Index = index;
    }
    public bool Move(int delta)
    {
        var next = Index + delta;
        if (next < 0 || next >= Items.Count) return false;
        Index = next;
        return true;
    }
    public void RefreshCurrent(MediaFile refreshed)
    {
        if (Current?.Id != refreshed.Id) throw new InvalidOperationException("The refreshed media must have the current file ID.");
        var items = Items.ToArray(); items[Index] = refreshed; Items = items;
    }
    public (TimeSpan Position, bool Next) Seek(TimeSpan current, TimeSpan duration, int seconds)
    {
        var target = current + TimeSpan.FromSeconds(seconds);
        if (seconds > 0 && duration > TimeSpan.Zero && target >= duration)
            return (current, Index + 1 < Items.Count);
        return (target < TimeSpan.Zero ? TimeSpan.Zero : target, false);
    }
}
