namespace ContinuousPlayer.Core;

public record FolderContents(List<FolderNode> Folders, List<MediaFile> Media);

public static class LocalFolders
{
    public static Task<FolderContents> ReadAsync(string path, CancellationToken cancellation) => Task.Run(() =>
    {
        var folders = new List<FolderNode>();
        var media = new List<MediaFile>();
        // Enumerate only this level. Do not silently turn access failures into empty folders.
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Name.StartsWith("._", StringComparison.Ordinal)) continue;
            if (entry is DirectoryInfo directory) folders.Add(new(directory.FullName, directory.Name));
            else if (entry is FileInfo file && MediaCatalog.IsSupported(file.Name)) media.Add(new(file.FullName, file.Name, file.FullName, file.Length));
        }
        return new FolderContents(MediaCatalog.SortFolders(folders, x => x.Name), MediaCatalog.SortMedia(media));
    }, cancellation);
}
