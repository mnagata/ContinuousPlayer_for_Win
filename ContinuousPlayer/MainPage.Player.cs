using System.Diagnostics;
using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System.Display;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private readonly PlaybackQueue queue = new();
    private MediaPlayer? mediaPlayer;
    private MediaSource? mediaSource;
    private MediaPlaybackItem? playbackItem;
    private readonly DisplayRequest displayRequest = new();
    private bool displayActive;
    private bool isPlayback;
    private bool userPaused;
    private bool startingPlayback;
    private int mediaRequest;
    private Windows.Foundation.Point? pointerStart;
    private long pointerTime;
    private uint? activePointer;

    private async Task StartPlaybackAsync(MediaFile selected)
    {
        if (browserBusy || startingPlayback) return;
        var playlist = Vm.Rows.Where(x => x.Media != null).Select(x => x.Media!).ToList();
        var index = playlist.FindIndex(x => x.Id == selected.Id);
        if (index < 0) return;
        startingPlayback = true;
        StopPlayback();
        queue.Start(playlist, index);
        playbackLocation = location with { Register = false };
        isPlayback = true;
        userPaused = false;
        Home.Visibility = Visibility.Collapsed;
        Playback.Visibility = Visibility.Visible;
        ((MainWindow)App.Window).SetFullscreen(true);
        mediaPlayer = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
        // Custom gestures and transport controls share the same explicit pause state.
        mediaPlayer.CommandManager.IsEnabled = false;
        var transport = mediaPlayer.SystemMediaTransportControls;
        transport.IsEnabled = true;
        transport.IsPlayEnabled = true;
        transport.IsPauseEnabled = true;
        transport.IsPreviousEnabled = true;
        transport.IsNextEnabled = true;
        transport.ButtonPressed += Transport_ButtonPressed;
        mediaPlayer.MediaEnded += Player_MediaEnded;
        mediaPlayer.MediaFailed += Player_MediaFailed;
        mediaPlayer.MediaOpened += Player_MediaOpened;
        mediaPlayer.PlaybackSession.PlaybackStateChanged += Player_PlaybackStateChanged;
        Player.SetMediaPlayer(mediaPlayer);
        try { displayRequest.RequestActive(); displayActive = true; }
        catch (Exception ex) { Debug.WriteLine(ex); }
        // Keep the browser until the black playback surface has been laid out.
        Playback.UpdateLayout();
        await NextRenderingAsync();
        if (!isPlayback || disposed) { startingPlayback = false; return; }
        CloseBrowser();
        TouchSurface.Focus(FocusState.Programmatic);
        await LoadCurrentAsync();
        startingPlayback = false;
    }

    private static Task NextRenderingAsync()
    {
        var completion = new TaskCompletionSource();
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            completion.TrySetResult();
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        return completion.Task;
    }

    private async Task LoadCurrentAsync()
    {
        if (!isPlayback || mediaPlayer == null || queue.Current is not { } current) return;
        var request = ++mediaRequest;
        Vm.FileName = current.Name;
        Vm.MediaInfo = $"{System.IO.Path.GetFileNameWithoutExtension(current.Name)}\n{FormatSize(current.Size)}";
        Vm.PlaybackStatus = "読み込み中…";
        AudioTitle.Visibility = Visibility.Collapsed;
        PauseToolbar.Visibility = userPaused ? Visibility.Visible : Visibility.Collapsed;
        mediaPlayer.Source = null;
        UpdateCursorAutoHide();
        mediaSource?.Dispose();
        mediaSource = null;
        playbackItem = null;
        try
        {
            MediaSource source;
            if (Uri.TryCreate(current.Location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                source = MediaSource.CreateFromUri(uri);
            else
                source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(current.Location));
            if (request != mediaRequest || !isPlayback || mediaPlayer == null) { source.Dispose(); return; }
            mediaSource = source;
            playbackItem = new MediaPlaybackItem(source);
            mediaPlayer.Source = playbackItem;
            var updater = mediaPlayer.SystemMediaTransportControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = current.Name;
            updater.Update();
            if (userPaused) mediaPlayer.Pause(); else mediaPlayer.Play();
            library.LastMediaId = current.Id;
            library.LastFolderKey = FindRegisteredAncestor(playbackLocation)?.Key;
            if (playbackLocation is { } folder)
                library.LastPlaybackFolder = folder.Kind == "dlna"
                    ? new("dlna", folder.Name, folder.Path, folder.Server?.Id, folder.Server?.Name, folder.Trail?.ToList())
                    : new("local", folder.Name, folder.Path);
            await SaveLibraryAsync();
        }
        catch (Exception ex)
        {
            if (request == mediaRequest && isPlayback) await ShowPlaybackErrorAsync(ex.Message, ex.HResult);
        }
    }

    private SavedFolder? FindRegisteredAncestor(Location? current)
    {
        if (current == null) return null;
        if (current.Kind == "dlna")
        {
            var ancestors = current.Trail ?? [];
            return library.Folders.Where(x => x.Kind == "dlna" && x.ServerId == current.Server?.Id && ancestors.Any(a => a.Id == x.Path))
                .OrderByDescending(x => ancestors.FindIndex(a => a.Id == x.Path)).FirstOrDefault();
        }
        return library.Folders.Where(x => x.Kind == "local" && (current.Path.Equals(x.Path, StringComparison.OrdinalIgnoreCase) ||
            current.Path.StartsWith(x.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.Path.Length).FirstOrDefault();
    }

    private void Player_MediaOpened(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (sender != mediaPlayer || !isPlayback) return;
        var session = sender.PlaybackSession;
        Vm.PlaybackStatus = "";
        AudioTitle.Visibility = session.NaturalVideoWidth == 0 ? Visibility.Visible : Visibility.Collapsed;
        var duration = session.NaturalDuration;
        Vm.MediaInfo = $"{System.IO.Path.GetFileNameWithoutExtension(queue.Current!.Name)}\n{FormatSize(queue.Current.Size)} · {duration:hh\\:mm\\:ss}";
        if (playbackItem?.VideoTracks.Count > 0)
        {
            var video = playbackItem.VideoTracks[0].GetEncodingProperties();
            var fps = video.FrameRate.Denominator > 0 ? video.FrameRate.Numerator / (double)video.FrameRate.Denominator : 0;
            Vm.MediaInfo += $"\n{video.Subtype} · {video.Width} × {video.Height}" + (fps > 0 ? $" · {fps:0.###} fps" : "");
        }
        if (playbackItem?.AudioTracks.Count > 0)
        {
            var audio = playbackItem.AudioTracks[0].GetEncodingProperties();
            Vm.MediaInfo += $"\n{audio.Subtype} · {audio.SampleRate:N0} Hz · {audio.ChannelCount} ch";
        }
        var updater = sender.SystemMediaTransportControls.DisplayUpdater;
        updater.Type = session.NaturalVideoWidth == 0 ? MediaPlaybackType.Music : MediaPlaybackType.Video;
        if (updater.Type == MediaPlaybackType.Music) updater.MusicProperties.Title = Vm.FileName;
        updater.Update();
        UpdateTransportState();
        UpdateCursorAutoHide();
    });

    private void Player_MediaEnded(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender != mediaPlayer || !isPlayback) return;
        if (queue.Move(1)) await LoadCurrentAsync();
        else { SetPaused(true); Vm.PlaybackStatus = "すべてのファイルの再生が終了しました。"; }
    });

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender == mediaPlayer && isPlayback)
            await ShowPlaybackErrorAsync($"{args.Error}\n{args.ErrorMessage}", args.ExtendedErrorCode?.HResult ?? 0);
    });

    private void Transport_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (mediaPlayer == null || !isPlayback || sender != mediaPlayer.SystemMediaTransportControls) return;
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: SetPaused(false); break;
            case SystemMediaTransportControlsButton.Pause: SetPaused(true); break;
            case SystemMediaTransportControlsButton.Previous: await MoveMediaAsync(-1); break;
            case SystemMediaTransportControlsButton.Next: await MoveMediaAsync(1); break;
            case SystemMediaTransportControlsButton.Rewind: await SeekAsync(-10); break;
            case SystemMediaTransportControlsButton.FastForward: await SeekAsync(10); break;
            case SystemMediaTransportControlsButton.Stop: SetPaused(true); break;
        }
    });

    private void SetPaused(bool paused, bool focus = true)
    {
        if (mediaPlayer == null || !isPlayback) return;
        userPaused = paused;
        if (paused) mediaPlayer.Pause(); else mediaPlayer.Play();
        PauseToolbar.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        UpdateCursorAutoHide();
        UpdateTransportState();
        if (focus)
        {
            if (paused) ResumeButton.Focus(FocusState.Programmatic);
            else TouchSurface.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateTransportState()
    {
        if (mediaPlayer == null) return;
        var transport = mediaPlayer.SystemMediaTransportControls;
        transport.PlaybackStatus = userPaused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
        transport.IsPreviousEnabled = queue.Index > 0;
        transport.IsNextEnabled = queue.Index + 1 < queue.Items.Count;
    }

    private async Task MoveMediaAsync(int delta)
    {
        if (mediaPlayer != null && isPlayback && queue.Move(delta)) await LoadCurrentAsync();
    }

    private async Task SeekAsync(int seconds)
    {
        if (mediaPlayer == null || !isPlayback || !mediaPlayer.PlaybackSession.CanSeek) return;
        var session = mediaPlayer.PlaybackSession;
        var seek = queue.Seek(session.Position, session.NaturalDuration, seconds);
        if (seek.Next) await MoveMediaAsync(1); else session.Position = seek.Position;
    }

    private async Task ShowPlaybackErrorAsync(string message, int code)
    {
        if (!isPlayback || disposed) return;
        SetPaused(true);
        Vm.PlaybackStatus = "再生できませんでした。Iキーで情報を確認するか、フォルダーから別のファイルを選択してください。";
        if (dialogOpen) return;
        dialogOpen = true;
        ContentDialogResult result;
        try
        {
            result = await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "再生エラー",
                Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = $"{Vm.FileName}\n\n{message}\n0x{code:X8}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = "再試行", SecondaryButtonText = "別のファイルを選ぶ", CloseButtonText = "キャンセル" }.ShowAsync();
        }
        finally { dialogOpen = false; }
        if (!isPlayback || disposed) return;
        if (result == ContentDialogResult.Primary) { userPaused = false; await LoadCurrentAsync(); }
        else if (result == ContentDialogResult.Secondary) await ReturnToFolderAsync();
    }

    private async Task ShowInfoAsync()
    {
        if (!isPlayback || mediaPlayer == null) return;
        var output = mediaPlayer.AudioDevice?.Name ?? "Windows既定の再生デバイス";
        await ShowMessageAsync("再生情報", $"{Vm.FileName}\n\n{Vm.MediaInfo}\n\n{queue.Index + 1} / {queue.Items.Count}\n音声出力: {output}\n再生基盤: Windows MediaPlayer\n音声: Windows標準ミキサー（共有モード）\nBit-Perfect / WASAPI排他出力: 未対応\n\n左右タップ: ±10秒 / 中央タップ: 再生切り替え\n左スワイプ: 次 / 右スワイプ: 前\nEsc: 再生元フォルダー / F11: 全画面切り替え");
    }

    private async Task ReturnToFolderAsync()
    {
        var target = playbackLocation;
        var selected = queue.Current?.Id;
        StopPlayback();
        if (target == null) { await OpenLibraryAsync(); return; }
        // Rediscover DLNA by stable server ID, rather than reusing old URLs.
        if (target.Kind == "dlna" && target.Server is { } server)
            target = target with { Server = null, Saved = new("dlna", target.Name, target.Path, server.Id, server.Name, target.Trail) };
        BrowserOverlay.Visibility = Visibility.Visible;
        Home.IsEnabled = false;
        browserHistory.Clear();
        await NavigateAsync(target, false, selected);
    }

    private void StopPlayback()
    {
        mediaRequest++;
        isPlayback = false;
        UpdateCursorAutoHide();
        userPaused = false;
        if (mediaPlayer != null)
        {
            mediaPlayer.MediaEnded -= Player_MediaEnded;
            mediaPlayer.MediaFailed -= Player_MediaFailed;
            mediaPlayer.MediaOpened -= Player_MediaOpened;
            mediaPlayer.PlaybackSession.PlaybackStateChanged -= Player_PlaybackStateChanged;
            mediaPlayer.SystemMediaTransportControls.ButtonPressed -= Transport_ButtonPressed;
            mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
            mediaPlayer.Pause();
            Player.SetMediaPlayer(null);
            mediaPlayer.Source = null;
            mediaPlayer.Dispose();
            mediaPlayer = null;
        }
        mediaSource?.Dispose();
        mediaSource = null;
        playbackItem = null;
        if (displayActive) { displayRequest.RequestRelease(); displayActive = false; }
        Playback.Visibility = Visibility.Collapsed;
        PauseToolbar.Visibility = Visibility.Collapsed;
        Home.Visibility = Visibility.Visible;
        Vm.PlaybackStatus = "";
        if (!disposed) ((MainWindow)App.Window).SetFullscreen(false);
    }

    private static string FormatSize(long bytes) => bytes < 0 ? "サイズ不明" : bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB" : $"{bytes / (1024.0 * 1024):F1} MB";

    private void Surface_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (activePointer != null || !e.GetCurrentPoint(TouchSurface).Properties.IsLeftButtonPressed) return;
        activePointer = e.Pointer.PointerId;
        pointerStart = e.GetCurrentPoint(TouchSurface).Position;
        pointerTime = Stopwatch.GetTimestamp();
        TouchSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private async void Surface_Released(object sender, PointerRoutedEventArgs e)
    {
        if (activePointer != e.Pointer.PointerId || pointerStart is not { } start) return;
        var point = e.GetCurrentPoint(TouchSurface).Position;
        var seconds = Stopwatch.GetElapsedTime(pointerTime).TotalSeconds;
        pointerStart = null;
        activePointer = null;
        TouchSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        var dx = point.X - start.X;
        var dy = point.Y - start.Y;
        if (Math.Abs(dx) > 100 && Math.Abs(dx) > Math.Abs(dy) && Math.Abs(dx) / Math.Max(.001, seconds) > 100)
            await MoveMediaAsync(dx < 0 ? 1 : -1);
        else if (Math.Abs(dx) < 24 && Math.Abs(dy) < 24)
        {
            if (point.X < TouchSurface.ActualWidth / 3) await SeekAsync(-10);
            else if (point.X > TouchSurface.ActualWidth * 2 / 3) await SeekAsync(10);
            else SetPaused(!userPaused);
        }
    }

    private void Surface_Canceled(object sender, PointerRoutedEventArgs e) { pointerStart = null; activePointer = null; }
}
