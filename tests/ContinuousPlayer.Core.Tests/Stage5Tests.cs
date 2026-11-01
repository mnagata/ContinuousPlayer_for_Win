using ContinuousPlayer.Core;

static class Stage5Tests
{
    static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    public static IEnumerable<(string Name, Func<Task> Test)> Cases()
    {
        yield return ("Manual DLNA accepts host, IPv4, IPv6, explicit ports and complete description URLs", () =>
        {
            foreach (var (input, expected) in new[] { ("nas", "http://nas:50001/desc/device.xml"), (" 192.168.1.2:80 ", "http://192.168.1.2/desc/device.xml"),
                ("[::1]:51000", "http://[::1]:51000/desc/device.xml"), ("https://nas/device.xml?q=1", "https://nas/device.xml?q=1") })
                Equal(expected, DlnaAddress.Parse(input).AbsoluteUri);
            return Task.CompletedTask;
        });
        yield return ("Manual DLNA rejects unsupported schemes, credentials, fragments, bad ports and ambiguous roots", () =>
        {
            foreach (var input in new[] { "", "a b", "file:///c:/x", "ftp://nas/x", "http://user:pass@nas/x", "http://nas/x#part", "nas:0", "nas:65536", "nas:", "http:/nas", "http://nas/?token=secret" })
            {
                try { DlnaAddress.Parse(input); throw new Exception("Accepted " + input); } catch (FormatException) { }
            }
            return Task.CompletedTask;
        });
        yield return ("Direct DLNA reconnect uses a persisted description and reacquires current media URLs", async () =>
        {
            using var server = new Stage5TestServer(Guid.NewGuid().ToString("N"), ""); using var client = new DlnaClient();
            var connected = await client.ConnectAsync(server.DescriptionUri, default);
            var saved = new SavedFolder("dlna", "Root", "r", connected.Id, connected.Name, [new("0", "NAS"), new("r", "Root")], server.DescriptionUri.AbsoluteUri);
            var first = await client.BrowseAsync(connected, "r", default); server.Recover();
            var reopened = await client.ReconnectAsync(saved, default); var fresh = await client.BrowseAsync(reopened, "r", default);
            Equal(connected.Id, reopened.Id); Equal("a", fresh[0].Id);
            Equal(false, first[0].Media!.Location == fresh[0].Media!.Location); Equal(2, server.DescriptionRequests);
        });
        yield return ("Repointing cannot silently apply saved folder IDs to a different server", async () =>
        {
            using var server = new Stage5TestServer(Guid.NewGuid().ToString("N"), ""); using var client = new DlnaClient();
            var saved = new SavedFolder("dlna", "Root", "r", server.Udn, Trail: [new("r", "Root")], DescriptionUrl: server.DescriptionUri.AbsoluteUri);
            server.DifferentIdentity = true;
            try { await client.ReconnectAsync(saved, default); throw new Exception("Accepted another server"); } catch (IOException) { }
        });
        yield return ("Direct description cancellation and denied/non-DLNA responses remain explicit failures", async () =>
        {
            using var server = new Stage5TestServer(Guid.NewGuid().ToString("N"), ""); using var client = new DlnaClient();
            using var cancellation = new CancellationTokenSource(100);
            try { await client.ConnectAsync(server.SlowDescriptionUri, cancellation.Token); throw new Exception("Ignored cancellation"); } catch (OperationCanceledException) { }
            try { await client.ConnectAsync(new Uri(server.DescriptionUri, "/denied"), default); throw new Exception("Accepted denied"); }
            catch (HttpRequestException ex) { Equal(System.Net.HttpStatusCode.Forbidden, ex.StatusCode); }
            try { await client.ConnectAsync(new Uri(server.DescriptionUri, "/not-device"), default); throw new Exception("Accepted HTML"); } catch (InvalidDataException) { }
        });
        yield return ("Version one libraries without endpoint data survive upgrade and invalid endpoints retain registrations", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Stage5Library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "library.json"), """{"Version":1,"Folders":[{"Kind":"dlna","Name":"NAS","Path":"r","ServerId":"nas","Trail":[{"Id":"r","Name":"NAS"}]}]}""");
                var store = new LibraryStore(root); await store.LoadAsync(); Equal(1, store.Folders.Count); Equal<string?>(null, store.Folders[0].DescriptionUrl);
                var key = store.Folders[0].Key; store.UpdateServerEndpoint("nas", new Uri("http://nas:50001/device")); await store.SaveAsync();
                var restored = new LibraryStore(root); await restored.LoadAsync(); Equal(key, restored.Folders[0].Key); Equal("http://nas:50001/device", restored.Folders[0].DescriptionUrl);
                restored.Register(restored.Folders[0] with { DescriptionUrl = "file:///invalid" }); await restored.SaveAsync();
                var sanitized = new LibraryStore(root); await sanitized.LoadAsync(); Equal(1, sanitized.Folders.Count); Equal<string?>(null, sanitized.Folders[0].DescriptionUrl);
            }
            finally { Directory.Delete(root, true); }
        });
        yield return ("Recovery keeps confirmed backward seeks and repeated retry positions, but resets for a new selection", () =>
        {
            var recovery = new PlaybackRecovery(); recovery.Begin("a", false); recovery.Observe("a", TimeSpan.FromSeconds(40)); recovery.Observe("a", TimeSpan.FromSeconds(30));
            recovery.Begin("a", true); Equal(TimeSpan.FromSeconds(30), recovery.Position); recovery.Begin("a", true); Equal(TimeSpan.FromSeconds(30), recovery.Position);
            recovery.Observe("old", TimeSpan.Zero); Equal(TimeSpan.FromSeconds(30), recovery.Position);
            recovery.Begin("b", true); Equal(TimeSpan.Zero, recovery.Position); recovery.Observe("b", TimeSpan.FromSeconds(10));
            recovery.Begin("b", false); Equal(TimeSpan.Zero, recovery.Position); return Task.CompletedTask;
        });
        yield return ("Interrupted retry preparation cannot restore a prior playback intent", () =>
        {
            var state = new PlaybackSessionState(); state.Start(); state.Ready(); state.Fail(); state.RequestPlayback(true); state.BeginMedia();
            state.SetInterruption(PlaybackInterruption.Suspended, true); state.SetInterruption(PlaybackInterruption.Suspended, false); state.Ready();
            Equal(false, state.CanPlay); state.RequestPlayback(true); Equal(true, state.CanPlay); return Task.CompletedTask;
        });
        yield return ("URL refresh preserves queue identity and refuses another file", () =>
        {
            var queue = new PlaybackQueue(); queue.Start([new("a", "A", "http://old"), new("b", "B", "http://b")], 0);
            queue.RefreshCurrent(new("a", "A", "http://fresh")); Equal(0, queue.Index); Equal(2, queue.Items.Count); Equal("http://fresh", queue.Current!.Location);
            try { queue.RefreshCurrent(new("b", "B", "http://wrong")); throw new Exception("Changed identity"); } catch (InvalidOperationException) { }
            return Task.CompletedTask;
        });
        yield return ("Progress timeout bounds silent engine waits and excludes manual and temporary pauses", () =>
        {
            var progress = new PlaybackProgress();
            Equal(false, progress.TimedOut(TimeSpan.FromSeconds(30), TimeSpan.Zero, true));
            Equal(false, progress.TimedOut(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(29), true));
            Equal(true, progress.TimedOut(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), true));
            Equal(false, progress.TimedOut(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(31), true));
            Equal(false, progress.TimedOut(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(120), false));
            Equal(false, progress.TimedOut(TimeSpan.FromSeconds(31), TimeSpan.FromSeconds(200), true));
            progress.Reset(); Equal(false, progress.TimedOut(TimeSpan.Zero, TimeSpan.FromSeconds(400), true));
            return Task.CompletedTask;
        });
    }
}
