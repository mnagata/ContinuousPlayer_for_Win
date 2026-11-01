using ContinuousPlayer.Core;

static class BrowserSelectionTests
{
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
    }

    public static IEnumerable<(string Name, Func<Task> Test)> Cases()
    {
        yield return ("Display names preserve periods, extensionless and unknown names", () =>
        {
            foreach (var (name, title) in new[] { ("作品.part.1.MP4", "作品.part.1"), ("作品", "作品"), ("作品.final", "作品.final") })
                Equal(title, MediaDisplayNames.Title(name));
            return Task.CompletedTask;
        });
        yield return ("Colliding media and folder titles gain format badges without changing identifiers or originals", () =>
        {
            var files = new[] { new MediaFile("wav", "作品.wav", "a"), new MediaFile("flac", "作品.FLAC", "b"),
                new MediaFile("unique", "別.mp4", "c"), new MediaFile("folder", "同名.mp4", "d") };
            var names = MediaDisplayNames.Create(files, ["同名"]);
            Equal("WAV", names[0].FormatBadge); Equal("FLAC", names[1].FormatBadge);
            Equal("", names[2].FormatBadge); Equal("MP4", names[3].FormatBadge);
            Equal("wav", names[0].Id); Equal("作品.wav", files[0].Name);
            return Task.CompletedTask;
        });
        yield return ("DLNA extensionless title collisions use known MIME or an explicit unknown format", () =>
        {
            var names = MediaDisplayNames.Create([new("a", "番組", "http://a", MimeType: "video/mp4"),
                new("b", "番組", "http://b", MimeType: "application/octet-stream")]);
            Equal("番組", names[0].Title); Equal("MP4", names[0].FormatBadge); Equal("形式不明", names[1].FormatBadge);
            return Task.CompletedTask;
        });
        yield return ("Local parents stop at the registered root and reject sibling-prefix escapes", () =>
        {
            var root = new SavedFolder("local", "Root", Path.Combine(Path.GetTempPath(), "stage4-root"));
            var child = Path.Combine(root.Path, "Child"); var deep = Path.Combine(child, "Deep");
            var parent = BrowserSelection.Parent("local", deep, null, root)!;
            Equal(child, parent.Path); Equal(deep, parent.SelectedId);
            Equal(root.Path, BrowserSelection.Parent("local", child, null, root)!.Path);
            Equal<BrowserParent?>(null, BrowserSelection.Parent("local", root.Path, null, root));
            Equal<BrowserParent?>(null, BrowserSelection.Parent("local", root.Path + "-other", null, root));
            return Task.CompletedTask;
        });
        yield return ("DLNA parents respect a deep registered root instead of the server root", () =>
        {
            var trail = new List<FolderNode> { new("0", "Server"), new("10", "Registered"), new("20", "Child"), new("30", "Deep") };
            var root = new SavedFolder("dlna", "Registered", "10", "server", Trail: trail.Take(2).ToList());
            var parent = BrowserSelection.Parent("dlna", "30", trail, root)!;
            Equal("20", parent.Path); Equal(3, parent.Trail!.Count); Equal("30", parent.SelectedId);
            Equal("10", BrowserSelection.Parent("dlna", "20", trail.Take(3).ToArray(), root)!.Path);
            Equal<BrowserParent?>(null, BrowserSelection.Parent("dlna", "10", trail, root));
            Equal<BrowserParent?>(null, BrowserSelection.Parent("dlna", "unknown", trail, root));
            return Task.CompletedTask;
        });
        yield return ("Removal selects the next neighbor, then previous, or Add when empty", () =>
        {
            Equal("b", BrowserSelection.SelectionAfterRemoval(["a", "b", "c"], 0));
            Equal("c", BrowserSelection.SelectionAfterRemoval(["a", "b", "c"], 1));
            Equal("b", BrowserSelection.SelectionAfterRemoval(["a", "b", "c"], 2));
            Equal("add", BrowserSelection.SelectionAfterRemoval(["a"], 0));
            Equal("add", BrowserSelection.SelectionAfterRemoval([], -1));
            return Task.CompletedTask;
        });
        yield return ("Canceled or superseded folder and picker completions cannot commit or end a newer load", async () =>
        {
            var state = new BrowserRequestState(); var old = state.Begin();
            var completion = new TaskCompletionSource<string>();
            async Task<string?> Deliver() { var value = await completion.Task; state.Complete(old); return state.IsCurrent(old) ? value : null; }
            var delayed = Deliver(); state.Cancel(); var current = state.Begin(); completion.SetResult("old picker path");
            Equal<string?>(null, await delayed); Equal(true, state.IsBusy);
            Equal(false, state.ShouldShowLoading(old)); Equal(true, state.ShouldShowLoading(current));
            state.Complete(current); Equal(false, state.ShouldShowLoading(current));
            state.Cancel(); Equal(false, state.IsCurrent(current));
        });
    }
}
