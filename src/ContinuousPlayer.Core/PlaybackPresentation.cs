namespace ContinuousPlayer.Core;

public static class PlaybackPresentation
{
    public static string Title(string name) => MediaDisplayNames.Title(name);

    // Buffering is deliberately absent: it does not change the user's playback intent.
    public static bool CanAutoHide(bool video, bool paused, bool foregroundUi, bool controlsKeyboardFocused,
        bool accessibilityActive, bool preparing = false) =>
        // A replacement player has no video dimensions until MediaOpened.
        // Keep chrome hidden during that interval; confirmed audio still keeps its controls.
        (video || preparing) && !paused && !foregroundUi && !controlsKeyboardFocused && !accessibilityActive;
}
