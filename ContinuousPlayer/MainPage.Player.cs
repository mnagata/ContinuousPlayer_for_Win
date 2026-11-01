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
    private readonly PlaybackSessionState playbackState = new();
    private bool isPlayback => playbackState.IsActive;
    private bool userPaused => !playbackState.WantsPlayback || playbackState.HasTemporaryPause ||
        playbackState.Interruptions != PlaybackInterruption.None ||
        playbackState.Phase is PlaybackPhase.Ended or PlaybackPhase.Failed;
    private bool startingPlayback;
    private int mediaRequest;

    private async Task StartPlaybackAsync(MediaFile selected)
    {
        if (browserBusy || startingPlayback) return;
        var playlist = Vm.Rows.Where(x => x.Media != null).Select(x => x.Media!).ToList();
        var index = playlist.FindIndex(x => x.Id == selected.Id);
        if (index < 0) return;
        startingPlayback = true;
        try
        {
            StopPlayback();
            queue.Start(playlist, index);
            playbackLocation = location with { Register = false, Saved = location.Saved ?? FindRegisteredAncestor(location) };
            playbackState.Start();
            var stamp = playbackState.Stamp;
            Home.Visibility = Visibility.Collapsed;
            Playback.Visibility = Visibility.Visible;
            UpdateFullscreenButtons();
            try { displayRequest.RequestActive(); displayActive = true; }
            catch (Exception ex) { Debug.WriteLine(ex); }
            // Keep the browser until the black playback surface has been laid out.
            Playback.UpdateLayout();
            await NextRenderingAsync();
            if (!playbackState.IsCurrent(stamp) || disposed) return;
            CloseBrowser(BrowserCloseReason.SelectionCommitted);
            TouchSurface.Focus(FocusState.Programmatic);
            await LoadCurrentAsync();
        }
        finally { startingPlayback = false; }
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer { AutoPlay = false, IsLoopingEnabled = false };
        // Custom gestures and transport controls share the same explicit pause state.
        player.CommandManager.IsEnabled = false;
        var transport = player.SystemMediaTransportControls;
        transport.IsEnabled = true;
        transport.IsPlayEnabled = true;
        transport.IsPauseEnabled = true;
        transport.IsPreviousEnabled = true;
        transport.IsNextEnabled = true;
        transport.ButtonPressed += Transport_ButtonPressed;
        player.MediaEnded += Player_MediaEnded;
        player.MediaFailed += Player_MediaFailed;
        player.MediaOpened += Player_MediaOpened;
        player.PlaybackSession.PlaybackStateChanged += Player_PlaybackStateChanged;
        return player;
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
        if (!isPlayback || queue.Current is not { } current) return;
        var request = ++mediaRequest;
        playbackState.BeginMedia();
        pendingFailure = null;
        ReleasePlayer();
        var player = CreatePlayer();
        mediaPlayer = player;
        playerHasPlayed = false;
        expectedEnginePause = false;
        Player.SetMediaPlayer(player);
        interruptionMonitor?.TrackAudioOutput(player.AudioDevice?.Id, true);
        Vm.FileName = current.Name;
        Vm.PlaybackTitle = PlaybackPresentation.Title(current.Name);
        Vm.MediaInfo = "映像形式: 取得できません\n音声形式: 取得できません";
        Vm.PlaybackStatus = "読み込み中…";
        AudioTitle.Visibility = Visibility.Collapsed;
        UpdatePlaybackButtons();
        UpdateCursorAutoHide();
        try
        {
            MediaSource source;
            if (Uri.TryCreate(current.Location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                source = MediaSource.CreateFromUri(uri);
            else
                source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(current.Location));
            if (request != mediaRequest || !isPlayback || mediaPlayer != player) { source.Dispose(); return; }
            mediaSource = source;
            playbackItem = new MediaPlaybackItem(source);
            player.Source = playbackItem;
            var updater = player.SystemMediaTransportControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = current.Name;
            updater.Update();
            ApplyPlaybackState();
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
        // A failed source must not become playable through a delayed open event.
        if (playbackState.Phase == PlaybackPhase.Failed) return;
        playbackState.Ready();
        var session = sender.PlaybackSession;
        Vm.PlaybackStatus = "";
        AudioTitle.Visibility = session.NaturalVideoWidth == 0 ? Visibility.Visible : Visibility.Collapsed;
        var duration = session.NaturalDuration;
        Vm.MediaInfo = session.NaturalVideoWidth == 0 ? "映像形式: 映像なし" : "映像形式: 取得できません";
        if (playbackItem?.VideoTracks.Count > 0)
        {
            var video = playbackItem.VideoTracks[0].GetEncodingProperties();
            var fps = video.FrameRate.Denominator > 0 ? video.FrameRate.Numerator / (double)video.FrameRate.Denominator : 0;
            Vm.MediaInfo = $"映像形式: {Known(video.Subtype)} · {video.Width} × {video.Height}" + (fps > 0 ? $" · {fps:0.###} fps" : " · フレームレート: 取得できません");
        }
        if (playbackItem?.AudioTracks.Count > 0)
        {
            var audio = playbackItem.AudioTracks[0].GetEncodingProperties();
            Vm.MediaInfo += $"\n音声形式: {Known(audio.Subtype)} · {audio.SampleRate:N0} Hz · {audio.ChannelCount} ch";
        }
        else Vm.MediaInfo += "\n音声形式: 取得できません";
        var updater = sender.SystemMediaTransportControls.DisplayUpdater;
        updater.Type = session.NaturalVideoWidth == 0 ? MediaPlaybackType.Music : MediaPlaybackType.Video;
        if (updater.Type == MediaPlaybackType.Music) updater.MusicProperties.Title = Vm.FileName;
        updater.Update();
        ApplyPlaybackState();
    });

    private void Player_MediaEnded(MediaPlayer sender, object args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender != mediaPlayer || !isPlayback) return;
        if (playbackState.HasTemporaryPause || dialogOpen || BrowserOverlay.Visibility == Visibility.Visible)
        {
            playbackState.End();
            ApplyPlaybackState();
            return;
        }
        if (userPaused) return;
        if (queue.Move(1)) await LoadCurrentAsync();
        else { playbackState.End(); Vm.PlaybackStatus = "最後まで再生しました"; ApplyPlaybackState(true); }
    });

    private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender == mediaPlayer && isPlayback)
            await ShowPlaybackErrorAsync($"{args.Error}\n{args.ErrorMessage}", args.ExtendedErrorCode?.HResult ?? 0);
    });

    private void Transport_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => PlaybackCommand.Play,
            SystemMediaTransportControlsButton.Pause or SystemMediaTransportControlsButton.Stop => PlaybackCommand.Pause,
            SystemMediaTransportControlsButton.Previous => PlaybackCommand.Previous,
            SystemMediaTransportControlsButton.Next => PlaybackCommand.Next,
            SystemMediaTransportControlsButton.Rewind => PlaybackCommand.SeekBackward,
            SystemMediaTransportControlsButton.FastForward => PlaybackCommand.SeekForward,
            _ => PlaybackCommand.None
        };
        var key = args.Button switch
        {
            SystemMediaTransportControlsButton.Next => 0xB0, SystemMediaTransportControlsButton.Previous => 0xB1,
            SystemMediaTransportControlsButton.Stop => 0xB2,
            SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Pause => 0xB3, _ => 0
        };
        // Capture acceptance before dispatching, while the physical key is still held.
        if (key != 0 && inputMonitor?.TryAcceptMediaKey(key) == false) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (mediaPlayer == null || sender != mediaPlayer.SystemMediaTransportControls) return;
            await ExecutePlaybackCommandAsync(command);
        });
    }

    private void SetPaused(bool paused, bool focus = true)
    {
        if (mediaPlayer == null || !PlaybackInputAllowed) return;
        if (!paused && playbackState.Phase == PlaybackPhase.Ended && playbackState.Interruptions == PlaybackInterruption.None)
        {
            _ = RestartEndedAsync();
            return;
        }
        playbackState.RequestPlayback(!paused);
        ApplyPlaybackState(focus);
    }

    private async Task RestartEndedAsync()
    {
        playbackState.RequestPlayback(true);
        try { await LoadCurrentAsync(); }
        catch (Exception ex) { if (isPlayback && !disposed) await ShowPlaybackErrorAsync(ex.Message, ex.HResult); }
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
        if (mediaPlayer != null && PlaybackInputAllowed && queue.Move(delta)) await LoadCurrentAsync();
    }

    private async Task SeekAsync(int seconds)
    {
        if (mediaPlayer == null || !PlaybackInputAllowed || !mediaPlayer.PlaybackSession.CanSeek) return;
        var session = mediaPlayer.PlaybackSession;
        var seek = queue.Seek(session.Position, session.NaturalDuration, seconds);
        if (seek.Next) await MoveMediaAsync(1); else session.Position = seek.Position;
    }

    private async Task ShowPlaybackErrorAsync(string message, int code)
    {
        if (!isPlayback || disposed) return;
        playbackState.Fail();
        ApplyPlaybackState();
        Vm.PlaybackStatus = "再生できませんでした";
        if (dialogOpen || BrowserOverlay.Visibility == Visibility.Visible)
        {
            pendingFailure = (message, code);
            return;
        }
        pendingFailure = null;
        var stamp = playbackState.Stamp;
        dialogOpen = true;
        ContentDialogResult result;
        try
        {
            result = await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "再生エラー",
                Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = $"{Vm.FileName}\n\n{message}\n0x{code:X8}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = "再試行", SecondaryButtonText = "別のファイルを選ぶ", CloseButtonText = "キャンセル" }.ShowAsync();
        }
        finally { dialogOpen = false; }
        if (!playbackState.IsCurrent(stamp) || disposed) return;
        if (result == ContentDialogResult.Primary) { playbackState.RequestPlayback(true); await LoadCurrentAsync(); }
        else if (result == ContentDialogResult.Secondary) await ReturnToFolderAsync();
    }

    private async Task ShowInfoAsync()
    {
        if (!PlaybackInputAllowed || mediaPlayer == null) return;
        gestures.Reset();
        var saved = playbackState.Suspend(mediaPlayer.PlaybackSession.Position);
        ApplyPlaybackState();
        var output = mediaPlayer.AudioDevice?.Name ?? "Windows既定の再生デバイス";
        var duration = mediaPlayer.PlaybackSession.NaturalDuration;
        var text = new TextBlock { Text = $"{Vm.FileName}\n\nファイル\n名前: {Vm.FileName}\nサイズ: {FormatSize(queue.Current?.Size ?? -1)}\n長さ: {(duration > TimeSpan.Zero ? duration.ToString(@"hh\:mm\:ss") : "取得できません")}\n再生位置: {saved.Position:hh\\:mm\\:ss}\n{queue.Index + 1} / {queue.Items.Count}\n\n映像・音声形式\n{Vm.MediaInfo}\n\n音声出力\n出力先: {output}\n再生基盤: Windows MediaPlayer\n音声: Windows標準ミキサー（共有モード）\nBit-Perfect / WASAPI排他出力: 未対応", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(text, "MediaInfoText");
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "メディア情報",
            Content = new ScrollViewer { MaxHeight = 400, Content = text }, CloseButtonText = "閉じる" };
        dialog.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.KeyStatus.WasKeyDown || e.Key is not (Windows.System.VirtualKey.I or Windows.System.VirtualKey.Escape)) return;
            e.Handled = true;
            dialog.Hide();
        }), true);
        dialogOpen = true;
        activeInfoDialog = dialog;
        UpdateCursorAutoHide();
        try { await dialog.ShowAsync(); }
        finally
        {
            activeInfoDialog = null;
            dialogOpen = false;
            RestoreSuspension(saved);
        }
        await ShowPendingFailureAsync();
    }

    private async Task ReturnToFolderAsync()
    {
        if (!PlaybackInputAllowed || mediaPlayer == null) return;
        gestures.Reset();
        var target = playbackLocation;
        var selected = queue.Current?.Id;
        browserSuspension = playbackState.Suspend(mediaPlayer.PlaybackSession.Position);
        ApplyPlaybackState();
        BrowserOverlay.Visibility = Visibility.Visible;
        Home.IsEnabled = false;
        SetPlaybackInputEnabled(false);
        browserHistory.Clear();
        await NavigateAsync(target ?? new("library", "保存済みフォルダー"), false, selected);
    }

    private void StopPlayback()
    {
        gestures.Reset();
        mediaRequest++;
        playbackState.Stop();
        browserSuspension = null;
        pendingFailure = null;
        UpdateCursorAutoHide();
        ReleasePlayer();
        interruptionMonitor?.TrackAudioOutput(null, false);
        if (displayActive) { displayRequest.RequestRelease(); displayActive = false; }
        Playback.Visibility = Visibility.Collapsed;
        PauseToolbar.Visibility = Visibility.Collapsed;
        Home.Visibility = Visibility.Visible;
        Vm.PlaybackStatus = "";
        UpdateFullscreenButtons();
    }

    private void ReleasePlayer()
    {
        if (mediaPlayer != null)
        {
            var old = mediaPlayer;
            mediaPlayer = null;
            old.MediaEnded -= Player_MediaEnded;
            old.MediaFailed -= Player_MediaFailed;
            old.MediaOpened -= Player_MediaOpened;
            old.PlaybackSession.PlaybackStateChanged -= Player_PlaybackStateChanged;
            old.SystemMediaTransportControls.ButtonPressed -= Transport_ButtonPressed;
            old.SystemMediaTransportControls.IsEnabled = false;
            old.Pause();
            Player.SetMediaPlayer(null);
            old.Source = null;
            old.Dispose();
        }
        mediaSource?.Dispose();
        mediaSource = null;
        playbackItem = null;
    }

    private static string Known(string? value) => string.IsNullOrWhiteSpace(value) ? "取得できません" : value;
    private static string FormatSize(long bytes) => bytes < 0 ? "取得できません" : bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB" : $"{bytes / (1024.0 * 1024):F1} MB";

    private void Surface_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (!PlaybackInputAllowed) return;
        var point = e.GetCurrentPoint(TouchSurface);
        if (!point.Properties.IsLeftButtonPressed) return;
        var kind = e.Pointer.PointerDeviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Touch => PlaybackPointerKind.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => PlaybackPointerKind.Pen, _ => PlaybackPointerKind.Mouse
        };
        gestures.MouseTapDistance = Math.Max(2, Interop.PlaybackInputMonitor.DoubleClickWidth / (XamlRoot?.RasterizationScale ?? 1));
        gestures.Press(e.Pointer.PointerId, kind, point.Position.X, point.Position.Y, TouchSurface.ActualWidth);
        if (kind == PlaybackPointerKind.Touch && rootTouches.Any(x => x != e.Pointer.PointerId)) gestures.Invalidate();
        if (!TouchSurface.CapturePointer(e.Pointer)) gestures.Cancel(e.Pointer.PointerId);
        e.Handled = true;
    }

    private async void Surface_Released(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TouchSurface).Position;
        var command = gestures.Release(e.Pointer.PointerId, point.X, point.Y, Environment.TickCount64);
        TouchSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        await ExecutePlaybackCommandAsync(command);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch && command == PlaybackCommand.Toggle && !userPaused)
            HidePlaybackChromeIfAllowed();
    }

    private void Surface_Canceled(object sender, PointerRoutedEventArgs e) => gestures.Cancel(e.Pointer.PointerId);
}
