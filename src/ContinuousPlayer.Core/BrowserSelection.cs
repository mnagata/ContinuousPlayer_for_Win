namespace ContinuousPlayer.Core;

public sealed record MediaDisplayName(string Id, string Title, string FormatBadge);

public static class MediaDisplayNames
{
    public static string Title(string name) => MediaCatalog.IsSupported(name)
        ? name[..^Path.GetExtension(name).Length] : name;

    public static IReadOnlyList<MediaDisplayName> Create(IEnumerable<MediaFile> media, IEnumerable<string>? folderNames = null)
    {
        var files = media.ToArray();
        var collisions = files.Select(x => Title(x.Name)).Concat(folderNames ?? [])
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Select(x => new MediaDisplayName(x.Id, Title(x.Name),
            collisions.Contains(Title(x.Name)) ? Format(x) : "")).ToArray();
    }

    private static string Format(MediaFile file) => MediaCatalog.IsSupported(file.Name)
        ? Path.GetExtension(file.Name).TrimStart('.').ToUpperInvariant()
        : file.MimeType?.ToLowerInvariant() switch
        {
            "audio/flac" or "audio/x-flac" => "FLAC", "audio/mpeg" => "MP3",
            "audio/wav" or "audio/x-wav" => "WAV", "video/mp4" => "MP4",
            "audio/mp4" => "M4A", "audio/aac" => "AAC", "audio/ogg" => "OGG",
            "audio/opus" => "OPUS", _ => "形式不明"
        };
}

public sealed record BrowserParent(string Path, string Name, IReadOnlyList<FolderNode>? Trail, string SelectedId);

public static class BrowserSelection
{
    // Null means the saved-folders list, never a folder above the registered root.
    public static BrowserParent? Parent(string kind, string path, IReadOnlyList<FolderNode>? trail, SavedFolder? root)
    {
        if (kind == "local")
        {
            if (root == null) return null;
            var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var boundary = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase) ||
                !current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var parent = Directory.GetParent(path);
            return parent == null ? null : new(parent.FullName, parent.Name, null, path);
        }
        if (kind != "dlna" || root == null || path == root.Path || trail == null) return null;
        var boundaryIndex = trail.ToList().FindIndex(x => x.Id == root.Path);
        var currentIndex = trail.ToList().FindLastIndex(x => x.Id == path);
        if (boundaryIndex < 0 || currentIndex <= boundaryIndex) return null;
        return new(trail[currentIndex - 1].Id, trail[currentIndex - 1].Name,
            trail.Take(currentIndex).ToArray(), path);
    }

    public static string SelectionAfterRemoval(IReadOnlyList<string> keys, int index) =>
        index < 0 || index >= keys.Count ? "add" : index + 1 < keys.Count ? keys[index + 1] : index > 0 ? keys[index - 1] : "add";
}

/// <summary>Prevents delayed loads and native pickers from committing after cancel or a newer request.</summary>
public sealed class BrowserRequestState
{
    private long generation;
    public bool IsBusy { get; private set; }
    public long Begin() { IsBusy = true; return ++generation; }
    public bool IsCurrent(long request) => generation == request;
    public void Complete(long request) { if (IsCurrent(request)) IsBusy = false; }
    public void Cancel() { generation++; IsBusy = false; }
    public bool ShouldShowLoading(long request) => IsBusy && IsCurrent(request);
}
