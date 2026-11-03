using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ContinuousPlayer.AudioProbe;

internal sealed record StreamFormat(int Index, Guid MajorType, Guid SubType, int? SampleRate,
    int? Channels, int? ContainerBits, int? ValidBits);

internal sealed class DecodeResult
{
    public required string File { get; init; }
    public required string Request { get; init; }
    public List<StreamFormat> InputStreams { get; } = [];
    public StreamFormat? DecodedPcm { get; set; }
    public long Frames { get; set; }
    public string? PcmSha256 { get; set; }
    public string? ReferenceWaveComparison { get; set; }
    public bool? RateMatchesNative { get; set; }
    public string Status { get; set; } = "not-tested";
    public string? Error { get; set; }
    public string? HResult { get; set; }
}

internal static class DecoderProbe
{
    private const int FirstAudio = MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM;
    private const int InvalidStream = unchecked((int)0xC00D36B3);

    public static DecodeResult Run(string file, bool preservePrecision, CancellationToken cancellationToken)
    {
        var result = new DecodeResult { File = Path.GetFullPath(file),
            Request = preservePrecision ? "native PCM bits, otherwise PCM32" : "decoder default PCM" };
        IMFSourceReader? reader = null;
        try
        {
            MediaFoundationApi.Startup();
            MediaFoundationInterop.MFCreateSourceReaderFromURL(result.File, null, out reader);
            for (var index = 0; index < 256; index++)
            {
                IMFMediaType native;
                try { reader.GetNativeMediaType(index, 0, out native); }
                catch (COMException e) when (e.HResult == InvalidStream) { break; }
                try { result.InputStreams.Add(Describe(index, native)); }
                finally { Marshal.ReleaseComObject(native); }
            }
            if (result.InputStreams.Any(s => s.MajorType == MediaTypes.MFMediaType_Video))
                throw new NotSupportedException("Video content is outside the stage 1 audio-only probe.");
            var input = result.InputStreams.First(s => s.MajorType == MediaTypes.MFMediaType_Audio);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(FirstAudio, true);
            var desired = new MediaType { MajorType = MediaTypes.MFMediaType_Audio,
                SubType = AudioSubtypes.MFAudioFormat_PCM };
            try
            {
                // Leave rate unset: inspect the PCM actually produced by the decoder (including HE-AAC).
                // Source Reader audio processing/resampling is not enabled.
                if (preservePrecision) desired.BitsPerSample = input.ContainerBits is 16 or 24 or 32
                    ? input.ContainerBits.Value : 32;
                reader.SetCurrentMediaType(FirstAudio, IntPtr.Zero, desired.MediaFoundationObject);
            }
            finally { Marshal.ReleaseComObject(desired.MediaFoundationObject); }
            reader.GetCurrentMediaType(FirstAudio, out var current);
            try { result.DecodedPcm = Describe(input.Index, current); }
            finally { Marshal.ReleaseComObject(current); }
            var pcm = result.DecodedPcm;
            if (pcm.SubType != AudioSubtypes.MFAudioFormat_PCM || pcm.ContainerBits is not (16 or 24 or 32)
                || pcm.Channels is null || pcm.SampleRate is null)
                throw new NotSupportedException("Decoder did not produce a supported integer PCM type.");
            var blockAlign = pcm.ContainerBits.Value / 8 * pcm.Channels.Value;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reader.ReadSample(FirstAudio, 0, out _, out var flags, out _, out var sample);
                try
                {
                    if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0) throw new InvalidDataException("Source Reader error flag.");
                    if ((flags & (MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED |
                        MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED)) != 0)
                        throw new NotSupportedException("Decoder format changed mid-stream; PCM is not reinterpreted.");
                    if (sample is not null)
                    {
                        sample.ConvertToContiguousBuffer(out var buffer);
                        try
                        {
                            buffer.Lock(out var pointer, out _, out var count);
                            try
                            {
                                if (count % blockAlign != 0) throw new InvalidDataException("Partial PCM frame.");
                                var data = new byte[count];
                                Marshal.Copy(pointer, data, 0, count);
                                hash.AppendData(data);
                                bytes += count;
                            }
                            finally { buffer.Unlock(); }
                        }
                        finally { Marshal.ReleaseComObject(buffer); }
                    }
                    if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                }
                finally { if (sample is not null) Marshal.ReleaseComObject(sample); }
            }
            result.Frames = bytes / blockAlign;
            if (result.Frames == 0) throw new InvalidDataException("Decoder returned no PCM frames.");
            result.PcmSha256 = Convert.ToHexString(hash.GetHashAndReset());
            result.RateMatchesNative = pcm.SampleRate == input.SampleRate;
            result.ReferenceWaveComparison = CompareReference(result, blockAlign);
            if (result.ReferenceWaveComparison == "PCM mismatch")
                throw new InvalidDataException("Decoded PCM does not match the reference WAV.");
            result.Status = "decoded";
        }
        catch (Exception e)
        {
            result.Status = e is OperationCanceledException ? "cancelled" : "failed";
            result.Error = e.Message;
            result.HResult = $"0x{e.HResult:X8}";
        }
        finally { if (reader is not null) Marshal.ReleaseComObject(reader); }
        return result;
    }

    private static StreamFormat Describe(int index, IMFMediaType native)
    {
        var type = new MediaType(native);
        int? Attribute(Guid key) { var value = type.TryGetUInt32(key, -1); return value < 0 ? null : value; }
        return new(index, type.MajorType, type.SubType,
            Attribute(MediaFoundationAttributes.MF_MT_AUDIO_SAMPLES_PER_SECOND),
            Attribute(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS),
            Attribute(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE),
            Attribute(new Guid("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06")));
    }

    private static string CompareReference(DecodeResult result, int blockAlign)
    {
        // Only local WAV and generated lossless FLAC/ALAC fixtures have a PCM reference.
        var extension = Path.GetExtension(result.File).ToLowerInvariant();
        if (extension != ".wav" && extension != ".flac" && !result.File.EndsWith(".alac.m4a", StringComparison.OrdinalIgnoreCase))
            return "not-applicable (lossy or no declared reference)";
        var reference = result.File.EndsWith(".alac.m4a", StringComparison.OrdinalIgnoreCase)
            ? result.File[..^9] + ".wav" : Path.ChangeExtension(result.File, ".wav");
        if (!System.IO.File.Exists(reference)) return "no-reference";
        using var wave = new WaveFileReader(reference);
        var pcm = result.DecodedPcm!;
        if (wave.WaveFormat.Encoding != WaveFormatEncoding.Pcm || wave.WaveFormat.BitsPerSample > pcm.ContainerBits
            || wave.WaveFormat.SampleRate != pcm.SampleRate
            || wave.WaveFormat.Channels != pcm.Channels) return "different-format (no sample comparison)";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[wave.WaveFormat.BlockAlign * 4096];
        int count;
        long bytes = 0;
        while ((count = wave.Read(buffer, 0, buffer.Length)) != 0)
        {
            var widened = PcmPacking.Widen(buffer.AsSpan(0, count), wave.WaveFormat.BitsPerSample, pcm.ContainerBits!.Value);
            hash.AppendData(widened);
            bytes += widened.Length;
        }
        return Convert.ToHexString(hash.GetHashAndReset()) == result.PcmSha256 && bytes / blockAlign == result.Frames
            ? wave.WaveFormat.BitsPerSample == pcm.ContainerBits ? "exact PCM match" : "exact PCM match after lossless widening"
            : "PCM mismatch";
    }
}
