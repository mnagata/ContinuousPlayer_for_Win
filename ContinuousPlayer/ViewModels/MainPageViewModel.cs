using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ContinuousPlayer.Core;

namespace ContinuousPlayer.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    public ObservableCollection<BrowserRow> Rows { get; } = [];
    [ObservableProperty] public partial string BrowserTitle { get; set; } = "保存済みフォルダー";
    [ObservableProperty] public partial string EmptyMessage { get; set; } = "";
    [ObservableProperty] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial string PlaybackTitle { get; set; } = "";
    [ObservableProperty] public partial string MediaInfo { get; set; } = "";
    [ObservableProperty] public partial string PlaybackStatus { get; set; } = "";
}

public sealed partial class BrowserRow
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string FormatBadge { get; init; } = "";
    public string VisibleName => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;
    public bool HasFormatBadge => FormatBadge.Length != 0;
    public MediaFile? Media { get; init; }
    public SavedFolder? Saved { get; init; }
    public DlnaServer? Server { get; init; }
    public string IconKey => Media != null
        ? (Media.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
           System.IO.Path.GetExtension(Media.Name).ToLowerInvariant() is ".mp3" or ".flac" or ".m4a" or ".aac" or ".wav" or ".ogg" or ".opus"
            ? "AudioIcon" : "VideoIcon")
        : Kind == "add" ? "AddIcon" : Kind is "server" or "network" ? "ServerIcon" : "FolderIcon";
    public string Chevron => Media == null ? "\uE76C" : "";
    public string AccessibleName => Name + (HasFormatBadge ? $"。形式 {FormatBadge}" : "") +
        (Saved != null ? "。開くにはEnter。登録解除にはDelete、右クリックまたは長押し。" : Media != null ? "。ここから連続再生。" : "。開く。");
}
