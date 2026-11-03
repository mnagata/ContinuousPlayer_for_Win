using ContinuousPlayer.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace ContinuousPlayer.Playback;

internal static class AlbumArtworkReader
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(4)
    });

    public static async Task<byte[]?> ReadAsync(MediaFile media, CancellationToken token)
    {
        if (media.AlbumArtUri is { } address && Uri.TryCreate(address, UriKind.Absolute, out var artwork) &&
            DlnaAddress.IsHttpTarget(artwork))
        {
            using var response = await http.GetAsync(artwork, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes) return null;
            using var stream = await response.Content.ReadAsStreamAsync(token);
            return await ReadBoundedAsync(stream, token);
        }
        if (Uri.TryCreate(media.Location, UriKind.Absolute, out var source) && source.Scheme is "http" or "https")
            return null;
        // Shell thumbnail providers can pump COM messages; keep them off the XAML thread.
        return await Task.Run(() => ReadLocalAsync(media.Location, token), token);
    }

    private static async Task<byte[]?> ReadLocalAsync(string path, CancellationToken token)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 768,
                ThumbnailOptions.ResizeThumbnail).AsTask(token);
            // Missing artwork returns a generic file icon, which is not an album cover.
            if (thumbnail is { Type: ThumbnailType.Image, Size: > 0 and <= MaximumBytes })
            {
                using var reader = new DataReader(thumbnail.GetInputStreamAt(0));
                var count = await reader.LoadAsync((uint)thumbnail.Size).AsTask(token);
                if (count == thumbnail.Size)
                {
                    var bytes = new byte[count];
                    reader.ReadBytes(bytes);
                    return bytes;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Album thumbnail: {ex.Message}"); }

        var directory = Path.GetDirectoryName(path);
        if (directory == null) return null;
        foreach (var name in new[] { Path.GetFileNameWithoutExtension(path), "cover", "folder", "front", "album" })
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp" })
        {
            token.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory, name + extension);
            if (!File.Exists(candidate)) continue;
            try
            {
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumBytes) continue;
                var bytes = await ReadBoundedAsync(stream, token);
                if (bytes is { Length: > 0 }) return bytes;
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > MaximumBytes) return null;
            result.Write(buffer, 0, count);
        }
        return result.Length > 0 ? result.ToArray() : null;
    }
}
