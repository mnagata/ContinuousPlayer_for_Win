using System.Runtime.InteropServices;
using ContinuousPlayer.Core;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;

namespace ContinuousPlayer.Audio.Windows;

public sealed record LocalAudioInfo(bool HasVideo, AudioStreamInfo? Audio, PcmFormat? Pcm, TimeSpan Duration);

/// <summary>A synchronous Source Reader owned and released by one MTA decoder thread.</summary>
internal sealed class PcmDecoder : IDisposable
{
    private IMFSourceReader reader = null!;
    private IMFByteStream? byteStream;
    private HttpAudioStream? remote;
    private HttpComStream? comStream;
    private readonly int audioIndex;
    private byte[] packet = [];
    private int offset;
    private bool ended;
    private long skipToFrame;
    private bool aligning = true;
    public LocalAudioInfo Info { get; }

    public static Task<LocalAudioInfo> InspectAsync(string path, CancellationToken token, string? mimeType = null) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var decoder = new PcmDecoder(path, token, mimeType);
        token.ThrowIfCancellationRequested();
        return decoder.Info;
    }, token);

    public PcmDecoder(string path, CancellationToken token = default, string? mimeType = null)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            MediaFoundationApi.Startup();
            if (Uri.TryCreate(path, UriKind.Absolute, out var address) && DlnaAddress.IsHttpTarget(address))
            {
                remote = new HttpAudioStream(address, token);
                comStream = new(remote);
                MediaFoundationInterop.MFCreateMFByteStreamOnStream(comStream, out byteStream);
                var contentType = remote.ContentType is { } type && type != "application/octet-stream" ? type : mimeType;
                if (contentType != null)
                    ((IMFAttributes)byteStream).SetString(new Guid("fc358288-3cb6-460c-a424-b6681260375a"), contentType);
                MediaFoundationInterop.MFCreateSourceReaderFromByteStream(byteStream, null, out reader);
            }
            else
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path))
                    throw new FileNotFoundException("音声ファイルが見つかりません。", path);
                MediaFoundationInterop.MFCreateSourceReaderFromURL(path, null, out reader);
            }
            var hasVideo = false;
            AudioStreamInfo? audio = null;
            Guid subtype = Guid.Empty;
            for (var index = 0; index < 256; index++)
            {
                IMFMediaType native;
                try { reader.GetNativeMediaType(index, 0, out native); }
                catch (COMException e) when (e.HResult == unchecked((int)0xC00D36B3)) { break; }
                try
                {
                    var type = new MediaType(native);
                    hasVideo |= type.MajorType == MediaTypes.MFMediaType_Video;
                    if (audio == null && type.MajorType == MediaTypes.MFMediaType_Audio)
                    {
                        audioIndex = index;
                        subtype = type.SubType;
                        var bits = type.TryGetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE, 0);
                        audio = new(Codec(subtype), type.SampleRate, type.ChannelCount, bits > 0 ? bits : null);
                    }
                }
                finally { Marshal.ReleaseComObject(native); }
            }
            var duration = ReadDuration();
            if (hasVideo) { Info = new(true, audio, null, duration); return; }
            if (audio == null) throw new NotSupportedException("音声ストリームが見つかりません。");
            if (subtype == AudioSubtypes.MFAudioFormat_Float)
                throw new NotSupportedException("浮動小数点WAVの排他再生はこの段階では未対応です。");
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(audioIndex, true);
            var desired = new MediaType { MajorType = MediaTypes.MFMediaType_Audio, SubType = AudioSubtypes.MFAudioFormat_PCM,
                BitsPerSample = audio.BitsPerSample is 16 or 24 or 32 ? audio.BitsPerSample.Value : 32 };
            try { reader.SetCurrentMediaType(audioIndex, IntPtr.Zero, desired.MediaFoundationObject); }
            finally { Marshal.ReleaseComObject(desired.MediaFoundationObject); }
            reader.GetCurrentMediaType(audioIndex, out var decoded);
            try
            {
                var type = new MediaType(decoded);
                var bits = type.BitsPerSample;
                var lossless = audio.Codec is "PCM" or "FLAC" or "ALAC";
                if (type.SubType != AudioSubtypes.MFAudioFormat_PCM || bits is not (16 or 24 or 32) ||
                    lossless && (type.SampleRate != audio.SampleRate || type.ChannelCount != audio.Channels) ||
                    type.SampleRate <= 0 || type.ChannelCount <= 0 || audio.BitsPerSample > bits)
                    throw new NotSupportedException("音源の精度を保持する整数PCMを取得できませんでした。");
                var valid = type.TryGetUInt32(new Guid("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06"), bits);
                var mask = type.TryGetUInt32(new Guid("55fb5765-644a-4caf-8479-938983bb1588"), 0);
                Info = new(false, audio, new(type.SampleRate, type.ChannelCount, PcmEncoding.Integer, bits, valid, mask), duration);
            }
            finally { Marshal.ReleaseComObject(decoded); }
        }
        catch
        {
            var failure = remote?.LastError;
            Dispose();
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    public void Seek(TimeSpan position)
    {
        var value = PropVariant.FromLong(position.Ticks);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(value));
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            try { reader.SetCurrentPosition(Guid.Empty, pointer); }
            catch { RethrowTransportFailure(); throw; }
        }
        finally { Marshal.FreeHGlobal(pointer); }
        packet = []; offset = 0; ended = false;
        skipToFrame = Frames(position, Info.Pcm!.SampleRate);
        aligning = true;
    }

    public byte[] ReadFrames(int frames, CancellationToken token)
    {
        var pcm = Info.Pcm ?? throw new NotSupportedException("映像を含むファイルは排他音声経路では再生しません。");
        var align = pcm.Channels * pcm.ContainerBits / 8;
        var result = new byte[frames * align];
        var written = 0;
        while (written < result.Length)
        {
            token.ThrowIfCancellationRequested();
            if (offset < packet.Length)
            {
                var count = Math.Min(result.Length - written, packet.Length - offset);
                packet.AsSpan(offset, count).CopyTo(result.AsSpan(written));
                offset += count; written += count;
                continue;
            }
            if (ended) break;
            MF_SOURCE_READER_FLAG flags;
            ulong unsignedTimestamp;
            IMFSample sample;
            try { reader.ReadSample(audioIndex, 0, out _, out flags, out unsignedTimestamp, out sample); }
            catch { RethrowTransportFailure(); token.ThrowIfCancellationRequested(); throw; }
            try
            {
                RethrowTransportFailure();
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                    throw new IOException("音声デコーダーがエラーを返しました。");
                if ((flags & (MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED |
                    MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED)) != 0)
                    throw new NotSupportedException("曲の途中でPCM形式が変わりました。出力を停止しました。");
                ended = (flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0;
                packet = []; offset = 0;
                if (sample == null) continue;
                sample.ConvertToContiguousBuffer(out var buffer);
                try
                {
                    buffer.Lock(out var pointer, out _, out var count);
                    try
                    {
                        if (count % align != 0) throw new InvalidDataException("PCMフレームが不完全です。");
                        packet = new byte[count];
                        Marshal.Copy(pointer, packet, 0, count);
                    }
                    finally { buffer.Unlock(); }
                }
                finally { Marshal.ReleaseComObject(buffer); }
                var firstFrame = (long)Math.Round(unchecked((long)unsignedTimestamp) * (double)pcm.SampleRate / 10_000_000);
                if (aligning)
                {
                    if (firstFrame > skipToFrame + 1) throw new IOException("指定位置より前にシークできませんでした。");
                    offset = (int)Math.Min(packet.Length, Math.Max(0, skipToFrame - firstFrame) * align);
                    if (offset < packet.Length) aligning = false;
                }
            }
            finally { if (sample != null) Marshal.ReleaseComObject(sample); }
        }
        return written == result.Length ? result : result[..written];
    }

    private TimeSpan ReadDuration()
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        // PropVariantClear must see an initialized variant even if the query fails.
        Marshal.StructureToPtr(default(PropVariant), pointer, false);
        try
        {
            var hr = reader.GetPresentationAttribute(MediaFoundationInterop.MF_SOURCE_READER_MEDIASOURCE,
                MediaFoundationAttributes.MF_PD_DURATION, pointer);
            if (hr == MediaFoundationErrors.MF_E_ATTRIBUTENOTFOUND) return TimeSpan.Zero;
            Marshal.ThrowExceptionForHR(hr);
            return TimeSpan.FromTicks((long)Marshal.PtrToStructure<PropVariant>(pointer).Value);
        }
        finally { PropVariant.Clear(pointer); Marshal.FreeHGlobal(pointer); }
    }

    private static string Codec(Guid subtype) => BitConverter.ToUInt32(subtype.ToByteArray()) switch
    {
        1 => "PCM", 3 => "Float", 0x55 => "MP3", 0x1600 or 0x1610 => "AAC", 0xf1ac => "FLAC", 0x6c61 => "ALAC", _ => subtype.ToString()
    };
    internal static long Frames(TimeSpan position, int rate) => position.Ticks / 10_000_000 * rate + position.Ticks % 10_000_000 * rate / 10_000_000;
    private void RethrowTransportFailure()
    {
        if (remote?.LastError is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    public void Dispose()
    {
        try { if (reader != null) { Marshal.ReleaseComObject(reader); reader = null!; } }
        finally
        {
            try { if (byteStream != null) { byteStream.Close(); Marshal.ReleaseComObject(byteStream); byteStream = null; } }
            finally { remote?.Dispose(); remote = null; comStream = null; }
        }
    }
}
