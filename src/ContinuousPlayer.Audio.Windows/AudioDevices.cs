using ContinuousPlayer.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ContinuousPlayer.Audio.Windows;

public sealed record AudioEndpoint(string Id, string Name);

public static class AudioDevices
{
    public static IReadOnlyList<AudioEndpoint> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        return devices;
    }

    internal static MMDevice Resolve(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try { device = id == null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : enumerator.GetDevice(id); }
        catch (System.Runtime.InteropServices.COMException e)
        {
            throw new System.Runtime.InteropServices.COMException("選択した音声出力機器が見つかりません。接続を確認してください。別の機器への切り替えは行いません。", e.HResult);
        }
        if (device.State == DeviceState.Active && device.DataFlow == DataFlow.Render) return device;
        device.Dispose();
        throw new IOException("選択した音声出力機器を利用できません。接続を確認してください。");
    }

    internal static WaveFormat WaveFormat(PcmOutputCandidate candidate)
    {
        var f = candidate.Format;
        if (!candidate.Extensible) return new WaveFormat(f.SampleRate, f.ContainerBits, f.Channels);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0xfffe); writer.Write((ushort)f.Channels); writer.Write(f.SampleRate);
        writer.Write(f.SampleRate * f.Channels * f.ContainerBits / 8);
        writer.Write((ushort)(f.Channels * f.ContainerBits / 8)); writer.Write((ushort)f.ContainerBits);
        writer.Write((ushort)22); writer.Write((ushort)f.ValidBits); writer.Write(f.ChannelMask);
        writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        var pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(40);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(stream.ToArray(), 0, pointer, 40);
            return NAudio.Wave.WaveFormat.MarshalFromPtr(pointer);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer); }
    }
}
