using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private Location? manualConnectTarget, failedDlnaTarget;
    private ContentDialog? activeConnectionDialog;
    private string lastManualAddress = "";
    private async void ManualConnect_Click(object sender, RoutedEventArgs e) => await ConnectManuallyAsync();
    private async void Reconnect_Click(object sender, RoutedEventArgs e) => await ConnectManuallyAsync(failedDlnaTarget);

    private async Task ConnectManuallyAsync(Location? reconnect = null)
    {
        if (disposed || dialogOpen || BrowserOverlay.Visibility != Visibility.Visible) return;
        var origin = manualConnectTarget ?? new("servers", "NAS / DLNA", Register: true);
        browserRequests.Cancel();
        browserCancellation?.Cancel();
        SetBrowserBusy(false);
        var request = browserRequests.Begin();
        using var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var input = new TextBox { Header = "サーバーのアドレス", Text = reconnect?.Saved?.DescriptionUrl ?? lastManualAddress,
            PlaceholderText = "192.168.1.10 または http://nas:50001/desc/device.xml" };
        AutomationProperties.SetAutomationId(input, "DlnaAddress");
        AutomationProperties.SetName(input, "サーバーのアドレス");
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        AutomationProperties.SetAutomationId(error, "DlnaConnectError");
        var progress = new ProgressRing { Width = 32, Height = 32, IsActive = false, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(progress, "DlnaConnecting");
        AutomationProperties.SetName(progress, "接続中");
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "ホスト名・IPアドレス（ポート指定可）、またはHTTP/HTTPSのデバイス記述URLを入力してください。パス省略時は /desc/device.xml（ポートも省略時は50001）を使います。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input); content.Children.Add(progress); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark,
            Title = reconnect == null ? "アドレスを指定して接続" : "接続先を再指定", Content = content,
            PrimaryButtonText = "接続", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
        DlnaServer? connected = null;
        var connecting = false;
        bool Current() => !disposed && !token.IsCancellationRequested && browserRequests.IsCurrent(request);
        async Task ConnectAsync()
        {
            if (connecting || !Current()) return;
            connecting = true; dialog.IsPrimaryButtonEnabled = false; input.IsEnabled = false;
            error.IsOpen = false; progress.IsActive = true; progress.Visibility = Visibility.Visible;
            try
            {
                var server = await dlna.ConnectAsync(DlnaAddress.Parse(input.Text), token);
                if (!Current()) return;
                if (reconnect?.Saved is { } saved && server.Id != saved.ServerId)
                    throw new InvalidDataException("登録先とサーバーIDが異なります。元のNASを指定するか、別のフォルダーとして追加してください。");
                connected = server;
                dialog.Hide();
            }
            catch (Exception ex)
            {
                if (!Current()) return;
                var guidance = FailureGuidance.Connection(ex);
                error.Message = guidance == ex.Message ? guidance : guidance + "\n" + ex.Message;
                error.IsOpen = true; dialog.PrimaryButtonText = "再試行";
            }
            finally
            {
                connecting = false;
                if (Current())
                {
                    progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
                    input.IsEnabled = true; dialog.IsPrimaryButtonEnabled = true; input.Focus(FocusState.Programmatic);
                }
            }
        }
        // No async button deferral: Cancel/Escape must remain usable while connecting.
        dialog.PrimaryButtonClick += (sender, args) => { args.Cancel = true; _ = ConnectAsync(); };
        dialog.Closing += (_, _) => lifetime.Cancel();
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        dialogOpen = true; activeConnectionDialog = dialog; UpdateCursorAutoHide();
        try { await dialog.ShowAsync(); }
        finally
        {
            lifetime.Cancel(); lastManualAddress = input.Text;
            activeConnectionDialog = null; dialogOpen = false;
            if (browserRequests.IsCurrent(request) && !disposed) { browserRequests.Complete(request); SetBrowserBusy(false); }
        }
        if (disposed || !browserRequests.IsCurrent(request) || BrowserOverlay.Visibility != Visibility.Visible) return;
        if (connected == null)
        {
            if (reconnect == null) await NavigateAsync(origin, false);
            return;
        }
        if (reconnect?.Saved is { } registered)
        {
            var save = browserRequests.Begin(); SetBrowserBusy(true);
            library.UpdateServerEndpoint(connected.Id, connected.DescriptionUri!);
            var persisted = await SaveBrowserLibraryAsync(save);
            if (!browserRequests.IsCurrent(save) || disposed) return;
            browserRequests.Complete(save); SetBrowserBusy(false);
            if (!persisted) return;
            var updated = registered with { DescriptionUrl = connected.DescriptionUri!.AbsoluteUri };
            if (playbackLocation?.Saved?.ServerId == connected.Id)
                playbackLocation = playbackLocation with { Saved = playbackLocation.Saved with { DescriptionUrl = updated.DescriptionUrl }, Server = connected };
            await NavigateAsync(reconnect with { Saved = updated, Server = connected }, false);
        }
        else await NavigateAsync(new("dlna", connected.Name, "0", connected, [new("0", connected.Name)], true), false);
    }
}
