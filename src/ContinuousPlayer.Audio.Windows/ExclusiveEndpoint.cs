using System.Runtime.InteropServices;
using ContinuousPlayer.Core;
using NAudio.CoreAudioApi;

namespace ContinuousPlayer.Audio.Windows;

internal sealed class ExclusiveEndpoint : IDisposable
{
    private readonly MMDevice device;
    private AudioClient? client;
    public EventWaitHandle Ready { get; } = new(false, EventResetMode.AutoReset);
    public PcmOutputCandidate Candidate { get; private set; } = null!;
    public string DeviceId => device.ID;
    public string DeviceName => device.FriendlyName;
    public int BufferFrames => client!.BufferSize;
    public bool Started { get; private set; }
    public long SubmittedFrames { get; private set; }
    public long ContentFrames { get; private set; }
    public long PlayedFrames
    {
        get
        {
            client!.AudioClockClient.GetPosition(out var position, out _);
            return (long)Math.Floor(position * (double)Candidate.Format.SampleRate / client.AudioClockClient.Frequency);
        }
    }

    public ExclusiveEndpoint(string? deviceId, PcmFormat pcm)
    {
        device = AudioDevices.Resolve(deviceId);
        try
        {
            foreach (var candidate in ExclusiveAudioPolicy.Candidates(pcm))
            {
                client = device.AudioClient;
                var wave = AudioDevices.WaveFormat(candidate);
                if (!client.IsFormatSupported(AudioClientShareMode.Exclusive, wave))
                {
                    client.Dispose(); client = null; continue;
                }
                try
                {
                    var period = Math.Max(1_000_000, client.MinimumDevicePeriod);
                    try { Initialize(period); }
                    catch (COMException e) when (e.HResult == unchecked((int)0x88890019))
                    {
                        period = (long)Math.Round(10_000_000.0 * client.BufferSize / pcm.SampleRate);
                        client.Dispose(); client = device.AudioClient;
                        Initialize(period);
                    }
                    void Initialize(long duration) => client.Initialize(AudioClientShareMode.Exclusive,
                        AudioClientStreamFlags.EventCallback, duration, duration, wave, Guid.Empty);
                    Candidate = candidate;
                    client.SetEventHandle(Ready.SafeWaitHandle.DangerousGetHandle());
                    return;
                }
                catch (COMException e) when (e.HResult == unchecked((int)0x88890008))
                {
                    client.Dispose(); client = null;
                }
            }
            throw new NotSupportedException($"この出力機器では {pcm.SampleRate:N0} Hz / {pcm.Channels} ch / {pcm.ValidBits}-bitを精度を保持して使用できません。");
        }
        catch { Dispose(); throw; }
    }

    public byte[] Write(byte[] source, PcmFormat decoded)
    {
        var output = Candidate.Format;
        var frameCount = source.Length / (decoded.Channels * decoded.ContainerBits / 8);
        if (frameCount > BufferFrames) throw new InvalidDataException("PCMバッファが出力バッファより大きくなっています。");
        var converted = ExclusiveAudioPolicy.Widen(source, decoded.ContainerBits, output.ContainerBits);
        var buffer = new byte[BufferFrames * output.Channels * output.ContainerBits / 8];
        converted.CopyTo(buffer, 0); // tail padding only; actual content is never faded or resampled
        var render = client!.AudioRenderClient;
        var pointer = render.GetBuffer(BufferFrames);
        try { Marshal.Copy(buffer, 0, pointer, buffer.Length); }
        finally { render.ReleaseBuffer(BufferFrames, AudioClientBufferFlags.None); }
        SubmittedFrames += BufferFrames;
        ContentFrames += frameCount;
        return converted;
    }

    public void Start() { client!.Start(); Started = true; }
    public void Dispose()
    {
        try { if (Started) { client!.Stop(); Started = false; } }
        finally { client?.Dispose(); client = null; device.Dispose(); Ready.Dispose(); }
    }
}
