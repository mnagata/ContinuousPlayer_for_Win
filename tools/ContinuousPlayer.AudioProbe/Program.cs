using System.Runtime.InteropServices;
using System.Text.Json;
using ContinuousPlayer.AudioProbe;
using NAudio.CoreAudioApi;

return await ProbeProgram.RunAsync(args);

internal static class ProbeProgram
{
    private static readonly int[] StandardRates = [44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine("""
                    AudioProbe — WASAPI / Media Foundation and production engine validation
                    list     [--output report.json]
                    matrix   --device ENDPOINT_ID [--rates 44100,48000,88200,96000,176400,192000]
                             [--channels 1,2] [--output report.json]
                    switch   --device ENDPOINT_ID [--rates 44100,96000,192000,44100]
                             [--bits 16|24|32] [--container 16|24|32] [--channels 2] [--seconds 5]
                             [--output report.json]
                    fixtures --directory PATH [--rates 44100,48000,88200,96000,176400,192000]
                    decode   --input LOCAL_FILE_OR_DIRECTORY [--output report.json]
                    self-test
                    engine-fixtures --directory PATH [--seconds 8]
                    engine-extra-fixtures --input LOCAL_PCM_WAV --directory PATH
                    engine --input PATH [--device ENDPOINT_ID] --output report.json [--exercise true]
                    engine-lifecycle --input LOCAL_WAV --device ENDPOINT_ID --output report.json
                    http-engine --directory ENGINE_FIXTURES --device ENDPOINT_ID --output report.json
                    nas-inspect --library APP_LOCALSTATE [--name FOLDER] [--folder OBJECT_ID] --output report.json
                                [--play true --device ENDPOINT_ID] [--spread true --skip 0 --limit 2]
                    soak --directory ENGINE_FIXTURES --device ENDPOINT_ID --output report.json [--minutes 10]

                    matrix opens each exact exclusive format and releases it without starting playback.
                    switch streams SILENCE on the named endpoint; watch the DAC's rate display.
                    decode reads complete local audio files; it does not play them.
                    No automatic device fallback, sample rate conversion, or system setting changes.
                    Ctrl+C cancels between operations (or within a running silent stream).
                    """);
                return 0;
            }
            var command = args[0];
            if (command == "soak") return await SoakValidation.RunAsync(args);
            if (command is "http-engine" or "nas-inspect") return await NetworkValidation.RunAsync(args);
            if (command is "engine" or "engine-fixtures" or "engine-lifecycle" or "engine-extra-fixtures") return await EngineValidation.RunAsync(args);
            if (command == "self-test" && args.Length == 1) { PcmPacking.Verify(); return 0; }
            var allowed = command switch
            {
                "list" => new[] { "output" },
                "matrix" => ["device", "rates", "channels", "output"],
                "switch" => ["device", "rates", "bits", "container", "channels", "seconds", "output"],
                "fixtures" => ["directory", "rates"],
                "decode" => ["input", "output"],
                _ => throw new ArgumentException($"Unknown command: {command}")
            };
            var options = new Dictionary<string, string>();
            for (var i = 1; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--") || i + 1 >= args.Length || !allowed.Contains(args[i][2..])
                    || !options.TryAdd(args[i][2..], args[i + 1]))
                    throw new ArgumentException($"Invalid, duplicate or incomplete option: {args[i]}");
            }
            string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException($"--{key} is required.");
            int[] Numbers(string key, int[] defaults) => options.TryGetValue(key, out var value)
                ? value.Split(',').Select(int.Parse).ToArray() : defaults;
            var rates = Numbers("rates", command == "switch" ? [44100, 96000, 192000, 44100] : StandardRates);
            if (rates.Length == 0 || rates.Any(r => r < 8000 || r > 768000))
                throw new ArgumentException("Rates must be between 8000 and 768000 Hz.");
            var channels = Numbers("channels", command == "switch" ? [2] : [1, 2]);
            if (channels.Any(c => c is not (1 or 2))) throw new ArgumentException("Channels must be 1 or 2.");
            var seconds = Numbers("seconds", [5]).Single();
            if (seconds < 1 || seconds > 60) throw new ArgumentException("Seconds must be between 1 and 60.");
            var bits = Numbers("bits", [24]).Single();
            var container = Numbers("container", [bits]).Single();
            if (bits is not (16 or 24 or 32) || container != bits && !(bits == 24 && container == 32))
                throw new ArgumentException("Use PCM16/24/32, or 24 valid bits in a 32-bit container.");
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            if (command == "fixtures")
            {
                return await Fixtures.CreateAsync(Path.GetFullPath(Required("directory")), rates, cancellation.Token) == 0 ? 0 : 1;
            }

            var report = new ProbeReport(command);
            if (command == "decode")
            {
                var input = Path.GetFullPath(Required("input"));
                string[] extensions = [".wav", ".flac", ".m4a", ".aac", ".mp3", ".ogg", ".opus", ".mp4", ".m4v"];
                var files = Directory.Exists(input)
                    ? Directory.GetFiles(input).Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order().ToArray()
                    : File.Exists(input) ? [input] : throw new FileNotFoundException("Input not found.", input);
                if (files.Length == 0) throw new ArgumentException("No audio files found in input directory.");
                foreach (var file in files)
                foreach (var preservePrecision in new[] { false, true })
                {
                    if (cancellation.IsCancellationRequested) break;
                    var result = DecoderProbe.Run(file, preservePrecision, cancellation.Token);
                    report.Decodes.Add(result);
                    Console.Error.WriteLine($"{Path.GetFileName(file)} [{result.Request}]: {result.Status}, " +
                        $"{result.DecodedPcm?.SampleRate} Hz / {result.DecodedPcm?.ContainerBits} bit, {result.ReferenceWaveComparison} {result.Error}");
                }
            }
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                if (command == "list")
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        using (device) report.Devices.Add(DeviceInfo.Read(device));
                }
                else
                {
                    // Always resolve the exact saved ID. An absent DAC must never select another endpoint.
                    using var device = enumerator.GetDevice(Required("device"));
                    if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                        throw new InvalidOperationException("Selected render endpoint is not active.");
                    report.Devices.Add(DeviceInfo.Read(device));
                    var variants = command == "matrix"
                        ? new[] { (16, 16, false), (16, 16, true), (24, 24, false), (24, 24, true),
                            (24, 32, true), (32, 32, false), (32, 32, true) }
                        : [(bits, container, container != bits)];
                    foreach (var rate in rates)
                    foreach (var channel in channels)
                    foreach (var (valid, width, extensible) in variants)
                    {
                        if (cancellation.IsCancellationRequested) break;
                        Console.Error.WriteLine($"{device.FriendlyName}: {rate} Hz / {channel} ch / {valid}-in-{width} " +
                            $"{(extensible ? "extensible" : "PCM")} {(command == "switch" ? $"silent stream {seconds}s" : "initialize")}");
                        var result = ExclusiveProbe.Run(device, AudioFormat.Pcm(rate, channel, valid, width, extensible),
                            command == "switch" ? seconds : 0, cancellation.Token);
                        report.Outputs.Add(result);
                        Console.Error.WriteLine($"  {result.Status} {result.HResult} {result.Error}");
                    }
                }
            }
            report.Cancelled = cancellation.IsCancellationRequested;
            var json = JsonSerializer.Serialize(report, JsonOptions);
            if (options.TryGetValue("output", out var output))
            {
                var path = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json + Environment.NewLine);
                Console.WriteLine($"report: {path}");
            }
            else Console.WriteLine(json);
            return report.Cancelled ? 130 : report.Outputs.Any(r => r.Status == "failed")
                || report.Decodes.Any(r => r.Status == "failed") ? 1
                : command == "switch" && report.Outputs.Any(r => r.Status == "unsupported") ? 2 : 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception e) { Console.Error.WriteLine($"{e.GetType().Name}: {e.Message} (0x{e.HResult:X8})"); return 1; }
    }
}

internal sealed class ProbeReport(string command)
{
    public int SchemaVersion { get; } = 1;
    public string Command { get; } = command;
    public DateTimeOffset RecordedAt { get; } = DateTimeOffset.UtcNow;
    public string OS { get; } = RuntimeInformation.OSDescription;
    public string Architecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();
    public string Runtime { get; } = RuntimeInformation.FrameworkDescription;
    public string Library { get; } = "NAudio.Wasapi 2.4.0 + NAudio.Core 2.4.0";
    public string BufferSupply { get; } = "Dedicated console MTA, WASAPI exclusive event callback, prefilled silent buffers, IAudioClock position";
    public bool Cancelled { get; set; }
    public List<DeviceInfo> Devices { get; } = [];
    public List<OutputResult> Outputs { get; } = [];
    public List<DecodeResult> Decodes { get; } = [];
}

internal sealed record DeviceInfo(string Id, string Name, string InstanceId, AudioFormat SharedMixFormat)
{
    public static DeviceInfo Read(MMDevice device)
    {
        using var client = device.AudioClient;
        return new(device.ID, device.FriendlyName, device.InstanceId, AudioFormat.From(client.MixFormat));
    }
}
