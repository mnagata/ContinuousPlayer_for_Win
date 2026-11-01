using System.Text.Json;

namespace ContinuousPlayer.Core;

public sealed class LibraryStore(string directory)
{
    private readonly string file = System.IO.Path.Combine(directory, "library.json");
    public List<SavedFolder> Folders { get; private set; } = [];
    public string? LastFolderKey { get; set; }
    public string? LastMediaId { get; set; }
    public SavedFolder? LastPlaybackFolder { get; set; }
    public string? LoadWarning { get; private set; }
    private readonly SemaphoreSlim saveLock = new(1, 1);

    public async Task LoadAsync()
    {
        if (!File.Exists(file)) return;
        try
        {
            var state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(file));
            if (state == null || state.Version != 1) throw new InvalidDataException("保存形式が不正です。");
            Folders = (state.Folders ?? []).Where(IsValid).Select(NormalizeEndpoint)
                .DistinctBy(x => x.Key).ToList();
            LastFolderKey = state.LastFolderKey;
            LastMediaId = state.LastMediaId;
            LastPlaybackFolder = IsValid(state.LastPlaybackFolder) ? NormalizeEndpoint(state.LastPlaybackFolder!) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LoadWarning = "保存済みフォルダーを読み込めませんでした。元の保存ファイルは保持しています。";
        }
    }

    public async Task SaveAsync()
    {
        var snapshot = new State(1, Folders.ToList(), LastFolderKey, LastMediaId, LastPlaybackFolder);
        await saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            var temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
        finally { saveLock.Release(); }
    }

    public void Register(SavedFolder folder)
    {
        var index = Folders.FindIndex(x => x.Key == folder.Key);
        if (index < 0) Folders.Add(folder); else Folders[index] = folder;
    }
    public void Remove(string key) => Folders.RemoveAll(x => x.Key == key);
    public void UpdateServerEndpoint(string serverId, Uri description)
    {
        if (!DlnaAddress.IsHttpTarget(description)) throw new ArgumentException("Invalid description URL.", nameof(description));
        for (var i = 0; i < Folders.Count; i++)
            if (Folders[i].Kind == "dlna" && Folders[i].ServerId == serverId) Folders[i] = Folders[i] with { DescriptionUrl = description.AbsoluteUri };
        if (LastPlaybackFolder is { Kind: "dlna" } last && last.ServerId == serverId)
            LastPlaybackFolder = last with { DescriptionUrl = description.AbsoluteUri };
    }
    private static SavedFolder NormalizeEndpoint(SavedFolder folder) => folder.DescriptionUrl == null ||
        Uri.TryCreate(folder.DescriptionUrl, UriKind.Absolute, out var uri) && DlnaAddress.IsHttpTarget(uri)
        ? folder : folder with { DescriptionUrl = null };
    private static bool IsValid(SavedFolder? folder) => folder is not null &&
        !string.IsNullOrWhiteSpace(folder.Name) && !string.IsNullOrWhiteSpace(folder.Path) && !folder.Path.Contains('\0') &&
        (folder.Kind == "local" && System.IO.Path.IsPathFullyQualified(folder.Path) ||
         folder.Kind == "dlna" && !string.IsNullOrWhiteSpace(folder.ServerId) && folder.Trail is { Count: > 0 } &&
         folder.Trail.All(t => t != null && !string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name)));
    private record State(int Version, List<SavedFolder>? Folders, string? LastFolderKey, string? LastMediaId, SavedFolder? LastPlaybackFolder = null);
}
