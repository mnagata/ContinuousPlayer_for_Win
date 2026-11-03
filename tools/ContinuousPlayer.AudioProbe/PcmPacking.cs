namespace ContinuousPlayer.AudioProbe;

internal static class PcmPacking
{
    public static byte[] Widen(ReadOnlySpan<byte> source, int sourceBits, int targetBits)
    {
        if (sourceBits is not (16 or 24 or 32) || targetBits is not (16 or 24 or 32) || targetBits < sourceBits)
            throw new ArgumentException("Only lossless integer PCM widening is allowed.");
        var sourceWidth = sourceBits / 8;
        var targetWidth = targetBits / 8;
        if (source.Length % sourceWidth != 0) throw new ArgumentException("Partial PCM sample.");
        var destination = new byte[source.Length / sourceWidth * targetWidth];
        for (int from = 0, to = 0; from < source.Length; from += sourceWidth, to += targetWidth)
            source.Slice(from, sourceWidth).CopyTo(destination.AsSpan(to + targetWidth - sourceWidth, sourceWidth));
        return destination; // little endian, left aligned, padding bits zero; no DSP/dither/resampling
    }

    public static void Verify()
    {
        static void Equal(byte[] actual, byte[] expected)
        {
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("PCM packing regression.");
        }
        // Zero, smallest positive/negative value, and signed full-scale boundaries.
        Equal(Widen([0, 0, 0, 1, 0, 0, 255, 255, 255, 255, 255, 127, 0, 0, 128], 24, 32),
            [0, 0, 0, 0, 0, 1, 0, 0, 0, 255, 255, 255, 0, 255, 255, 127, 0, 0, 0, 128]);
        Equal(Widen([0, 0, 1, 0, 255, 255, 255, 127, 0, 128], 16, 32),
            [0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 255, 255, 0, 0, 255, 127, 0, 0, 0, 128]);
        Equal(Widen([1, 0, 255, 255], 16, 24), [0, 1, 0, 0, 255, 255]);
        Equal(Widen([1, 2, 3], 24, 24), [1, 2, 3]);
        foreach (var invalid in new Action[] { () => Widen([0, 0, 0], 24, 16), () => Widen([0], 16, 32) })
        {
            try { invalid(); }
            catch (ArgumentException) { continue; }
            throw new InvalidDataException("Invalid PCM conversion was not rejected.");
        }
        var format = AudioFormat.From(AudioFormat.Pcm(96000, 2, 24, 32, true));
        if (format.ContainerBits != 32 || format.ValidBits != 24 || format.ChannelMask != 3
            || format.Encoding != "00000001-0000-0010-8000-00aa00389b71")
            throw new InvalidDataException("24-in-32 WAVEFORMATEXTENSIBLE regression.");
        Console.WriteLine("PASS PCM boundary samples, lossless widening, invalid conversion rejection, 24-in-32 format");
    }
}
