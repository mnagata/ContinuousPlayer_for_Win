using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ContinuousPlayer.Audio.Windows;
using ContinuousPlayer.Core;

namespace ContinuousPlayer.AudioProbe;

internal static class NetworkValidation
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options=new Dictionary<string,string>();
        for(var n=1;n<args.Length;n+=2) options.Add(args[n][2..],args[n+1]);
        if(args[0]=="nas-inspect") return await NasAsync(options);
        using var server=new AudioHttpTestServer();
        var directory=Path.GetFullPath(options["directory"]);
        var files=new[] { ("a","01 44.1k.wav","audio/wav"), ("b","02 96k.flac","audio/flac"), ("c","03 192k.wav","audio/wav"), ("d","04 44.1k.m4a","audio/mp4") };
        foreach(var (id,name,mime) in files) server.Add(id,name,File.ReadAllBytes(Path.Combine(directory,name)),mime,id=="b");
        using var dlna=new DlnaClient();
        var connected=await dlna.ConnectAsync(server.DescriptionUri,default);
        var saved=new SavedFolder("dlna","Audio","r",connected.Id,connected.Name,[new("0","NAS"),new("r","Audio")],server.DescriptionUri.AbsoluteUri);
        var all=new ConcurrentQueue<AudioPlaybackTrace>();var results=new List<object>();
        async Task Check(string name,Func<Task> test)
        {
            try { await test(); results.Add(new {Name=name,Status="passed",Error=(string?)null});Console.WriteLine("passed: "+name); }
            catch(Exception e) { results.Add(new {Name=name,Status="failed",Error=e.ToString()});Console.WriteLine("failed: "+name+" "+e.Message); }
        }
        WasapiPlaybackEngine Engine() => new(options.GetValueOrDefault("device"),entry=>
        {
            all.Enqueue(entry);
            if(entry.Event is "decoded-format" or "initialized" or "failed" or "ended") Console.WriteLine(entry.Event+": "+entry.Decoded+" -> "+entry.Output.Format+" "+entry.Detail);
        });
        async Task<List<MediaFile>> Fresh()
        {
            var device=await dlna.ReconnectAsync(saved,default);
            return (await dlna.BrowseAsync(device,"r",default)).Select(x=>x.Media!).ToList();
        }
        foreach(var (id,name,_) in files)
            await Check("DLNA full playback "+name,async()=>
            {
                var file=(await Fresh()).Single(x=>x.Id==id);
                var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var error=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var engine=Engine();
                engine.Ended+=(_,_)=>ended.TrySetResult();engine.Failed+=(_,e)=>error.TrySetResult(new IOException(e.Message));
                await engine.PrepareAsync(new(file.Location,file.MimeType),default).WaitAsync(TimeSpan.FromSeconds(20));
                if(id=="b" && (!file.IsTranscoded || engine.DecodedPcm!.SampleRate!=96000)) throw new Exception("Trusted original metadata instead of converted resource.");
                engine.Play();await Completion(ended.Task,error.Task,30);
                var final=all.Last(x=>x.Event=="ended" && x.File==file.Location);
                if(final.PlayedFrames<final.ContentFrames || final.ContentFrames==0) throw new Exception("Ended before drain.");
                if(id!="d" && EngineValidation.CompareReference(Path.Combine(directory,name),final)!="exact submitted PCM match") throw new Exception("HTTP submitted PCM mismatch.");
            });
        await Check("HTTP pause, paused seek, release and resume",async()=>
        {
            var file=(await Fresh()).First(x=>x.Id=="b");
            await using var engine=Engine();
            var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seek=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var error=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Ended+=(_,_)=>ended.TrySetResult();engine.SeekCompleted+=(_,_)=>seek.TrySetResult();engine.Failed+=(_,e)=>error.TrySetResult(new IOException(e.Message));
            await engine.PrepareAsync(new(file.Location,file.MimeType),default);engine.Play();
            await Until(()=>engine.Position>TimeSpan.FromMilliseconds(300));engine.Pause();
            await Until(()=>engine.State==PlaybackEngineState.Paused && engine.Output.Format==null);
            engine.Seek(engine.Duration/2);await Completion(seek.Task,error.Task,15);
            if(engine.State!=PlaybackEngineState.Paused || engine.Output.Format!=null) throw new Exception("Paused seek resumed or held endpoint.");
            engine.Play();await Completion(ended.Task,error.Task,20);
        });
        await Check("Playing HTTP seek reports buffering rather than an external pause",async()=>
        {
            var file=(await Fresh()).Single(x=>x.Id=="a");await using var engine=Engine();
            var seek=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var states=new ConcurrentQueue<PlaybackEngineState>();
            engine.SeekCompleted+=(_,_)=>seek.TrySetResult();
            await engine.PrepareAsync(new(file.Location,file.MimeType),default);engine.Play();await Until(()=>engine.Position>TimeSpan.FromMilliseconds(300));
            engine.StateChanged+=(_,_)=>states.Enqueue(engine.State);
            var target=engine.Duration/2;engine.Seek(target);await seek.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Until(()=>engine.State==PlaybackEngineState.Playing && engine.Position>target+TimeSpan.FromMilliseconds(200));
            if(states.Contains(PlaybackEngineState.Paused)) throw new Exception("Internal seek looked like an external pause.");
        });
        await Check("Canceled stalled HTTP preparation never initializes output",async()=>
        {
            server.HeaderDelayMilliseconds=2000;
            var file=(await Fresh()).First();
            var before=all.Count(x=>x.Event=="initialized");var watch=Stopwatch.StartNew();
            await using var engine=Engine();using var cancellation=new CancellationTokenSource(100);
            try { await engine.PrepareAsync(new(file.Location,file.MimeType),cancellation.Token);throw new Exception("Ignored cancellation."); }
            catch(OperationCanceledException) { }
            finally { server.HeaderDelayMilliseconds=0;await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
            if(watch.Elapsed>TimeSpan.FromSeconds(3) || before!=all.Count(x=>x.Event=="initialized")) throw new Exception("Late/canceled preparation reserved the DAC.");
        });
        await Check("No-range HTTP is rejected without fallback",async()=>
        {
            server.IgnoreRange=true;await using var engine=Engine();var file=(await Fresh()).First();
            try { await engine.PrepareAsync(new(file.Location,file.MimeType),default);throw new Exception("Accepted no-range server."); }
            catch(AudioNetworkException e) when(e.Failure==AudioNetworkFailure.RangeRequired) { }
            finally { server.IgnoreRange=false; }
        });
        await Check("Delayed HTTP preparation stays paused and releases the endpoint",async()=>
        {
            server.HeaderDelayMilliseconds=100;
            await using var engine=Engine();var file=(await Fresh()).First();
            try
            {
                await engine.PrepareAsync(new(file.Location,file.MimeType),default).WaitAsync(TimeSpan.FromSeconds(15));
                if(engine.State!=PlaybackEngineState.Paused || engine.Output.Format!=null) throw new Exception("Preparation played or held the endpoint.");
            }
            finally { server.HeaderDelayMilliseconds=0; }
        });
        await Check("HTTP disconnect is not EOF; retry refreshes URL and restores confirmed position",async()=>
        {
            using(var wave=new NAudio.Wave.WaveFileReader(Path.Combine(directory,"01 44.1k.wav")))
            {
                using var memory=new MemoryStream();
                using(var writer=new NAudio.Wave.WaveFileWriter(memory,wave.WaveFormat))
                {
                    var pcm=new byte[(int)wave.Length];wave.ReadExactly(pcm);
                    for(var n=0;n<15;n++) writer.Write(pcm,0,pcm.Length);
                }
                server.Add("z","Long 44.1k.wav",memory.ToArray(),"audio/wav");
            }
            var first=(await Fresh()).Single(x=>x.Id=="z");
            var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var natural=false;TimeSpan confirmed;
            await using(var old=Engine())
            {
                old.Failed+=(_,_)=>failed.TrySetResult();old.Ended+=(_,_)=>natural=true;
                await old.PrepareAsync(new(first.Location,first.MimeType),default);old.Play();
                await Until(()=>old.Position>TimeSpan.FromMilliseconds(300));server.Disconnect();
                await failed.Task.WaitAsync(TimeSpan.FromSeconds(20));confirmed=old.Position;
                if(natural || confirmed<=TimeSpan.Zero) throw new Exception("Disconnect was EOF or lost confirmed position.");
            }
            server.Recover();var fresh=(await Fresh()).Single(x=>x.Id=="z");
            if(first.Location==fresh.Location) throw new Exception("URL was not renewed.");
            await using var retry=Engine();var seek=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            retry.SeekCompleted+=(_,_)=>seek.TrySetResult();
            await retry.PrepareAsync(new(fresh.Location,fresh.MimeType),default);retry.Seek(confirmed);
            await seek.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if(Math.Abs((retry.Position-confirmed).TotalMilliseconds)>1 || retry.Output.Format!=null) throw new Exception("Position restoration did not remain paused.");
            retry.Play();await Until(()=>retry.Position>confirmed+TimeSpan.FromMilliseconds(300));retry.Pause();
            await Until(()=>retry.State==PlaybackEngineState.Paused && retry.Output.Format==null);
        });
        var output=Path.GetFullPath(options["output"]);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,JsonSerializer.Serialize(new {SchemaVersion=1,Results=results,Traces=all.ToArray(),HttpRequests=server.Requests.ToArray(),server.BrowseRequests},new JsonSerializerOptions{WriteIndented=true}));
        using var doc=JsonDocument.Parse(File.ReadAllText(output));
        return doc.RootElement.GetProperty("Results").EnumerateArray().Any(x=>x.GetProperty("Status").GetString()=="failed")?1:0;
    }
    private static async Task Completion(Task success,Task<Exception> failure,int seconds)
    {
        var completed=await Task.WhenAny(success,failure).WaitAsync(TimeSpan.FromSeconds(seconds));
        if(completed==failure) throw await failure;
        await success;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while(!condition()) await Task.Delay(10,timeout.Token);
    }
    private static async Task<int> NasAsync(Dictionary<string,string> options)
    {
        var library=new LibraryStore(Path.GetFullPath(options["library"]));await library.LoadAsync();
        using var dlna=new DlnaClient();var results=new List<object>();
        foreach(var folder in library.Folders.Where(x=>x.Kind=="dlna" && (!options.TryGetValue("name",out var name) || x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var server=await dlna.ReconnectAsync(folder,default);var entries=await dlna.BrowseAsync(server,options.GetValueOrDefault("folder")??folder.Path,default);
                if(options.ContainsKey("play"))
                {
                    var candidates=new Queue<(string Id,int Depth)>();
                    foreach(var entry in entries.Where(x=>x.Media==null)) candidates.Enqueue((entry.Id,1));
                    var audio=entries.Where(x=>x.Media!=null && new AudioPlaybackOptions(true).ProbesAudio(x.Media)).Select(x=>x.Media!).ToList();
                    var visits=0;
                    var spread=options.ContainsKey("spread");
                    while((spread || audio.Count<2) && candidates.Count>0 && visits++<20)
                    {
                        var next=candidates.Dequeue();var children=await dlna.BrowseAsync(server,next.Id,default);
                        var found=children.Where(x=>x.Media!=null && new AudioPlaybackOptions(true).ProbesAudio(x.Media)).Select(x=>x.Media!);
                        audio.AddRange(spread ? found.Take(1) : found);
                        if(next.Depth<4) foreach(var child in children.Where(x=>x.Media==null)) candidates.Enqueue((child.Id,next.Depth+1));
                    }
                    if(audio.Count==0) throw new IOException("指定フォルダー内に検証できる音声が見つかりません。");
                    var evidence=new List<object>();
                    foreach(var file in audio.Skip(options.TryGetValue("skip",out var skip)?int.Parse(skip):0).Take(options.TryGetValue("limit",out var limit)?int.Parse(limit):spread?6:2))
                    {
                        var traces=new ConcurrentQueue<AudioPlaybackTrace>();
                        var tracePath=Path.ChangeExtension(Path.GetFullPath(options["output"]),"trace.jsonl");
                        Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
                        await using var engine=new WasapiPlaybackEngine(options.GetValueOrDefault("device"),entry=>
                        {
                            traces.Enqueue(entry);File.AppendAllText(tracePath,JsonSerializer.Serialize(entry)+Environment.NewLine);
                            if(entry.Event is "decoded-format" or "failed" or "seek-completed") Console.WriteLine("NAS "+entry.Event+": "+entry.Decoded+" "+entry.Detail);
                        });
                        var failure=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
                        engine.Failed+=(_,e)=>failure.TrySetResult(new IOException(e.Message));
                        await engine.PrepareAsync(new(file.Location,file.MimeType),default).WaitAsync(TimeSpan.FromSeconds(30));
                        engine.Play();await Until(()=>engine.Position>TimeSpan.FromSeconds(1) || failure.Task.IsCompleted);
                        if(failure.Task.IsCompleted) throw await failure.Task;
                        engine.Pause();await Until(()=>engine.State==PlaybackEngineState.Paused && engine.Output.Format==null);
                        var target=engine.Duration/2;var seek=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        engine.SeekCompleted+=(_,_)=>seek.TrySetResult();engine.Seek(target);
                        await Completion(seek.Task,failure.Task,30);
                        if(engine.Output.Format!=null || Math.Abs((engine.Position-target).TotalSeconds)>0.1) throw new Exception("NAS paused seek failed.");
                        engine.Play();await Until(()=>engine.Position>target+TimeSpan.FromSeconds(1) || failure.Task.IsCompleted);
                        if(failure.Task.IsCompleted) throw await failure.Task;
                        engine.Pause();await Until(()=>engine.State==PlaybackEngineState.Paused && engine.Output.Format==null);
                        evidence.Add(new {file.Id,file.Name,file.MimeType,engine.Audio,engine.DecodedPcm,Traces=traces.ToArray(),Status="passed"});
                        Console.WriteLine($"NAS playback/seek passed: {engine.DecodedPcm}");
                    }
                    var evidencePath=Path.ChangeExtension(Path.GetFullPath(options["output"]),"playback.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);File.WriteAllText(evidencePath,JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
                }
                results.Add(new {folder.Name,Status="connected",Server=server,Entries=entries,Error=(string?)null});
                Console.WriteLine($"connected: {folder.Name}; entries={entries.Count}; audio={entries.Count(x=>x.Media!=null && new AudioPlaybackOptions(true).ProbesAudio(x.Media))}");
            }
            catch(Exception e) { results.Add(new {folder.Name,Status="failed",Server=(DlnaServer?)null,Entries=(List<DlnaEntry>?)null,Error=e.Message});Console.WriteLine(folder.Name+": "+e.Message); }
        }
        var path=Path.GetFullPath(options["output"]);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
}
