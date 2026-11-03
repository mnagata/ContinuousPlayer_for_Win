using ContinuousPlayer.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace ContinuousPlayer.Playback;

/// <summary>The Windows standard engine and its video surface. No app session policy lives here.</summary>
internal sealed class MediaPlayerPlaybackEngine : IPlaybackEngine
{
    private readonly MediaPlayer player = new() { AutoPlay = false, IsLoopingEnabled = false };
    private readonly MediaPlayerElement surface;
    private MediaSource? source;
    private MediaPlaybackItem? item;
    private bool prepared;
    private bool disposed;

    public MediaPlayerPlaybackEngine(MediaPlayerElement surface)
    {
        this.surface = surface;
        player.CommandManager.IsEnabled = false;
        // The HWND-owned transport manager handles every engine. Suppress the player's session.
        player.SystemMediaTransportControls.IsEnabled = false;
        player.MediaOpened += MediaOpened;
        player.MediaEnded += MediaEnded;
        player.MediaFailed += MediaFailed;
        player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
        player.PlaybackSession.SeekCompleted += PlaybackSeekCompleted;
        surface.SetMediaPlayer(player);
    }

    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler<PlaybackEngineFailureEventArgs>? Failed;
    public event EventHandler? StateChanged;
    public event EventHandler? SeekCompleted;
    public event EventHandler? OutputChanged;

    public PlaybackEngineState State => player.PlaybackSession.PlaybackState switch
    {
        MediaPlaybackState.Opening => PlaybackEngineState.Opening,
        MediaPlaybackState.Buffering => PlaybackEngineState.Buffering,
        MediaPlaybackState.Playing => PlaybackEngineState.Playing,
        MediaPlaybackState.Paused => PlaybackEngineState.Paused,
        _ => PlaybackEngineState.None
    };
    public TimeSpan Position => player.PlaybackSession.Position;
    public TimeSpan Duration => player.PlaybackSession.NaturalDuration;
    public bool CanSeek => player.PlaybackSession.CanSeek;
    public bool HasVideo => player.PlaybackSession.NaturalVideoWidth > 0;
    public VideoStreamInfo? Video
    {
        get
        {
            if (item?.VideoTracks.Count is not > 0) return null;
            var video = item.VideoTracks[0].GetEncodingProperties();
            return new(video.Subtype, (int)video.Width, (int)video.Height,
                video.FrameRate.Denominator > 0 ? video.FrameRate.Numerator / (double)video.FrameRate.Denominator : null);
        }
    }
    public AudioStreamInfo? Audio
    {
        get
        {
            if (item?.AudioTracks.Count is not > 0) return null;
            var audio = item.AudioTracks[0].GetEncodingProperties();
            return new(audio.Subtype, (int)audio.SampleRate, (int)audio.ChannelCount,
                audio.BitsPerSample > 0 ? (int)audio.BitsPerSample : null);
        }
    }
    // MediaPlayer does not expose the decoded/output PCM. Do not substitute stream tags or MixFormat.
    public PcmFormat? DecodedPcm => null;
    public PlaybackOutputInfo Output => new("Windows MediaPlayer", PlaybackOutputMode.Standard,
        player.AudioDevice?.Id, player.AudioDevice?.Name, player.AudioDevice == null);

    public async Task PrepareAsync(PlaybackSource media, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (prepared) throw new InvalidOperationException("An engine instance owns only one source.");
        prepared = true;
        cancellationToken.ThrowIfCancellationRequested();
        MediaSource resolved;
        if (Uri.TryCreate(media.Location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            resolved = MediaSource.CreateFromUri(uri);
        else
            resolved = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(media.Location));
        // A file lookup can finish after a next/home command has disposed this engine.
        if (disposed || cancellationToken.IsCancellationRequested)
        {
            resolved.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }
        source = resolved;
        item = new MediaPlaybackItem(resolved);
        player.Source = item;
    }

    public void Play() => player.Play();
    public void Pause() => player.Pause();
    public void Stop() { player.Pause(); if (CanSeek) Seek(TimeSpan.Zero); }
    public void Seek(TimeSpan position) => player.PlaybackSession.Position = position;

    private void MediaOpened(MediaPlayer sender, object args)
    {
        if (disposed) return;
        OutputChanged?.Invoke(this, EventArgs.Empty);
        Opened?.Invoke(this, EventArgs.Empty);
    }
    private void MediaEnded(MediaPlayer sender, object args) { if (!disposed) Ended?.Invoke(this, EventArgs.Empty); }
    private void MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (!disposed) Failed?.Invoke(this, new($"{args.Error}\n{args.ErrorMessage}", args.ExtendedErrorCode?.HResult ?? 0));
    }
    private void PlaybackStateChanged(MediaPlaybackSession sender, object args) { if (!disposed) StateChanged?.Invoke(this, EventArgs.Empty); }
    private void PlaybackSeekCompleted(MediaPlaybackSession sender, object args) { if (!disposed) SeekCompleted?.Invoke(this, EventArgs.Empty); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        player.MediaOpened -= MediaOpened;
        player.MediaEnded -= MediaEnded;
        player.MediaFailed -= MediaFailed;
        player.PlaybackSession.PlaybackStateChanged -= PlaybackStateChanged;
        player.PlaybackSession.SeekCompleted -= PlaybackSeekCompleted;
        player.Pause();
        surface.SetMediaPlayer(null);
        player.Source = null;
        player.Dispose();
        source?.Dispose();
        source = null;
        item = null;
    }
}
