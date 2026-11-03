using ContinuousPlayer.Core;

internal static class HttpAudioTests
{
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T a,T b) { if (!EqualityComparer<T>.Default.Equals(a,b)) throw new Exception($"Expected {a}, got {b}"); }
    public static IEnumerable<(string,Func<Task>)> Cases()
    {
        yield return ("HTTP audio routing uses DLNA MIME/title rather than URL extension; video/credentials stay excluded", () =>
        {
            var options = new AudioPlaybackOptions(true);
            True(options.ProbesAudio(new("a","opaque","http://nas/media?id=42",MimeType:"audio/flac")));
            True(options.ProbesAudio(new("a","song.MP3","http://nas/media?id=42")));
            True(!options.ProbesAudio(new("a","song.wav","http://nas/media",MimeType:"video/mp4")));
            True(!options.ProbesAudio(new("a","song.wav","http://user:pass@nas/media")));
            True(!new AudioPlaybackOptions().ProbesAudio(new("a","song.wav","http://nas/media")));
            return Task.CompletedTask;
        });
        yield return ("HTTP ranges return exact seek bytes, bounded cache and only real EOF", () =>
        {
            using var server = new AudioHttpTestServer();
            var bytes = Enumerable.Range(0,HttpAudioStream.BlockBytes*35+17).Select(x => (byte)(x*31)).ToArray();
            server.Add("a","source.wav",bytes,"audio/wav");
            using var stream = new HttpAudioStream(server.MediaUri("a"),default);
            Equal((long)bytes.Length,stream.Length);
            foreach (var offset in Enumerable.Range(0,35).Select(x => (long)x*HttpAudioStream.BlockBytes+3).Append(7))
            {
                stream.Seek(offset,SeekOrigin.Begin);var data=new byte[31];Equal(31,stream.Read(data));
                True(data.SequenceEqual(bytes.AsSpan((int)offset,31).ToArray()));
                True(stream.CachedBytes <= (long)HttpAudioStream.BlockBytes*HttpAudioStream.CachedBlocks);
            }
            stream.Seek(-2,SeekOrigin.End);var end=new byte[8];Equal(2,stream.Read(end));Equal(0,stream.Read(end));
            return Task.CompletedTask;
        });
        yield return ("Ignoring or corrupting a range is rejected without downloading the whole resource", () =>
        {
            using var server=new AudioHttpTestServer();server.Add("a","a.wav",new byte[200000],"audio/wav");
            server.IgnoreRange=true;Reject(() => new HttpAudioStream(server.MediaUri("a"),default),AudioNetworkFailure.RangeRequired);
            server.IgnoreRange=false;server.WrongRange=true;Reject(() => new HttpAudioStream(server.MediaUri("a"),default),AudioNetworkFailure.RangeRequired);
            return Task.CompletedTask;
        });
        yield return ("Truncated responses and resource changes fail rather than becoming EOF or mixed PCM", () =>
        {
            using var server=new AudioHttpTestServer();server.Add("a","a.wav",new byte[HttpAudioStream.BlockBytes*3],"audio/wav");
            server.TruncateAfterBytes=50;
            try { using var failed=new HttpAudioStream(server.MediaUri("a"),default);throw new Exception("Accepted truncation."); }
            catch(AudioNetworkException) { }
            server.TruncateAfterBytes=0;
            using var stream=new HttpAudioStream(server.MediaUri("a"),default);server.ChangeTag=true;
            Reject(() => { stream.Position=HttpAudioStream.BlockBytes;stream.ReadByte(); },AudioNetworkFailure.ResourceChanged);
            return Task.CompletedTask;
        });
        yield return ("HTTP deadline and cancellation bound stalled headers", () =>
        {
            using var server=new AudioHttpTestServer();server.Add("a","a.wav",new byte[200000],"audio/wav");server.HeaderDelayMilliseconds=1000;
            Reject(() => new HttpAudioStream(server.MediaUri("a"),default,TimeSpan.FromMilliseconds(100)),AudioNetworkFailure.Timeout);
            using var stop=new CancellationTokenSource(100);
            try { using var failed=new HttpAudioStream(server.MediaUri("a"),stop.Token);throw new Exception("Ignored cancellation."); }
            catch(OperationCanceledException) { }
            return Task.CompletedTask;
        });
        yield return ("DLNA retries reacquire signed URLs and preserve conversion flag without trusting sampleFrequency", async () =>
        {
            using var server=new AudioHttpTestServer();server.Add("a","Original 192k.wav",new byte[200000],"audio/flac",true);
            using var dlna=new DlnaClient();var device=await dlna.ConnectAsync(server.DescriptionUri,default);
            var saved=new SavedFolder("dlna","Audio","r",device.Id,device.Name,[new("0","NAS"),new("r","Audio")],server.DescriptionUri.AbsoluteUri);
            var first=(await dlna.BrowseAsync(device,"r",default)).Single().Media!;
            True(first.IsTranscoded);Equal("audio/flac",first.MimeType);
            server.Recover();var refreshed=await dlna.ReconnectAsync(saved,default);
            var fresh=(await dlna.BrowseAsync(refreshed,"r",default)).Single().Media!;
            Equal(first.Id,fresh.Id);True(first.Location != fresh.Location);
            Reject(() => new HttpAudioStream(new Uri(first.Location),default),AudioNetworkFailure.RequestFailed);
            using var readable=new HttpAudioStream(new Uri(fresh.Location),default);Equal(0,readable.ReadByte());
        });
    }
    private static void Reject(Action action,AudioNetworkFailure expected)
    {
        try { action();throw new Exception("Expected a transport failure."); }
        catch(AudioNetworkException e) { Equal(expected,e.Failure); }
    }
}
