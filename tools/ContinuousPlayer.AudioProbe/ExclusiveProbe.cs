using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ContinuousPlayer.AudioProbe;

internal sealed class OutputResult
{
    public required string DeviceId { get; init; }
    public required AudioFormat Requested { get; init; }
    public bool? Supported { get; set; }
    public AudioFormat? Initialized { get; set; }
    public int? BufferFrames { get; set; }
    public double? BufferMilliseconds { get; set; }
    public double? OpenMilliseconds { get; set; }
    public long SubmittedFrames { get; set; }
    public long? ClockPosition { get; set; }
    public long? ClockFrequency { get; set; }
    public double? StreamSeconds { get; set; }
    public string Status { get; set; } = "not-tested";
    public string DacDisplay { get; init; } = "unverified";
    public string? Error { get; set; }
    public string? HResult { get; set; }
}

internal static class ExclusiveProbe
{
    public static OutputResult Run(MMDevice device, WaveFormat format, int seconds,
        CancellationToken cancellationToken)
    {
        var result = new OutputResult { DeviceId = device.ID, Requested = AudioFormat.From(format) };
        AudioClient? client = null;
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            client = device.AudioClient; // NAudio 2.x returns a fresh IAudioClient on every access.
            result.Supported = client.IsFormatSupported(AudioClientShareMode.Exclusive, format);
            if (result.Supported != true)
            {
                result.Status = "unsupported";
                return result;
            }
            long period = Math.Max(1_000_000, client.MinimumDevicePeriod); // 100 ms, not a DAC sync delay
            try { Initialize(client, format, period); }
            catch (COMException e) when (e.HResult == unchecked((int)0x88890019))
            {
                period = (long)Math.Round(10_000_000.0 * client.BufferSize / format.SampleRate);
                client.Dispose();
                client = null;
                client = device.AudioClient;
                Initialize(client, format, period);
            }
            result.Initialized = AudioFormat.From(format); // exact Initialize argument; never MixFormat
            result.OpenMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            result.BufferFrames = client.BufferSize;
            result.BufferMilliseconds = 1000.0 * client.BufferSize / format.SampleRate;
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            result.Status = "initialized";
            if (seconds == 0) return result;

            var render = client.AudioRenderClient;
            void SupplySilence()
            {
                render.GetBuffer(client.BufferSize);
                render.ReleaseBuffer(client.BufferSize, AudioClientBufferFlags.Silent);
                result.SubmittedFrames += client.BufferSize;
            }
            SupplySilence(); // prefill before starting the hardware clock
            client.Start();
            try
            {
                var clock = client.AudioClockClient;
                result.ClockFrequency = checked((long)clock.Frequency);
                var targetFrames = (long)seconds * format.SampleRate;
                var deadline = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ready.WaitOne(TimeSpan.FromSeconds(2)))
                        throw new TimeoutException("WASAPI buffer event did not arrive within 2 seconds.");
                    clock.GetPosition(out var position, out _);
                    result.ClockPosition = checked((long)position);
                    result.StreamSeconds = position / (double)clock.Frequency;
                    if (result.StreamSeconds >= seconds) break;
                    if (deadline.Elapsed.TotalSeconds > seconds + 5)
                        throw new TimeoutException("The hardware clock did not reach the requested duration.");
                    if (result.SubmittedFrames < targetFrames) SupplySilence();
                }
                result.Status = "streamed-silence";
            }
            finally { client.Stop(); }
        }
        catch (Exception e)
        {
            result.Status = e is OperationCanceledException ? "cancelled" : "failed";
            result.Error = e.Message;
            result.HResult = $"0x{e.HResult:X8}";
        }
        finally { client?.Dispose(); }
        return result;
    }

    private static void Initialize(AudioClient client, WaveFormat format, long period) =>
        client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback,
            period, period, format, Guid.Empty);
}
