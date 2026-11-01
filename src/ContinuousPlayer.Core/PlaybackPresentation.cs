namespace ContinuousPlayer.Core;

public static class PlaybackPresentation
{
    public static string Title(string name) => MediaDisplayNames.Title(name);

    // Buffering is deliberately absent: it does not change the user's playback intent.
    public static bool CanAutoHide(bool video, bool paused, bool foregroundUi, bool controlsFocused,
        bool pointerOverButton, bool accessibilityActive) =>
        video && !paused && !foregroundUi && !controlsFocused && !pointerOverButton && !accessibilityActive;
}
