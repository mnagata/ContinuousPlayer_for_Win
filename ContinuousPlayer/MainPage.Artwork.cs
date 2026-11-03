using ContinuousPlayer.Core;
using ContinuousPlayer.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private CancellationTokenSource? artworkCancellation;

    private void ClearAudioArtwork()
    {
        artworkCancellation?.Cancel();
        artworkCancellation?.Dispose();
        artworkCancellation = null;
        AudioArtwork.Source = null;
        AudioArtworkHost.Visibility = Visibility.Collapsed;
        AudioPlaceholder.Visibility = Visibility.Visible;
    }

    private async Task LoadAudioArtworkAsync(MediaFile media, IPlaybackEngine engine, PlaybackStamp stamp)
    {
        if (mediaCancellation == null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(mediaCancellation.Token);
        artworkCancellation = request;
        request.CancelAfter(TimeSpan.FromSeconds(8));
        var token = request.Token;
        try
        {
            var bytes = await AlbumArtworkReader.ReadAsync(media, token);
            if (bytes == null || token.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(token);
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 ||
                decoder.PixelWidth > 16384 || decoder.PixelHeight > 16384) return;
            var scale = Math.Min(1, 768.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var image = new BitmapImage
            {
                DecodePixelWidth = Math.Max(1, (int)(decoder.PixelWidth * scale)),
                DecodePixelHeight = Math.Max(1, (int)(decoder.PixelHeight * scale))
            };
            stream.Seek(0);
            await image.SetSourceAsync(stream).AsTask(token);
            if (token.IsCancellationRequested || disposed || engine != playbackEngine || !playbackState.IsCurrent(stamp)) return;
            AudioArtwork.Source = image;
            AudioArtworkHost.Visibility = Visibility.Visible;
            AudioPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Album artwork: {ex.Message}"); }
        finally
        {
            if (ReferenceEquals(artworkCancellation, request)) artworkCancellation = null;
        }
    }
}
