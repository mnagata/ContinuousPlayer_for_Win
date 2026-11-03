using System.Net;
using System.Net.Http.Headers;

namespace ContinuousPlayer.Core;

public enum AudioNetworkFailure { Timeout = 1, ResourceChanged, RangeRequired, RequestFailed, Truncated }

public sealed class AudioNetworkException : IOException
{
    public AudioNetworkFailure Failure { get; }
    public AudioNetworkException(AudioNetworkFailure failure, string message, Exception? inner = null) : base(message, inner)
    {
        Failure = failure;
        HResult = unchecked((int)0xA0040000) + (int)failure;
    }
}

/// <summary>Read-only HTTP resource with bounded LRU bytes and verified byte ranges. No full-file downloads.</summary>
public sealed class HttpAudioStream : Stream
{
    public const int BlockBytes = 1024 * 1024;
    public const int CachedBlocks = 8;
    private readonly object gate = new();
    private readonly HttpClient client;
    private readonly Uri address;
    private readonly CancellationToken lifetime;
    private readonly TimeSpan requestTimeout;
    private readonly Dictionary<long, (byte[] Data, LinkedListNode<long> Node)> cache = [];
    private readonly LinkedList<long> lru = [];
    private long length = -1, position;
    private string? etag;
    private DateTimeOffset? modified;
    private bool disposed;
    private Exception? lastError;
    public string? ContentType { get; private set; }
    public Exception? LastError => Volatile.Read(ref lastError);
    public long CachedBytes { get { lock (gate) return cache.Values.Sum(x => (long)x.Data.Length); } }

    public HttpAudioStream(Uri address, CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        if (!DlnaAddress.IsHttpTarget(address)) throw new ArgumentException("HTTP音声URLが不正です。", nameof(address));
        this.address = address; lifetime = cancellationToken;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.None, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };
        try { GetBlock(0); }
        catch { client.Dispose(); throw; }
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get { lock (gate) return position; } set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        lock (gate) { var read = ReadAt(position, buffer); position += read; return read; }
    }

    // The COM adapter can clone cursors without sharing their seek positions.
    public int ReadAt(long offset, Span<byte> buffer)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lifetime.ThrowIfCancellationRequested();
            if (offset < 0) throw new IOException("負のHTTP読み取り位置です。");
            if (offset >= length || buffer.Length == 0) return 0;
            try
            {
                var data = GetBlock(offset / BlockBytes);
                var within = (int)(offset % BlockBytes);
                var count = Math.Min(buffer.Length, data.Length - within);
                data.AsSpan(within, count).CopyTo(buffer);
                return count;
            }
            catch (Exception e) { Interlocked.CompareExchange(ref lastError, e, null); throw; }
        }
    }

    private byte[] GetBlock(long index)
    {
        if (cache.TryGetValue(index, out var found))
        { lru.Remove(found.Node); lru.AddFirst(found.Node); return found.Data; }
        var data = FetchAsync(index).GetAwaiter().GetResult(); // Only decoder/COM workers call this stream.
        var node = lru.AddFirst(index); cache.Add(index, (data, node));
        if (cache.Count > CachedBlocks) { var oldest = lru.Last!; cache.Remove(oldest.Value); lru.RemoveLast(); }
        return data;
    }

    private async Task<byte[]> FetchAsync(long index)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(requestTimeout);
        try
        {
            var start = checked(index * BlockBytes);
            var end = length < 0 ? start + BlockBytes - 1 : Math.Min(length - 1, start + BlockBytes - 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Range = new(start, end);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (etag != null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
            else if (modified != null) request.Headers.IfRange = new RangeConditionHeaderValue(modified.Value);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                if (response.StatusCode == HttpStatusCode.OK)
                    throw new AudioNetworkException(etag != null || modified != null ? AudioNetworkFailure.ResourceChanged : AudioNetworkFailure.RangeRequired,
                        "配信データが変更されたか、サーバーがHTTP Rangeに対応していません。再試行または標準再生を選んでください。");
                throw new AudioNetworkException(AudioNetworkFailure.RequestFailed,
                    $"音声データを取得できません（HTTP {(int)response.StatusCode}）。再試行でURLを取得し直してください。");
            }
            var range = response.Content.Headers.ContentRange;
            if (range is not { Unit: "bytes", From: { } from, To: { } to, Length: > 0 } || from != start ||
                to != Math.Min(range.Length.Value - 1, end))
                throw new AudioNetworkException(AudioNetworkFailure.RangeRequired, "サーバーから正しいContent-Rangeと全体サイズを取得できません。");
            var responseTag = response.Headers.ETag?.ToString();
            var responseModified = response.Content.Headers.LastModified;
            if (length < 0)
            {
                length = range.Length.Value;
                etag = response.Headers.ETag is { IsWeak: false } ? responseTag : null;
                modified = responseModified;
                ContentType = response.Content.Headers.ContentType?.MediaType;
            }
            else if (length != range.Length || etag != null && responseTag != etag ||
                etag == null && modified != null && responseModified != modified)
                throw new AudioNetworkException(AudioNetworkFailure.ResourceChanged, "再生中に配信データが変更されました。混在を防ぐため停止しました。再試行してください。");
            var count = checked((int)(to - from + 1));
            if (response.Content.Headers.ContentLength is { } declared && declared != count)
                throw new AudioNetworkException(AudioNetworkFailure.Truncated, "音声応答のサイズが要求範囲と一致しません。");
            var data = new byte[count];
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var read = 0;
            while (read < data.Length)
            {
                var n = await body.ReadAsync(data.AsMemory(read), deadline.Token).ConfigureAwait(false);
                if (n == 0) throw new AudioNetworkException(AudioNetworkFailure.Truncated, "音声応答が途中で切断されました。自然終了として扱いません。");
                read += n;
            }
            return data;
        }
        catch (OperationCanceledException e) when (!lifetime.IsCancellationRequested)
        { throw new AudioNetworkException(AudioNetworkFailure.Timeout, "HTTP音声の取得がタイムアウトしました。再試行または標準再生を選んでください。", e); }
        catch (HttpRequestException e)
        { throw new AudioNetworkException(AudioNetworkFailure.RequestFailed, "HTTP音声の接続が切断されました。再試行でURLを取得し直してください。", e); }
        catch (IOException e) when (e is not AudioNetworkException)
        { throw new AudioNetworkException(AudioNetworkFailure.Truncated, "HTTP音声の応答を最後まで受信できませんでした。自然終了として扱いません。", e); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var next = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : origin == SeekOrigin.End ? length : throw new ArgumentOutOfRangeException(nameof(origin))) + offset);
            if (next < 0) throw new IOException("負のシーク位置です。");
            return position = next;
        }
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        lock (gate) { if (disposed) return; disposed = true; client.Dispose(); cache.Clear(); lru.Clear(); }
        base.Dispose(disposing);
    }
}
