using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ContinuousPlayer.Core;

namespace ContinuousPlayer.Audio.Windows;

public sealed record AudioPlaybackTrace(string Event, DateTimeOffset Time, string File,
    AudioStreamInfo? Input, PcmFormat? Decoded, PlaybackOutputInfo Output,
    TimeSpan Position, long ContentFrames = 0, long PlayedFrames = 0, string? PcmSha256 = null, string? Detail = null);

/// <summary>One local or HTTP audio resource. Dedicated workers own WASAPI and MF.</summary>
public sealed class WasapiPlaybackEngine : IPlaybackEngine
{
    private enum CommandKind { Play, Pause, Seek, Stop }
    private sealed record Command(CommandKind Kind, TimeSpan Position = default);
    private readonly ConcurrentQueue<Command> commands = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> producers = [];
    private readonly object lifecycle = new();
    private readonly string? deviceId;
    private readonly Action<AudioPlaybackTrace>? trace;
    private LocalAudioInfo? info;
    private PlaybackOutputInfo output;
    private int state;
    private int disposed;
    private int hasPrepared;
    private long positionTicks;
    private volatile bool wantsPlayback;
    private string path = "";
    private string? mimeType;
    private CancellationTokenRegistration preparationCancellation;
    // The following fields are touched only by the renderer worker.
    private ExclusiveEndpoint? endpoint;
    private DecodedPcmQueue? pipeline;
    private IncrementalHash? pcmHash;
    private TimeSpan anchor;
    private bool draining;
    private TimeSpan DecodePreparationTimeout => Uri.TryCreate(path, UriKind.Absolute, out var source) && DlnaAddress.IsHttpTarget(source)
        ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(5);

