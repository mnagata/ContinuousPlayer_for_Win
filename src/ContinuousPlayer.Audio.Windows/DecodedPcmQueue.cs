using System.Collections.Concurrent;

namespace ContinuousPlayer.Audio.Windows;

/// <summary>Bounded PCM lookahead. All Source Reader calls and COM release stay on its producer.</summary>
internal sealed class DecodedPcmQueue : IDisposable
{
    private readonly BlockingCollection<byte[]> blocks = new(8);
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource<LocalAudioInfo> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? failure;
    private int disposed;
    public Task<LocalAudioInfo> Ready => ready.Task;
    public Task Finished => finished.Task;

    public DecodedPcmQueue(string path, TimeSpan position, int frames, string? mimeType = null)
    {
        var thread = new Thread(() => Produce(path, position, frames, mimeType)) { IsBackground = true, Name = "ContinuousPlayer PCM decoder" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Produce(string path, TimeSpan position, int frames, string? mimeType)
    {
        try
        {
            using var decoder = new PcmDecoder(path, cancellation.Token, mimeType);
            if (decoder.Info.HasVideo) throw new NotSupportedException("映像を含むファイルです。");
            if (position > TimeSpan.Zero) decoder.Seek(position);
            var first = decoder.ReadFrames(frames, cancellation.Token); // confirms seek before reporting ready
            ready.TrySetResult(decoder.Info);
            blocks.Add(first, cancellation.Token);
            while (first.Length != 0)
            {
                first = decoder.ReadFrames(frames, cancellation.Token);
                blocks.Add(first, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception e) { failure = e; ready.TrySetException(e); }
        finally { blocks.CompleteAdding(); finished.TrySetResult(); }
    }

    public byte[] Take(bool preparing, CancellationToken token)
    {
        if (blocks.TryTake(out var block, preparing ? 3000 : 0, token)) return block;
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        token.ThrowIfCancellationRequested();
        throw new IOException(blocks.IsCompleted ? "PCMの終了通知がありません。" : "音声データの供給が間に合わないため停止しました。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel();
        _ = ReleaseAsync();
    }

    private async Task ReleaseAsync()
    {
        await finished.Task;
        blocks.Dispose();
        cancellation.Dispose();
    }
}
