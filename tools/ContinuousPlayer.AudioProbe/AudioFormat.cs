using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ContinuousPlayer.AudioProbe;

internal sealed record AudioFormat(int SampleRate, int Channels, string Encoding,
    int ContainerBits, int ValidBits, int ChannelMask, string FormatTag)
{
    public static AudioFormat From(WaveFormat format)
    {
        // Serialize includes the fmt chunk length; valid bits/mask follow WAVEFORMATEX.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        format.Serialize(writer);
        var bytes = stream.ToArray();
        var extensible = format as WaveFormatExtensible;
        return new(format.SampleRate, format.Channels,
            extensible is null ? format.Encoding.ToString() : extensible.SubFormat.ToString(),
            format.BitsPerSample, extensible is null ? format.BitsPerSample : BitConverter.ToUInt16(bytes, 22),
            extensible is null ? 0 : BitConverter.ToInt32(bytes, 24), format.Encoding.ToString());
    }

    public static WaveFormat Pcm(int rate, int channels, int bits, int container, bool extensible)
    {
        if (!extensible && bits == container) return new WaveFormat(rate, bits, channels);
        // Build the documented 40-byte WAVEFORMATEXTENSIBLE explicitly. The NAudio 2.x
        // convenience constructor treats 32-bit containers as float and cannot set valid bits.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0xfffe);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * container / 8);
        writer.Write((ushort)(channels * container / 8));
        writer.Write((ushort)container);
        writer.Write((ushort)22);
        writer.Write((ushort)bits);
        writer.Write(channels == 1 ? 4 : 3); // front center / front left + right
        writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        var pointer = Marshal.AllocHGlobal(40);
        try
        {
            Marshal.Copy(stream.ToArray(), 0, pointer, 40);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
