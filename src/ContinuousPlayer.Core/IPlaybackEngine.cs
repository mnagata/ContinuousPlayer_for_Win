namespace ContinuousPlayer.Core;

public enum PlaybackEngineState { None, Opening, Buffering, Playing, Paused }
public enum PlaybackOutputMode { Standard, Exclusive }
public enum PcmEncoding { Integer, Float }

public sealed record PlaybackSource(string Location, string? MimeType = null);
public sealed record AudioStreamInfo(string Codec, int SampleRate, int Channels, int? BitsPerSample);
public sealed record VideoStreamInfo(string Codec, int Width, int Height, double? FramesPerSecond);
public sealed record PcmFormat(int SampleRate, int Channels, PcmEncoding Encoding, int ContainerBits, int ValidBits,
    int ChannelMask = 0);
public sealed record PlaybackOutputInfo(string EngineName, PlaybackOutputMode Mode, string? DeviceId,
    string? DeviceName, bool UsesDefaultDevice, PcmFormat? Format = null);

public sealed class PlaybackEngineFailureEventArgs(string message, int code) : EventArgs
{
    public string Message { get; } = message;
    public int Code { get; } = code;
}

/// <summary>
/// One engine instance owns one media source. Opened signals readiness; PrepareAsync
/// only resolves and assigns the source. Events may arrive from a worker thread.
/// Dispose releases the source/output and invalidates pending preparation.
/// </summary>
public interface IPlaybackEngine : IDisposable, IAsyncDisposable
{
    event EventHandler? Opened;
    event EventHandler? Ended;
    event EventHandler<PlaybackEngineFailureEventArgs>? Failed;
    event EventHandler? StateChanged;
    event EventHandler? SeekCompleted;
    event EventHandler? OutputChanged;

    PlaybackEngineState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool CanSeek { get; }
    bool HasVideo { get; }
    VideoStreamInfo? Video { get; }
    AudioStreamInfo? Audio { get; }
    PcmFormat? DecodedPcm { get; }
    PlaybackOutputInfo Output { get; }

    Task PrepareAsync(PlaybackSource source, CancellationToken cancellationToken);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);

    ValueTask IAsyncDisposable.DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
