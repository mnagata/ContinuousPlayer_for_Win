using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ContinuousPlayer.Audio.Windows;
using ContinuousPlayer.Core;
using NAudio.Wave;

namespace ContinuousPlayer.AudioProbe;

// Opt-in real-device acceptance. Test sources are silence with one-LSB boundary markers.
internal static class SoakValidation
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--directory" or "--device" or "--output" or "--minutes"))
                throw new ArgumentException("Invalid soak option.");
            options.Add(args[i][2..], args[i + 1]);
        }
        var directory = Path.GetFullPath(options["directory"]);
        var output = Path.GetFullPath(options["output"]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var minutes = int.Parse(options.GetValueOrDefault("minutes", "10"));
        if (minutes is < 1 or > 60) throw new ArgumentException("Use 1..60 minutes per source.");
        var device = options["device"];
        var longFile = Path.Combine(Path.GetDirectoryName(output)!, "soak-44k.wav");
        using (var writer = new WaveFileWriter(longFile, new WaveFormat(44100, 16, 2)))
        {
            var block = new byte[44100 * 4];
            for (var second = 0; second < minutes * 60; second++)
            {
                Array.Clear(block);
                if (second == 0) block[0] = 1;
                if (second == minutes * 60 - 1) { block[^2] = 255; block[^1] = 255; }
                writer.Write(block, 0, block.Length);
            }
        }
        var files = Directory.GetFiles(directory).Where(x => Path.GetExtension(x) is ".wav" or ".flac" or ".m4a").Order().ToArray();
        if (files.Length != 4) throw new ArgumentException("Expected four engine fixtures.");
        using var server = new AudioHttpTestServer();
        server.Add("long", "long.wav", File.ReadAllBytes(longFile), "audio/wav");
        for (var i = 0; i < files.Length; i++) server.Add("f" + i, Path.GetFileName(files[i]), File.ReadAllBytes(files[i]),
            Path.GetExtension(files[i]) switch { ".wav" => "audio/wav", ".flac" => "audio/flac", _ => "audio/mp4" });
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var results = new List<object>();
        var samples = new List<object>();
        var started = DateTimeOffset.UtcNow;
        var overallStatus = "running";
        void Save() => File.WriteAllText(output, JsonSerializer.Serialize(new { SchemaVersion = 1, Started = started,
            Updated = DateTimeOffset.UtcNow, Status = overallStatus, MinutesPerSource = minutes, Device = device,
            server.MediaRequests,
            CpuIncludes = "renderer, decoder, diagnostic PCM hashing, loopback HTTP server and probe sampling",
            Results = results, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Play(string name, string reference, PlaybackSource source)
        {
            var traces = new ConcurrentQueue<AudioPlaybackTrace>();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new WasapiPlaybackEngine(device, trace => traces.Enqueue(trace));
            engine.Ended += (_, _) => ended.TrySetResult();
            engine.Failed += (_, e) => ended.TrySetException(new IOException($"{e.Message} (0x{e.Code:X8})"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(minutes + 2));
            var watch = Stopwatch.StartNew();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var initialCpu = process.TotalProcessorTime;
            var status = "passed"; string? error = null; string? comparison = null;
            var peakPrivate = process.PrivateMemorySize64;
            var peakWorking = process.WorkingSet64;
            var peakHandles = process.HandleCount;
            var peakThreads = process.Threads.Count;
            TimeSpan firstProgress = default, lastPosition = default;
            var progressAt = watch.Elapsed;
            try
            {
                await engine.PrepareAsync(source, timeout.Token);
                if (engine.Output.Format != null) throw new Exception("Preparation retained exclusive output.");
                engine.Play();
                while (!ended.Task.IsCompleted)
                {
                    await Task.WhenAny(ended.Task, Task.Delay(1000, timeout.Token));
                    timeout.Token.ThrowIfCancellationRequested();
                    var position = engine.Position;
                    if (position < lastPosition) throw new Exception("Playback clock regressed.");
                    if (position > lastPosition)
                    {
                        if (firstProgress == default) firstProgress = watch.Elapsed;
                        progressAt = watch.Elapsed;
                    }
                    else if (watch.Elapsed - progressAt > TimeSpan.FromSeconds(30)) throw new Exception("Playback clock stalled for 30 seconds.");
                    lastPosition = position;
                    process.Refresh();
                    peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                    peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                    peakHandles = Math.Max(peakHandles, process.HandleCount);
                    peakThreads = Math.Max(peakThreads, process.Threads.Count);
                    if ((int)watch.Elapsed.TotalSeconds % 10 == 0 || ended.Task.IsCompleted)
                    {
                        samples.Add(new { Name = name, Seconds = watch.Elapsed.TotalSeconds, PositionSeconds = position.TotalSeconds,
                            PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
                            ManagedBytes = GC.GetTotalMemory(false), Handles = process.HandleCount, Threads = process.Threads.Count });
                        Save();
                        Console.WriteLine($"{name}: {position.TotalSeconds:F1}s, private {process.PrivateMemorySize64 / 1048576.0:F1} MiB");
                    }
                }
                await ended.Task;
                var final = traces.Last(x => x.Event == "ended");
                if (final.ContentFrames <= 0 || final.PlayedFrames < final.ContentFrames) throw new Exception("End preceded output drain.");
                comparison = EngineValidation.CompareReference(reference, final);
                if (comparison is "PCM mismatch" or "no-reference") throw new Exception(comparison);
                if (engine.Output.Format != null) throw new Exception("End retained exclusive output.");
                await engine.DisposeAsync();
                // A second production engine must negotiate this exact DAC after release.
                await using var contender = new WasapiPlaybackEngine(device);
                await contender.PrepareAsync(new(reference), timeout.Token);
            }
            catch (Exception ex) { status = "failed"; error = ex.ToString(); }
            finally { await engine.DisposeAsync(); }
            process.Refresh();
            // One-core percentage: 100% means one logical CPU fully occupied.
            var cpuPercent = (process.TotalProcessorTime - initialCpu).TotalSeconds / watch.Elapsed.TotalSeconds * 100;
            results.Add(new { Name = name, Status = status, Error = error, Seconds = watch.Elapsed.TotalSeconds,
                FirstClockProgressSeconds = firstProgress.TotalSeconds, CpuOneCorePercent = cpuPercent,
                CpuMachinePercent = cpuPercent / Environment.ProcessorCount, LogicalProcessors = Environment.ProcessorCount,
                PeakPrivateBytes = peakPrivate, PeakWorkingSetBytes = peakWorking, PeakHandles = peakHandles,
                PeakThreads = peakThreads, FinalPrivateBytes = process.PrivateMemorySize64,
                FinalHandles = process.HandleCount, Comparison = comparison, Traces = traces.ToArray() });
            Save();
            Console.WriteLine($"{status}: {name}, CPU {cpuPercent:F2}% of one core, {comparison} {error}");
            if (status != "passed") throw new Exception($"Soak failed: {name}");
        }
        try
        {
            await Play("local-continuous", longFile, new(longFile));
            for (var i = 0; i < files.Length; i++) await Play("local-switch-" + i, files[i], new(files[i]));
            await Play("http-continuous", longFile, new(server.MediaUri("long").AbsoluteUri, "audio/wav"));
            for (var i = 0; i < files.Length; i++) await Play("http-switch-" + i, files[i],
                new(server.MediaUri("f" + i).AbsoluteUri, Path.GetExtension(files[i]) switch { ".wav" => "audio/wav", ".flac" => "audio/flac", _ => "audio/mp4" }));
            overallStatus = "passed"; Save();
            return 0;
        }
        catch (Exception ex) { overallStatus = "failed"; Save(); Console.Error.WriteLine(ex); return 1; }
    }
}
