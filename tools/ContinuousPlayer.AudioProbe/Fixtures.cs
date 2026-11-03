using NAudio.Wave;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ContinuousPlayer.AudioProbe;

internal static class Fixtures
{
    public static async Task<int> CreateAsync(string directory, int[] rates, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var failures = 0;
        foreach (var rate in rates)
        foreach (var bits in new[] { 16, 24 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(directory, $"pcm-{rate}-{bits}.wav"));
            // Two seconds of deterministic non-silent samples, including low 24-bit values.
            // Fixtures are decoded and hashed, never sent to the DAC by this tool.
            using (var wave = new WaveFileWriter(path, new WaveFormat(rate, bits, 2)))
            {
                var data = new byte[rate * 2 * bits / 8 * 2];
                uint state = 0x13579bdf;
                for (var offset = 0; offset < data.Length; offset += bits / 8)
                {
                    state = unchecked(state * 1664525 + 1013904223);
                    var value = ((int)(state >> 8) - 0x800000) / 16;
                    if (bits == 16) value >>= 8;
                    for (var b = 0; b < bits / 8; b++) data[offset + b] = (byte)(value >> (b * 8));
                }
                wave.Write(data, 0, data.Length);
            }
            if (!await TranscodeAsync(path, Path.ChangeExtension(path, ".flac"),
                MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High), rate, bits)) failures++;
            // AAC is commonly limited to 44.1/48 kHz by installed encoders. Record failures as unverified.
            if (bits == 16 && rate is 44100 or 48000)
                if (!await TranscodeAsync(path, Path.ChangeExtension(path, ".m4a"),
                    MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High), rate, bits)) failures++;
        }
        return failures;
    }

    private static async Task<bool> TranscodeAsync(string source, string destination, MediaEncodingProfile profile,
        int rate, int bits)
    {
        try
        {
            profile.Audio.SampleRate = (uint)rate;
            profile.Audio.ChannelCount = 2;
            if (profile.Audio.Subtype == MediaEncodingSubtypes.Flac) profile.Audio.BitsPerSample = (uint)bits;
            var input = await StorageFile.GetFileFromPathAsync(source);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
            var output = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.ReplaceExisting);
            var prepared = await new MediaTranscoder().PrepareFileTranscodeAsync(input, output, profile);
            if (!prepared.CanTranscode) throw new InvalidOperationException(prepared.FailureReason.ToString());
            await prepared.TranscodeAsync();
            Console.WriteLine($"fixture: {Path.GetFileName(destination)}");
            return true;
        }
        catch (Exception e)
        {
            // Never leave a failed/empty transcode masquerading as a valid fixture.
            if (File.Exists(destination)) File.Delete(destination);
            Console.Error.WriteLine($"fixture unavailable: {Path.GetFileName(destination)}: {e.Message}");
            return false;
        }
    }
}
