using System.Diagnostics;
using ContinuousPlayer.Core;
using ContinuousPlayer.Playback;
using ContinuousPlayer.Audio.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System.Display;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private readonly PlaybackQueue queue = new();
    private readonly PlaybackEngineSession engineSession = new();
    private IPlaybackEngine? playbackEngine => engineSession.Engine;
    private Interop.PlaybackTransportControls? transportControls;
    private readonly DisplayRequest displayRequest = new();
    private bool displayActive;
    private readonly PlaybackSessionState playbackState = new();
    private bool isPlayback => playbackState.IsActive;
    private bool userPaused => !playbackState.WantsPlayback || playbackState.HasTemporaryPause ||
        playbackState.Interruptions != PlaybackInterruption.None ||
        playbackState.Phase is PlaybackPhase.Ended or PlaybackPhase.Failed;
    private bool startingPlayback;
    private int mediaRequest;
    private readonly PlaybackRecovery recovery = new();
    private readonly PlaybackProgress playbackProgress = new();
    private CancellationTokenSource? mediaCancellation;
    private TimeSpan retryTarget;
    private ContentDialog? activeErrorDialog;
    private (string Message, int Code)? lastFailure;

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

    private async Task<IPlaybackEngine> CreatePlayerAsync(MediaFile file, CancellationToken token)
    {
        IPlaybackEngine player;
        var options = new AudioPlaybackOptions(audioSettings.Exclusive && !standardForCurrent, audioSettings.DeviceId);
        var candidate = options.ProbesAudio(file);
        var hasVideo = candidate && (await LocalAudioInspector.InspectAsync(new PlaybackSource(file.Location, file.MimeType), token)).HasVideo;
        token.ThrowIfCancellationRequested();
        if (candidate && !hasVideo)
        {
            player = new WasapiPlaybackEngine(audioSettings.DeviceId, App.AudioOptions.LogPath != null ? AudioPlaybackLog.Write : null);
            standardReason = "";
        }
        else
        {
            player = new MediaPlayerPlaybackEngine(Player);
            standardReason = standardForCurrent ? "この曲を標準再生する操作を選択しました。"
                : !audioSettings.Exclusive ? "標準モードを選択しています。"
                : hasVideo ? "映像を含むファイルは標準再生です。"
                : "このコンテンツは音声の排他再生の対象外です。";
        }
        player.Ended += Player_MediaEnded;
        player.Failed += Player_MediaFailed;
        player.Opened += Player_MediaOpened;
        player.StateChanged += Player_PlaybackStateChanged;
        player.SeekCompleted += Player_SeekCompleted;
        player.OutputChanged += Player_OutputChanged;
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

    private async Task LoadCurrentAsync(bool retry = false)
    {
        if (!isPlayback || queue.Current is not { } current) return;
        var request = ++mediaRequest;
        mediaCancellation?.Cancel(); mediaCancellation?.Dispose();
        mediaCancellation = new CancellationTokenSource();
        var cancellation = mediaCancellation.Token;
        recovery.Begin(current.Id, retry);
        playbackProgress.Reset();
        retryTarget = retry ? recovery.Position : TimeSpan.Zero;
        if (!retry) standardForCurrent = false;
        lastInitializedOutput = null;
        playbackState.BeginMedia();
        pendingFailure = null;
        lastFailure = null;
        ClearAudioArtwork();
        ReleasePlayer();
        playerHasPlayed = false;
        expectedEnginePause = false;
        Vm.FileName = current.Name;
        Vm.PlaybackTitle = PlaybackPresentation.Title(current.Name);
        Vm.MediaInfo = "映像形式: 取得できません\n音声形式: 取得できません";
        Vm.PlaybackStatus = "読み込み中…";
        AudioTitle.Visibility = Visibility.Collapsed;
        UpdatePlaybackButtons();
        UpdateCursorAutoHide();
        try
        {
            if (IsNetworkMedia(current.Location) && playbackLocation is { Kind: "dlna" } origin)
            {
                var registered = library.Folders.FirstOrDefault(x => x.Key == origin.Saved?.Key) ?? origin.Saved
                    ?? new SavedFolder("dlna", origin.Name, origin.Path, origin.Server?.Id, origin.Server?.Name, origin.Trail);
                registered = registered with { DescriptionUrl = registered.DescriptionUrl ?? origin.Server?.DescriptionUri?.AbsoluteUri };
                var server = await dlna.ReconnectAsync(registered, cancellation);
                var fresh = (await dlna.BrowseAsync(server, origin.Path, cancellation)).FirstOrDefault(x => x.Id == current.Id)?.Media
                    ?? throw new IOException("元のファイルが見つかりません。ファイル一覧から再選択してください。");
                if (request != mediaRequest || disposed || cancellation.IsCancellationRequested) return;
                queue.RefreshCurrent(fresh); current = fresh;
                playbackLocation = origin with { Server = server, Saved = registered };
            }
            var player = await CreatePlayerAsync(current, cancellation);
            if (request != mediaRequest || disposed || cancellation.IsCancellationRequested)
            {
                await player.DisposeAsync(); return;
            }
            engineSession.Replace(player);
            interruptionMonitor?.TrackAudioOutput(player.Output);
            if (!await engineSession.PrepareAsync(new(current.Location, current.MimeType), cancellation) ||
                request != mediaRequest || disposed || !isPlayback || playbackEngine != player) return;
            ApplyPlaybackState();
            library.LastMediaId = current.Id;
            library.LastFolderKey = FindRegisteredAncestor(playbackLocation)?.Key;
            if (playbackLocation is { } folder)
                library.LastPlaybackFolder = folder.Kind == "dlna"
                    ? new("dlna", folder.Name, folder.Path, folder.Server?.Id, folder.Server?.Name, folder.Trail?.ToList(), folder.Server?.DescriptionUri?.AbsoluteUri)
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

    private void Player_MediaOpened(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender is not IPlaybackEngine engine || engine != playbackEngine || !isPlayback || disposed) return;
        // A failed source must not become playable through a delayed open event.
        if (playbackState.Phase == PlaybackPhase.Failed) return;
        var stamp = playbackState.Stamp;
        var session = engine;
        Vm.PlaybackStatus = retryTarget > TimeSpan.Zero ? "再生位置を復元中…" : "";
        AudioTitle.Visibility = !session.HasVideo ? Visibility.Visible : Visibility.Collapsed;
        Vm.MediaInfo = !session.HasVideo ? "映像形式: 映像なし" : "映像形式: 取得できません";
        if (session.Video is { } video)
        {
            var fps = video.FramesPerSecond ?? 0;
            Vm.MediaInfo = $"映像形式: {Known(video.Codec)} · {video.Width} × {video.Height}" + (fps > 0 ? $" · {fps:0.###} fps" : " · フレームレート: 取得できません");
        }
        if (session.Audio is { } audio)
        {
            Vm.MediaInfo += $"\n音声形式: {Known(audio.Codec)} · {audio.SampleRate:N0} Hz · {audio.Channels} ch";
            if (audio.BitsPerSample is { } bits) Vm.MediaInfo += $" · {bits}-bit（入力情報）";
        }
        else Vm.MediaInfo += "\n音声形式: 取得できません";
        if (queue.Current is { } resource && IsNetworkMedia(resource.Location))
            Vm.MediaInfo += $"\n配信リソース: {(resource.IsTranscoded ? "サーバー変換あり" : "変換の申告なし")}。形式は受信したデータを調査した値です。";
        if (engine.DecodedPcm is { } decoded) Vm.MediaInfo += $"\nデコードPCM: {DescribePcm(decoded)}";
        var restored = true;
        if (retryTarget > TimeSpan.Zero)
        {
            restored = false;
            if (session.CanSeek)
            {
                try
                {
                    restored = await engineSession.RestorePositionAsync(recovery.RestoreTarget(session.Duration),
                        TimeSpan.FromSeconds(queue.Current is { } current && IsNetworkMedia(current.Location) ? 25 : 5), mediaCancellation!.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    if (sender == playbackEngine && playbackState.IsCurrent(stamp)) await ShowPlaybackErrorAsync(ex.Message, ex.HResult);
                    return;
                }
            }
        }
        if (sender != playbackEngine || !playbackState.IsCurrent(stamp) || playbackState.Phase != PlaybackPhase.Preparing) return;
        playbackState.Ready();
        if (!restored)
        {
            playbackState.RequestPlayback(false);
            Vm.PlaybackStatus = "位置を復元できませんでした。現在位置からの再生は手動で操作してください。";
        }
        else Vm.PlaybackStatus = "";
        ApplyPlaybackState();
        if (!session.HasVideo && queue.Current is { } track)
            _ = LoadAudioArtworkAsync(track, session, stamp);
    });

    private void Player_MediaEnded(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender != playbackEngine || !isPlayback || disposed) return;
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

    private void Player_MediaFailed(object? sender, PlaybackEngineFailureEventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (sender == playbackEngine && isPlayback && !disposed)
            await ShowPlaybackErrorAsync(args.Message, args.Code);
    });

    private void Player_OutputChanged(object? sender, EventArgs args)
    {
        var observed = (sender as IPlaybackEngine)?.Output;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!disposed && isPlayback && sender == playbackEngine)
            {
                if (observed?.Format != null) lastInitializedOutput = observed;
                interruptionMonitor?.TrackAudioOutput(playbackEngine!.Output);
                UpdateAppliedAudioStatus();
            }
        });
    }

    private void UpdateAppliedAudioStatus()
    {
        if (playbackState.Phase != PlaybackPhase.Ready || playbackEngine == null) return;
        if (playbackState.Interruptions.HasFlag(PlaybackInterruption.AudioOutputUnavailable))
        {
            Vm.PlaybackStatus = "出力機器を利用できません"; return;
        }
        if (playbackEngine.Output.Mode == PlaybackOutputMode.Exclusive)
            Vm.PlaybackStatus = playbackEngine.Output.Format is { } actual
                ? $"排他 · {actual.SampleRate / 1000.0:0.###} kHz" : "排他（出力停止中）";
        else Vm.PlaybackStatus = audioSettings.Exclusive ? standardForCurrent ? "標準（この曲のみ）" : "標準（排他対象外）" : "標準";
    }

    private static string DescribePcm(PcmFormat pcm) => $"{pcm.SampleRate:N0} Hz · {pcm.Channels} ch · " +
        $"{pcm.Encoding} · 有効{pcm.ValidBits}-bit / 格納{pcm.ContainerBits}-bit";

    private string DescribeAudioOutput()
    {
        var output = playbackEngine!.Output;
        var deviceName = output.DeviceName ?? (output.UsesDefaultDevice ? "Windows既定の再生デバイス" : audioSettings.DeviceName ?? "選択した出力機器");
        var description = $"設定: {RequestedAudioMode}\n実際の再生モード: {(output.Mode == PlaybackOutputMode.Exclusive ? "WASAPI排他" : "標準（共有モード）")}\n出力先: {deviceName}\n再生基盤: {output.EngineName}";
        return output.Mode == PlaybackOutputMode.Exclusive
            ? description + $"\n機器ID: {output.DeviceId}\n直近の排他出力形式: {((output.Format ?? lastInitializedOutput?.Format) is { } pcm ? DescribePcm(pcm) : "初期化未完了")}\n状態: {(userPaused || output.Format == null ? "一時停止（排他出力を解放）" : "再生中")}"
            : description + $"\n{standardReason}\n出力PCM形式: Windowsが管理（排他レートとして表示しません）";
    }

    private void Transport_Command(PlaybackCommand command)
    {
        var request = mediaRequest;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (disposed || playbackEngine == null || request != mediaRequest) return;
            await ExecutePlaybackKeyboardCommandAsync(command);
        });
    }

    private void SetPaused(bool paused, bool focus = true)
    {
        TracePlaybackSession("pause-request", new { paused, PlaybackInputAllowed });
        if (!PlaybackInputAllowed) return;
        if (!paused && playbackState.Phase == PlaybackPhase.Failed)
        {
            _ = RetryCurrentAsync();
            return;
        }
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

    private async Task RetryCurrentAsync()
    {
        if (!PlaybackInputAllowed || playbackState.Phase != PlaybackPhase.Failed) return;
        playbackState.RequestPlayback(true);
        await LoadCurrentAsync(true);
    }

    private TimeSpan CurrentPosition => playbackState.Phase == PlaybackPhase.Failed ? recovery.Position : playbackEngine?.Position ?? TimeSpan.Zero;
    private bool CanSeekCurrent => playbackState.Phase is PlaybackPhase.Ready or PlaybackPhase.Ended && playbackEngine?.CanSeek == true;

    private void RecordConfirmedPosition()
    {
        if (playbackState.Phase == PlaybackPhase.Ready && queue.Current is { } file && playbackEngine is { } player &&
            player.State is PlaybackEngineState.Playing or PlaybackEngineState.Paused or PlaybackEngineState.Buffering)
            recovery.Observe(file.Id, player.Position);
    }

    private void CheckPlaybackProgress()
    {
        if (playbackEngine is not { } player) return;
        var expected = playbackState.CanPlay || playbackState.Phase == PlaybackPhase.Preparing &&
            playbackState.WantsPlayback && !playbackState.HasTemporaryPause &&
            playbackState.Interruptions == PlaybackInterruption.None;
        if (playbackProgress.TimedOut(player.Position, TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency), expected))
            _ = ShowPlaybackErrorAsync("30秒間、再生位置が進みませんでした。接続先やファイルを確認して再試行してください。", unchecked((int)0x800705B4));
    }

    private void Player_SeekCompleted(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (disposed || playbackEngine != sender) return;
        RecordConfirmedPosition();
    });

    private void UpdateTransportState()
    {
        transportControls?.Update(isPlayback && playbackEngine != null, userPaused,
            queue.Index > 0, queue.Index + 1 < queue.Items.Count, Vm.FileName,
            playbackState.Phase == PlaybackPhase.Preparing || playbackEngine?.HasVideo == true);
    }

    private async Task MoveMediaAsync(int delta)
    {
        if (PlaybackInputAllowed && queue.Move(delta)) await LoadCurrentAsync();
    }

    private async Task SeekAsync(int seconds)
    {
        if (playbackEngine == null || !PlaybackInputAllowed || !CanSeekCurrent) return;
        var session = playbackEngine;
        var seek = queue.Seek(session.Position, session.Duration, seconds);
        playbackProgress.Reset();
        if (seek.Next) await MoveMediaAsync(1); else session.Seek(seek.Position);
    }

    private async Task ShowPlaybackErrorAsync(string message, int code)
    {
        if (!isPlayback || disposed || playbackState.Phase == PlaybackPhase.Failed && lastFailure != null) return;
        RecordConfirmedPosition();
        lastFailure = (message, code);
        playbackState.Fail();
        ApplyPlaybackState();
        Vm.PlaybackStatus = "再生できませんでした";
        if (dialogOpen || BrowserOverlay.Visibility == Visibility.Visible)
        {
            pendingFailure = (message, code);
            return;
        }
        pendingFailure = null;
        await ShowFailureDialogAsync();
    }

    private async Task ShowFailureDialogAsync()
    {
        if (disposed || !isPlayback || dialogOpen || lastFailure is not { } failure || playbackState.Phase != PlaybackPhase.Failed) return;
        var stamp = playbackState.Stamp;
        dialogOpen = true;
        ContentDialogResult result;
        var chooseNext = false;
        var chooseStandard = false;
        var exclusiveAttempt = audioSettings.Exclusive && !standardForCurrent && queue.Current is { } attempt && new AudioPlaybackOptions(true).ProbesAudio(attempt);
        var guidance = exclusiveAttempt ? AudioFailureGuidance.Describe(failure.Code)
            : FailureGuidance.Playback(queue.Current is { } current && IsNetworkMedia(current.Location), failure.Code);
        var mode = playbackEngine?.Output.Mode == PlaybackOutputMode.Exclusive || exclusiveAttempt ? "排他（停止中）" : "標準（停止中）";
        var text = new TextBlock { Text = $"{Vm.FileName}\n\n設定: {RequestedAudioMode}\n再生経路: {mode}\n{guidance}\n\n最後に確認した位置: {recovery.Position:hh\\:mm\\:ss}\n{failure.Message}\n0x{failure.Code:X8}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(text, "PlaybackFailureText");
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(text);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "再生エラー",
            Content = new ScrollViewer { MaxHeight = 360, Content = panel }, PrimaryButtonText = "再試行",
            SecondaryButtonText = "ファイル一覧", CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Primary };
        if (queue.Index + 1 < queue.Items.Count)
        {
            var next = new Button { Content = "次のファイル", HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(next, "ErrorNextFile");
            next.Click += (_, _) => { chooseNext = true; dialog.Hide(); };
            panel.Children.Add(next);
        }
        if (exclusiveAttempt)
        {
            var standard = new Button { Content = "この曲を標準再生", HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(standard, "ErrorPlayStandard");
            standard.Click += (_, _) => { chooseStandard = true; dialog.Hide(); };
            panel.Children.Add(standard);
        }
        activeErrorDialog = dialog;
        dialog.Opened += (_, _) => FocusPrimaryDialogButton(dialog);
        try
        {
            UpdateCursorAutoHide();
            result = await dialog.ShowAsync();
        }
        finally { activeErrorDialog = null; dialogOpen = false; }
        if (!playbackState.IsCurrent(stamp) || disposed) return;
        if (chooseStandard)
        {
            standardForCurrent = true;
            // This explicit choice changes the destination to the Windows default.
            // An unavailable old DAC must not block that new destination. Other interruptions remain.
            interruptionMonitor?.TrackAudioOutput(null);
            playbackState.SetInterruption(PlaybackInterruption.AudioOutputUnavailable, false);
            playbackState.RequestPlayback(true);
            await LoadCurrentAsync(true);
        }
        else if (chooseNext) { playbackState.RequestPlayback(true); await MoveMediaAsync(1); }
        else if (result == ContentDialogResult.Primary) await RetryCurrentAsync();
        else if (result == ContentDialogResult.Secondary) await ReturnToFolderAsync();
        else ApplyPlaybackState(true);
    }

    private static bool FocusPrimaryDialogButton(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Name: "PrimaryButton" } button && button.IsEnabled)
                return button.Focus(FocusState.Programmatic);
            if (FocusPrimaryDialogButton(child)) return true;
        }
        return false;
    }

    private async Task ShowInfoAsync()
    {
        if (!PlaybackInputAllowed || playbackEngine == null) return;
        gestures.Reset();
        var saved = playbackState.Suspend(CurrentPosition);
        ApplyPlaybackState();

        var duration = playbackEngine.Duration;
        var text = new TextBlock { Text = $"{Vm.FileName}\n\nファイル\n名前: {Vm.FileName}\nサイズ: {FormatSize(queue.Current?.Size ?? -1)}\n長さ: {(duration > TimeSpan.Zero ? duration.ToString(@"hh\:mm\:ss") : "取得できません")}\n再生位置: {saved.Position:hh\\:mm\\:ss}\n{queue.Index + 1} / {queue.Items.Count}\n\n映像・音声形式\n{Vm.MediaInfo}\n\n音声出力\n{DescribeAudioOutput()}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
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
        if (!PlaybackInputAllowed || playbackEngine == null) return;
        gestures.Reset();
        var target = playbackLocation;
        var selected = queue.Current?.Id;
        browserSuspension = playbackState.Suspend(CurrentPosition);
        ApplyPlaybackState();
        BrowserOverlay.Visibility = Visibility.Visible;
        Home.IsEnabled = false;
        SetPlaybackInputEnabled(false);
        browserHistory.Clear();
        await NavigateAsync(target ?? new("library", "保存済みフォルダー"), false, selected);
    }

    private void StopPlayback()
    {
        ClearAudioArtwork();
        gestures.Reset();
        mediaRequest++;
        mediaCancellation?.Cancel(); mediaCancellation?.Dispose(); mediaCancellation = null;
        recovery.Reset(); lastFailure = null;
        playbackProgress.Reset();
        playbackState.Stop();
        browserSuspension = null;
        pendingFailure = null;
        UpdateCursorAutoHide();
        ReleasePlayer();
        UpdateTransportState();
        interruptionMonitor?.TrackAudioOutput(null);
        if (displayActive) { displayRequest.RequestRelease(); displayActive = false; }
        Playback.Visibility = Visibility.Collapsed;
        PauseToolbar.Visibility = Visibility.Collapsed;
        Home.Visibility = Visibility.Visible;
        Vm.PlaybackStatus = "";
        UpdatePlaybackButtons();
        UpdateFullscreenButtons();
    }

    private void ReleasePlayer()
    {
        if (playbackEngine is { } old)
        {
            old.Ended -= Player_MediaEnded;
            old.Failed -= Player_MediaFailed;
            old.Opened -= Player_MediaOpened;
            old.StateChanged -= Player_PlaybackStateChanged;
            old.SeekCompleted -= Player_SeekCompleted;
            old.OutputChanged -= Player_OutputChanged;
            engineSession.Replace(null);
        }
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