    public WasapiPlaybackEngine(string? deviceId = null, Action<AudioPlaybackTrace>? trace = null)
    {
        this.deviceId = deviceId;
        this.trace = trace;
        output = new("WASAPI / Media Foundation", PlaybackOutputMode.Exclusive, deviceId, null, deviceId == null);
    }

    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler<PlaybackEngineFailureEventArgs>? Failed;
    public event EventHandler? StateChanged;
    public event EventHandler? SeekCompleted;
    public event EventHandler? OutputChanged;
    public PlaybackEngineState State => (PlaybackEngineState)Volatile.Read(ref state);
    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref positionTicks));
    public TimeSpan Duration => Volatile.Read(ref info)?.Duration ?? TimeSpan.Zero;
    public bool CanSeek => Volatile.Read(ref info)?.Pcm != null;
    public bool HasVideo => false;
    public VideoStreamInfo? Video => null;
    public AudioStreamInfo? Audio => Volatile.Read(ref info)?.Audio;
    public PcmFormat? DecodedPcm => Volatile.Read(ref info)?.Pcm;
    public PlaybackOutputInfo Output => Volatile.Read(ref output);

    public async Task PrepareAsync(PlaybackSource source, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        var localPath = Uri.TryCreate(source.Location, UriKind.Absolute, out var uri) && DlnaAddress.IsHttpTarget(uri)
            ? uri.AbsoluteUri : Path.GetFullPath(source.Location);
        if (Interlocked.Exchange(ref hasPrepared, 1) != 0) throw new InvalidOperationException("エンジンは1曲につき1個作成してください。");
        path = localPath;
        mimeType = source.MimeType;
        preparationCancellation = token.Register(Dispose);
        SetState(PlaybackEngineState.Opening);
        var thread = new Thread(Run) { IsBackground = true, Name = "ContinuousPlayer WASAPI renderer" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        await prepared.Task.WaitAsync(token);
    }

    public void Play() { wantsPlayback = true; Enqueue(new(CommandKind.Play)); }
    public void Pause() { wantsPlayback = false; Enqueue(new(CommandKind.Pause)); }
    public void Stop() { wantsPlayback = false; Enqueue(new(CommandKind.Stop)); }
    public void Seek(TimeSpan position) => Enqueue(new(CommandKind.Seek, position));
    private void Enqueue(Command command)
    {
        lock (lifecycle)
        {
            if (disposed != 0) return;
            commands.Enqueue(command); wake.Set();
        }
    }

    private void Run()
    {
        nint mmcss = 0;
        try
        {
            mmcss = AvSetMmThreadCharacteristics("Audio", out _);
            cancellation.Token.ThrowIfCancellationRequested();
            using (var decoder = new PcmDecoder(path, cancellation.Token, mimeType)) info = decoder.Info;
            if (info.HasVideo || info.Pcm == null) throw new NotSupportedException("音声のみを含むファイルを選択してください。");
            Emit("decoded-format");
            // Verify initialization now, but do not reserve the endpoint while prepared/paused.
            OpenEndpoint(); CloseEndpoint();
            cancellation.Token.ThrowIfCancellationRequested();
            SetState(PlaybackEngineState.Paused);
            prepared.TrySetResult();
            if (Volatile.Read(ref disposed) == 0) Opened?.Invoke(this, EventArgs.Empty);
            while (!cancellation.IsCancellationRequested)
            {
                while (commands.TryDequeue(out var command))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    switch (command.Kind)
                    {
                        case CommandKind.Play: StartPlayback(); break;
                        case CommandKind.Pause: PausePlayback(); break;
                        case CommandKind.Stop: PausePlayback(); SetPosition(TimeSpan.Zero); break;
                        case CommandKind.Seek: SeekPlayback(command.Position); break;
                    }
                }
                if (endpoint?.Started == true)
                {
                    var signal = WaitHandle.WaitAny([wake, endpoint.Ready, cancellation.Token.WaitHandle], 2000);
                    if (signal == WaitHandle.WaitTimeout) throw new TimeoutException("音声出力機器から2秒間応答がありませんでした。");
                    if (signal == 1) Render();
                }
                else WaitHandle.WaitAny([wake, cancellation.Token.WaitHandle]);
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { prepared.TrySetCanceled(); }
        catch (Exception e)
        {
            prepared.TrySetException(e);
            Emit("failed", detail: $"0x{e.HResult:X8} {e.Message}");
            if (Volatile.Read(ref disposed) == 0) Failed?.Invoke(this, new(e.Message, e.HResult));
        }
        finally
        {
            try { CloseEndpoint(); }
            catch (Exception e) { Emit("release-failed", detail: e.Message); }
            finally
            {
                StopPipeline();
                pcmHash?.Dispose();
                if (mmcss != 0) AvRevertMmThreadCharacteristics(mmcss);
                SetState(PlaybackEngineState.Paused);
                // A following track waits for decoder/file handles as well as WASAPI release.
                _ = CompleteReleaseAsync();
            }
        }
    }

    private async Task CompleteReleaseAsync()
    {
        try { await Task.WhenAll(producers); }
        finally
        {
            preparationCancellation.Dispose();
            lock (lifecycle)
            {
                if (disposed != 0) DisposeHandles();
                released.TrySetResult();
            }
        }
    }

    private void OpenEndpoint()
    {
        cancellation.Token.ThrowIfCancellationRequested();
        endpoint = new(deviceId, info!.Pcm!);
        Volatile.Write(ref output, new("WASAPI / Media Foundation", PlaybackOutputMode.Exclusive,
            endpoint.DeviceId, endpoint.DeviceName, deviceId == null, endpoint.Candidate.Format));
        Emit("initialized", detail: $"{(endpoint.Candidate.Extensible ? "WAVEFORMATEXTENSIBLE" : "WAVEFORMATEX")}; buffer={endpoint.BufferFrames} frames");
        if (Volatile.Read(ref disposed) == 0) OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseEndpoint()
    {
        var old = endpoint; endpoint = null;
        try { old?.Dispose(); }
        finally
        {
            if (old != null)
            {
                Emit("released");
                Volatile.Write(ref output, Output with { Format = null });
                if (Volatile.Read(ref disposed) == 0) OutputChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void StartPlayback()
    {
        if (!wantsPlayback || endpoint?.Started == true) return;
        SetState(PlaybackEngineState.Buffering);
        anchor = Position;
        OpenEndpoint();
        pipeline = new(path, anchor, endpoint!.BufferFrames, mimeType);
        producers.Add(pipeline.Finished);
        var decoded = pipeline.Ready.WaitAsync(DecodePreparationTimeout, cancellation.Token).GetAwaiter().GetResult();
        if (decoded.Pcm != info!.Pcm) throw new NotSupportedException("再開時にデコードPCM形式が変わりました。");
        var first = pipeline.Take(true, cancellation.Token);
        if (!wantsPlayback) { PausePlayback(); return; }
        if (first.Length == 0) { FinishPlayback(); return; }
        pcmHash?.Dispose(); pcmHash = trace != null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var firstOutput = endpoint.Write(first, info.Pcm!);
        pcmHash?.AppendData(firstOutput);
        // Wait for another block before the hardware starts; no file reads in the render callback.
        nextBlock = pipeline.Take(true, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        if (!wantsPlayback) { PausePlayback(); return; }
        draining = false;
        endpoint.Start();
        SetState(PlaybackEngineState.Playing);
        Emit("playing");
    }

    private byte[]? nextBlock;
    private void Render()
    {
        var current = endpoint!;
        var played = current.PlayedFrames;
        UpdatePosition(played);
        if (draining)
        {
            if (played >= current.ContentFrames) FinishPlayback();
            return;
        }
        var block = nextBlock ?? pipeline!.Take(false, cancellation.Token);
        nextBlock = null;
        if (block.Length == 0)
        {
            draining = true;
            Emit("draining", current.ContentFrames, played);
            if (played >= current.ContentFrames) FinishPlayback();
        }
        else
        {
            var written = current.Write(block, info!.Pcm!);
            pcmHash?.AppendData(written);
        }
    }

    private void PausePlayback(bool seeking = false)
    {
        if (endpoint?.Started == true) UpdatePosition(endpoint.PlayedFrames);
        Emit("paused", endpoint?.ContentFrames ?? 0, endpoint?.Started == true ? endpoint.PlayedFrames : 0);
        CloseEndpoint(); StopPipeline();
        nextBlock = null;
        SetState(seeking && wantsPlayback ? PlaybackEngineState.Buffering : PlaybackEngineState.Paused);
    }

    private void SeekPlayback(TimeSpan requested)
    {
        PausePlayback(seeking: true);
        var target = requested < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && requested > Duration ? Duration : requested;
        using (var seek = new DecodedPcmQueue(path, target, Math.Max(1, info!.Pcm!.SampleRate / 10), mimeType))
        {
            producers.Add(seek.Finished);
            var decoded = seek.Ready.WaitAsync(DecodePreparationTimeout, cancellation.Token).GetAwaiter().GetResult();
            if (decoded.Pcm != info.Pcm) throw new NotSupportedException("シーク時にPCM形式が変わりました。");
        }
        SetPosition(target); Emit("seek-completed");
        if (Volatile.Read(ref disposed) == 0) SeekCompleted?.Invoke(this, EventArgs.Empty);
        if (wantsPlayback) StartPlayback();
    }

    private void FinishPlayback()
    {
        var content = endpoint?.ContentFrames ?? 0;
        var played = endpoint?.Started == true ? endpoint.PlayedFrames : 0;
        if (endpoint != null) UpdatePosition(played);
        Emit("ended", content, played, pcmHash == null ? null : Convert.ToHexString(pcmHash.GetHashAndReset()));
        wantsPlayback = false;
        CloseEndpoint(); StopPipeline(); nextBlock = null;
        SetState(PlaybackEngineState.Paused);
        if (Volatile.Read(ref disposed) == 0) Ended?.Invoke(this, EventArgs.Empty);
    }

    private void StopPipeline() { pipeline?.Dispose(); pipeline = null; }
    private void UpdatePosition(long played)
    {
        var frames = Math.Min(played, endpoint!.ContentFrames);
        var ticks = anchor.Ticks + frames * 10_000_000 / info!.Pcm!.SampleRate;
        SetPosition(Duration > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(ticks, Duration.Ticks)) : TimeSpan.FromTicks(ticks));
    }
    private void SetPosition(TimeSpan value) => Interlocked.Exchange(ref positionTicks, value.Ticks);
    private void SetState(PlaybackEngineState value)
    {
        if (Interlocked.Exchange(ref state, (int)value) != (int)value && Volatile.Read(ref disposed) == 0)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Emit(string kind, long content = 0, long played = 0, string? hash = null, string? detail = null)
    {
        try { trace?.Invoke(new(kind, DateTimeOffset.UtcNow, path, Audio, DecodedPcm, Output, Position, content, played, hash, detail)); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
    }

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            wantsPlayback = false; cancellation.Cancel(); wake.Set();
            if (Volatile.Read(ref hasPrepared) == 0 || released.Task.IsCompleted)
            {
                DisposeHandles(); released.TrySetResult();
            }
        }
    }
    private void DisposeHandles() { wake.Dispose(); cancellation.Dispose(); }
    public async ValueTask DisposeAsync() { Dispose(); await released.Task; }

    [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", CharSet = CharSet.Unicode)]
    private static extern nint AvSetMmThreadCharacteristics(string taskName, out uint taskIndex);
    [DllImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(nint handle);
}
