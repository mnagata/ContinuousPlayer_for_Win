using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using ContinuousPlayer.Audio.Windows;
using ContinuousPlayer.Core;
using NAudio.Wave;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ContinuousPlayer.AudioProbe;

internal static class EngineValidation
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Dictionary<string,string>();
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Incomplete engine option.");
            options.Add(args[i][2..], args[i+1]);
        }
        if (args[0] == "engine-fixtures")
        {
            await FixturesAsync(Path.GetFullPath(options["directory"]), options.TryGetValue("seconds", out var seconds) ? int.Parse(seconds) : 8);
            return 0;
        }
        if (args[0] == "engine-lifecycle") return await LifecycleAsync(options);
        if (args[0] == "engine-extra-fixtures")
        {
            var directory = Path.GetFullPath(options["directory"]); Directory.CreateDirectory(directory);
            foreach (var extension in new[] { ".mp3", ".aac" })
            {
                using var reader = new WaveFileReader(Path.GetFullPath(options["input"]));
                var file = Path.Combine(directory,"sample" + extension);
                if (extension == ".mp3") MediaFoundationEncoder.EncodeToMp3(reader,file,128000);
                else MediaFoundationEncoder.EncodeToAac(reader,file,128000);
                Console.WriteLine(file);
            }
            return 0;
        }
        var input = Path.GetFullPath(options["input"]);
        var files = Directory.Exists(input) ? Directory.GetFiles(input).Where(f => new[] {".wav",".flac",".m4a",".mp3",".aac",".ogg",".opus"}.Contains(Path.GetExtension(f))).Order().ToArray() : [input];
        var exercise = options.GetValueOrDefault("exercise") == "true";
        var result = new List<object>();
        foreach (var file in files)
        {
            var traces = new ConcurrentQueue<AudioPlaybackTrace>();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var engine = new WasapiPlaybackEngine(options.GetValueOrDefault("device"), entry =>
            {
                traces.Enqueue(entry);
                if (entry.Event is "initialized" or "playing" or "failed" or "ended")
                    Console.WriteLine($"{Path.GetFileName(file)} {entry.Event}: {entry.Output.Format} {entry.Detail}");
            });
            engine.Opened += (_,_) => opened.TrySetResult();
            engine.Ended += (_,_) => ended.TrySetResult();
            engine.SeekCompleted += (_,_) => seeked.TrySetResult();
            engine.Failed += (_,e) => { var failure = new IOException(e.Message); opened.TrySetException(failure); ended.TrySetException(failure); seeked.TrySetException(failure); };
            var status = "passed";
            string? error = null, comparison = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await engine.PrepareAsync(new(file), timeout.Token);
                await opened.Task.WaitAsync(timeout.Token);
                if (engine.Output.Format != null) throw new Exception("Prepared engine still reserves the endpoint.");
                engine.Play();
                if (exercise)
                {
                    await UntilAsync(() => engine.Position > TimeSpan.FromMilliseconds(300), timeout.Token);
                    engine.Pause();
                    await UntilAsync(() => engine.State == PlaybackEngineState.Paused && engine.Output.Format == null, timeout.Token);
                    var frozen = engine.Position;
                    await Task.Delay(250, timeout.Token);
                    if (engine.Position != frozen) throw new Exception("Paused position advanced.");
                    engine.Seek(TimeSpan.FromTicks(engine.Duration.Ticks / 2));
                    await seeked.Task.WaitAsync(timeout.Token);
                    if (engine.State != PlaybackEngineState.Paused || engine.Output.Format != null)
                        throw new Exception("Paused seek acquired output or resumed playback.");
                    engine.Play();
                }
                await ended.Task.WaitAsync(timeout.Token);
                var final = traces.Last(x => x.Event == "ended");
                if (final.PlayedFrames < final.ContentFrames) throw new Exception("Ended before all content frames were played.");
                if (final.ContentFrames <= 0) throw new Exception("No PCM content was played.");
                comparison = exercise ? "seek-run (full-file comparison not applicable)" : CompareReference(file, final);
                if (comparison == "PCM mismatch") throw new Exception("Submitted PCM differs from reference.");
            }
            catch (Exception e) { status = "failed"; error = e.ToString(); }
            finally { await engine.DisposeAsync(); }
            result.Add(new { File = file, Status = status, Error = error, Comparison = comparison, Traces = traces.ToArray() });
            Console.WriteLine($"{status}: {Path.GetFileName(file)} {comparison} {error}");
        }
        var output = Path.GetFullPath(options["output"]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { SchemaVersion=1, Exercise=exercise, Results=result },new JsonSerializerOptions{WriteIndented=true}));
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        return doc.RootElement.GetProperty("Results").EnumerateArray().Any(x => x.GetProperty("Status").GetString() == "failed") ? 1 : 0;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    private static async Task<int> LifecycleAsync(Dictionary<string,string> options)
    {
        var input = Path.GetFullPath(options["input"]);
        var device = options["device"];
        var results = new List<object>();
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); results.Add(new { Name=name, Status="passed", Error=(string?)null }); Console.WriteLine($"passed: {name}"); }
            catch (Exception e) { results.Add(new { Name=name, Status="failed", Error=e.ToString() }); Console.WriteLine($"failed: {name}: {e.Message}"); }
        }
        async Task Reject(string file, string endpoint, string expected)
        {
            var engine = new WasapiPlaybackEngine(endpoint);
            try
            {
                try { await engine.PrepareAsync(new(file), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception e) when (e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
                catch (COMException) when (expected == "missing-device") { return; }
                throw new Exception("The unsupported source/device was not rejected as expected.");
            }
            finally { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        await Check("Missing selected endpoint fails without default fallback", () => Reject(input,"{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}","missing-device"));
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options["output"]))!,"negative");
        Directory.CreateDirectory(directory);
        var mono = Path.Combine(directory,"mono.wav");
        using (var writer = new WaveFileWriter(mono,new WaveFormat(44100,16,1))) writer.Write(new byte[44100*2],0,44100*2);
        var floating = Path.Combine(directory,"float.wav");
        using (var writer = new WaveFileWriter(floating,WaveFormat.CreateIeeeFloatWaveFormat(44100,2))) writer.Write(new byte[44100*8],0,44100*8);
        await Check("BTR17 unsupported mono is rejected without upmix", () => Reject(mono,device,"精度を保持"));
        await Check("Float WAV is rejected without integer quantization", () => Reject(floating,device,"浮動小数点"));
        await Check("Cancellation before prepare releases immediately", async () =>
        {
            var engine = new WasapiPlaybackEngine(device);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await engine.PrepareAsync(new(input),cancellation.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            finally { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        });
        await Check("Cancellation during prepare releases before following initialization", async () =>
        {
            var engine = new WasapiPlaybackEngine(device);
            using var cancellation = new CancellationTokenSource();
            var preparing = engine.PrepareAsync(new(input),cancellation.Token);
            cancellation.Cancel();
            try { await preparing; throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            finally { await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            await using var following = new WasapiPlaybackEngine(device);
            await following.PrepareAsync(new(input),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        });
        await Check("Rapid real engine replacement cancels stale work; no-log playback pauses and frees endpoint", async () =>
        {
            using var session = new PlaybackEngineSession();
            var pending = new List<Task>();
            for (var i=0;i<12;i++)
            {
                session.Replace(new WasapiPlaybackEngine(device));
                pending.Add(session.PrepareAsync(new(input),CancellationToken.None));
            }
            foreach (var task in pending)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { }
            }
            var current = session.Engine!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            current.Play(); await UntilAsync(() => current.Position > TimeSpan.FromMilliseconds(300),timeout.Token);
            current.Pause(); await UntilAsync(() => current.State == PlaybackEngineState.Paused && current.Output.Format == null,timeout.Token);
            var position = current.Position; await Task.Delay(250,timeout.Token);
            if (current.Position != position) throw new Exception("Paused clock advanced.");
            await using (var competitor = new WasapiPlaybackEngine(device))
                await competitor.PrepareAsync(new(input),timeout.Token);
            session.Replace(null);
            await current.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        });
        if (options.TryGetValue("video",out var video))
            await Check("Native inspection detects video inside an M4A path", async () =>
            {
                var disguised = Path.Combine(directory,"video.m4a"); File.Copy(Path.GetFullPath(video),disguised,true);
                if (!(await LocalAudioInspector.InspectAsync(disguised,CancellationToken.None)).HasVideo)
                    throw new Exception("Video was not detected.");
            });
        var output = Path.GetFullPath(options["output"]);
        File.WriteAllText(output,JsonSerializer.Serialize(new { SchemaVersion=1,Results=results },new JsonSerializerOptions{WriteIndented=true}));
        using var doc = JsonDocument.Parse(File.ReadAllText(output));
        return doc.RootElement.GetProperty("Results").EnumerateArray().Any(x => x.GetProperty("Status").GetString() == "failed") ? 1 : 0;
    }

    internal static string CompareReference(string file, AudioPlaybackTrace trace)
    {
        if (Path.GetExtension(file) is ".m4a" or ".aac" or ".mp3" or ".ogg" or ".opus") return "compressed audio (lossless sample comparison not applicable)";
        var reference = Path.GetExtension(file) == ".wav" ? file : Path.Combine(Path.GetDirectoryName(file)!, "references", Path.GetFileNameWithoutExtension(file) + ".wav");
        if (!File.Exists(reference)) return "no-reference";
        using var reader = new WaveFileReader(reference);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var data = new byte[reader.WaveFormat.BlockAlign * 4096];
        int count; long frames = 0;
        while ((count = reader.Read(data,0,data.Length)) != 0)
        {
            hash.AppendData(ExclusiveAudioPolicy.Widen(data.AsSpan(0,count),reader.WaveFormat.BitsPerSample,trace.Output.Format!.ContainerBits));
            frames += count / reader.WaveFormat.BlockAlign;
        }
        return frames == trace.ContentFrames && Convert.ToHexString(hash.GetHashAndReset()) == trace.PcmSha256 ? "exact submitted PCM match" : "PCM mismatch";
    }

    private static async Task FixturesAsync(string directory, int seconds)
    {
        if (seconds < 2 || seconds > 60) throw new ArgumentException("Fixture seconds must be 2..60.");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory,"references"));
        var profiles = new[] { ("01 44.1k",44100,16,".wav"), ("02 96k",96000,24,".flac"),
            ("03 192k",192000,24,".wav"), ("04 44.1k",44100,16,".m4a") };
        foreach (var (name,rate,bits,extension) in profiles)
        {
            var wavePath = Path.Combine(extension == ".wav" ? directory : Path.Combine(directory,"references"),name+".wav");
            using (var writer = new WaveFileWriter(wavePath,new WaveFormat(rate,bits,2)))
            {
                var data = new byte[rate*seconds*2*bits/8];
                // Almost silent; ±1-LSB markers at both ends verify that submission retains the tail.
                data[0] = 1; data[^1] = 255; data[^2] = 255;
                if (bits == 24) data[^3] = 255;
                writer.Write(data,0,data.Length);
            }
            if (extension != ".wav")
            {
                var profile = extension == ".flac" ? MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High) : MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High);
                profile.Audio.SampleRate = (uint)rate; profile.Audio.ChannelCount = 2;
                if (extension == ".flac") profile.Audio.BitsPerSample = (uint)bits;
                var input = await StorageFile.GetFileFromPathAsync(wavePath);
                var folder = await StorageFolder.GetFolderFromPathAsync(directory);
                var destination = await folder.CreateFileAsync(name+extension,CreationCollisionOption.ReplaceExisting);
                var transcode = await new MediaTranscoder().PrepareFileTranscodeAsync(input,destination,profile);
                if (!transcode.CanTranscode) throw new Exception(transcode.FailureReason.ToString());
                await transcode.TranscodeAsync();
            }
            Console.WriteLine(Path.Combine(directory,name+extension));
        }
    }
}
