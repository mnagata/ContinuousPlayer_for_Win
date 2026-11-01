using System.Globalization;
using System.Text.RegularExpressions;

namespace ContinuousPlayer.Core;

public record MediaFile(string Id, string Name, string Location, long Size = -1, string? MimeType = null);
public record FolderNode(string Id, string Name);
public record SavedFolder(string Kind, string Name, string Path, string? ServerId = null, string? ServerName = null, List<FolderNode>? Trail = null, string? DescriptionUrl = null)
{
    public string Key => Kind == "dlna" ? $"dlna:{ServerId}:{Path}" : $"local:{Path.ToUpperInvariant()}";
}

public static partial class MediaCatalog
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus" };
    [GeneratedRegex(@"^(.+?)\s+(OP|ED)(\d*)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpEdPattern();
    [GeneratedRegex(@"^(.*?)\s*(\d{4})(?:(0[1-9]|1[0-2]))?年?\s*[*_]?([冬春夏秋])[*_]?(.*)$")]
    private static partial Regex SeasonPattern();
    public static bool IsSupported(string name) => !name.StartsWith("._", StringComparison.Ordinal) && Extensions.Contains(System.IO.Path.GetExtension(name));
    private static readonly StringComparer Names = StringComparer.Create(CultureInfo.CurrentCulture, true);

    public static List<MediaFile> SortMedia(IEnumerable<MediaFile> files)
    {
        var result = files.OrderBy(x => x.Name, Names).ToList();
        var groups = result.Select((item, index) => (index, info: ParseOpEd(item.Name)))
            .Where(x => x.info != null).GroupBy(x => x.info!.Value.Base, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var positions = group.Select(x => x.index).ToArray();
            var ordered = positions.Select(i => result[i]).OrderBy(x => ParseOpEd(x.Name)!.Value.Number)
                .ThenBy(x => ParseOpEd(x.Name)!.Value.Category).ThenBy(x => x.Name, Names).ToArray();
            for (var i = 0; i < positions.Length; i++) result[positions[i]] = ordered[i];
        }
        return result;
    }

    private static (string Base, int Number, int Category)? ParseOpEd(string name)
    {
        var m = OpEdPattern().Match(System.IO.Path.GetFileNameWithoutExtension(name).Trim());
        if (!m.Success) return null;
        return (m.Groups[1].Value.Trim(), int.TryParse(m.Groups[3].Value, out var n) ? n : 1,
            m.Groups[2].Value.Equals("OP", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    public static List<T> SortFolders<T>(IEnumerable<T> folders, Func<T, string> name)
    {
        var result = folders.OrderBy(name, Names).ToList();
        var groups = result.Select((item, index) => (index, match: SeasonPattern().Match(name(item).Trim())))
            .Where(x => x.match.Success).GroupBy(x => (x.match.Groups[1].Value.Trim().ToUpperInvariant(), x.match.Groups[5].Value.Trim().ToUpperInvariant()));
        foreach (var group in groups)
        {
            var positions = group.Select(x => x.index).ToArray();
            var ordered = group.OrderBy(x => int.Parse(x.match.Groups[2].Value, CultureInfo.InvariantCulture))
                .ThenBy(x => "冬春夏秋".IndexOf(x.match.Groups[4].Value, StringComparison.Ordinal)).Select(x => result[x.index]).ToArray();
            for (var i = 0; i < positions.Length; i++) result[positions[i]] = ordered[i];
        }
        return result;
    }
}
