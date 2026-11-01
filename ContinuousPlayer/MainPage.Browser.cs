using ContinuousPlayer.Core;
using ContinuousPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation;
using Windows.System;

namespace ContinuousPlayer;

public sealed partial class MainPage
{
    private enum BrowserCloseReason { Cancel, SelectionCommitted, Home, WindowClosed }
    private sealed record Location(string Kind, string Name, string Path = "", DlnaServer? Server = null,
        List<FolderNode>? Trail = null, bool Register = false, SavedFolder? Saved = null);
    private Location location = new("library", "保存済みフォルダー");
    private Location? playbackLocation;
    private readonly Stack<(Location Location, string? Selected)> browserHistory = new();
    private CancellationTokenSource? browserCancellation;
    private Func<Task>? retry;
    private bool browserBusy;
    private readonly BrowserRequestState browserRequests = new();
    private string additionReturnId = "add";
    private string additionLocalOrigin = "sources";

    private async Task OpenLibraryAsync()
    {
        if (browserBusy || dialogOpen) return;
        BrowserOverlay.Visibility = Visibility.Visible;
        Home.IsEnabled = false;
        browserHistory.Clear();
        await NavigateAsync(new("library", "保存済みフォルダー"), false, library.LastFolderKey);
    }

    private async Task NavigateAsync(Location target, bool push = true, string? selected = null)
    {
        if (browserBusy || disposed) return;
        var request = browserRequests.Begin();
        manualConnectTarget = target.Kind == "servers" ? target : null;
        ManualConnectButton.Visibility = target.Kind == "servers" ? Visibility.Visible : Visibility.Collapsed;
        ReconnectButton.Visibility = Visibility.Collapsed;
        failedDlnaTarget = null;
        browserCancellation?.Dispose();
        browserCancellation = new CancellationTokenSource();
        var token = browserCancellation.Token;
        SetBrowserBusy(true);
        retry = () => NavigateAsync(target, push, selected);
        _ = ShowDelayedLoadingAsync(request, token);
        try
        {
            var rows = new List<BrowserRow>();
            switch (target.Kind)
            {
                case "library":
                    rows.AddRange(library.Folders.Select(x => new BrowserRow { Kind = "saved", Id = x.Key, Name = x.Name,
                        Detail = x.Kind == "dlna" ? $"NAS / DLNA · {x.ServerName}" : "ローカル / USB", Saved = x }));
                    rows.Add(new() { Kind = "add", Id = "add", Name = "フォルダーを追加" });
                    break;
                case "sources":
                    rows.Add(new() { Kind = "picker", Id = "picker", Name = "PC / ローカルフォルダー" });
                    rows.Add(new() { Kind = "drives", Id = "drives", Name = "USB / 外部ストレージ" });
                    rows.Add(new() { Kind = "network", Id = "network", Name = "NAS / DLNA" });
                    break;
                case "drives":
                    rows.AddRange(await Task.Run(() => DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType is DriveType.Removable or DriveType.Fixed)
                        .Select(x => new BrowserRow { Kind = "folder", Id = x.RootDirectory.FullName, Name = $"{x.Name} {x.VolumeLabel}" }).ToList(), token));
                    break;
                case "servers":
                    rows.AddRange((await dlna.DiscoverAsync(token)).Select(x => new BrowserRow { Kind = "server", Id = x.Id,
                        Name = x.Name, Detail = x.ControlUrl.Host, Server = x }));
                    break;
                case "local":
                    var contents = await LocalFolders.ReadAsync(target.Path, token);
                    rows.AddRange(contents.Folders.Select(x => new BrowserRow { Kind = "folder", Id = x.Id, Name = x.Name }));
                    if (!target.Register) rows.AddRange(contents.Media.Select(x => new BrowserRow { Kind = "media", Id = x.Id, Name = x.Name, Media = x }));
                    break;
                case "dlna":
                    if (target.Server == null && target.Saved != null)
                    {
                        var server = await dlna.ReconnectAsync(target.Saved, token);
                        target = target with { Server = server };
                    }
                    var entries = await dlna.BrowseAsync(target.Server ?? throw new IOException("サーバーが未選択です。"), target.Path, token);
                    if (target.Saved != null && target.Server.DescriptionUri is { } description && target.Saved.DescriptionUrl != description.AbsoluteUri)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!browserRequests.IsCurrent(request) || disposed) return;
                        library.UpdateServerEndpoint(target.Server.Id, description);
                        if (!await SaveBrowserLibraryAsync(request)) return;
                        target = target with { Saved = target.Saved with { DescriptionUrl = description.AbsoluteUri } };
                    }
                    rows.AddRange(MediaCatalog.SortFolders(entries.Where(x => x.Media == null), x => x.Name)
                        .Select(x => new BrowserRow { Kind = "folder", Id = x.Id, Name = x.Name }));
                    if (!target.Register) rows.AddRange(MediaCatalog.SortMedia(entries.Where(x => x.Media != null).Select(x => x.Media!))
                        .Select(x => new BrowserRow { Kind = "media", Id = x.Id, Name = x.Name, Media = x }));
                    break;
            }
            token.ThrowIfCancellationRequested();
            if (!browserRequests.IsCurrent(request) || disposed || BrowserOverlay.Visibility != Visibility.Visible) return;
            if (push) browserHistory.Push((location, (BrowserList.SelectedItem as BrowserRow)?.Id));
            location = target;
            BrowserError.IsOpen = false;
            Vm.BrowserTitle = target.Name;
            Vm.Rows.Clear();
            foreach (var row in PrepareDisplayRows(rows)) Vm.Rows.Add(row);
            Vm.EmptyMessage = rows.Count == 0 ? target.Kind == "servers" ? "メディアサーバーが見つかりません。\n同じLANへの接続とNASの設定を確認するか、「アドレスを指定して接続」を使用してください。"
                : target.Kind == "drives" ? "利用できるドライブはありません。"
                : target.Register ? "子フォルダーはありません。このフォルダーを登録できます。" : "このフォルダーに対応メディアはありません。" : "";
            RegisterButton.Visibility = target.Register && target.Kind is "local" or "dlna" ? Visibility.Visible : Visibility.Collapsed;
            BrowserHint.Text = target.Kind == "servers" ? "サーバーを選ぶか、「アドレスを指定して接続」から接続してください。"
                : target.Kind == "sources" ? "連続再生するフォルダーを登録します。PCの選択ではWindowsのフォルダー選択画面を開きます。" : target.Register
                ? "登録するフォルダーを開き、「このフォルダーを登録」を押してください。登録だけでは再生しません。"
                : target.Kind == "library" ? "フォルダーを開くとファイルを選べます。登録解除は行の右クリック・長押し・Deleteで行います。"
                : "ファイルを選ぶと、その位置から同じフォルダーを連続再生します。";
            var backLabel = target.Kind == "library" || target.Kind == "sources" ? "キャンセル"
                : !target.Register && string.Equals(target.Saved?.Path, target.Path,
                    target.Kind == "local" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? "保存済みフォルダー" : "上のフォルダー";
            AutomationProperties.SetName(BackButton, backLabel);
            ToolTipService.SetToolTip(BackButton, backLabel + " (Alt+←)");
            browserRequests.Complete(request);
            SetBrowserBusy(false);
            SelectRow(selected ?? LastSelection(target));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or System.Xml.XmlException or FormatException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            if (browserRequests.IsCurrent(request) && !disposed)
            {
                ShowBrowserError((target.Kind is "servers" or "dlna" ? FailureGuidance.Connection(ex) + "\n" : "") + ex.Message);
                if (target.Kind == "dlna" && target.Saved != null)
                {
                    failedDlnaTarget = target;
                    ReconnectButton.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            if (browserRequests.IsCurrent(request) && !disposed) { browserRequests.Complete(request); SetBrowserBusy(false); }
        }
    }

    private async Task ShowDelayedLoadingAsync(long request, CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token);
            if (!disposed && browserRequests.ShouldShowLoading(request) && BrowserOverlay.Visibility == Visibility.Visible)
            { LoadingRing.Visibility = Visibility.Visible; LoadingRing.IsActive = true; }
        }
        catch (OperationCanceledException) { }
    }

    private void SetBrowserBusy(bool busy)
    {
        browserBusy = busy;
        BrowserList.IsEnabled = !busy;
        RegisterButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        BackButton.IsEnabled = true; // During a load this cancels the whole selection.
        if (!busy) { LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed; }
    }

    private void SelectRow(string? id)
    {
        var row = Vm.Rows.FirstOrDefault(x => x.Id == id) ?? Vm.Rows.FirstOrDefault();
        BrowserList.SelectedItem = row;
        if (row == null) { BackButton.Focus(FocusState.Programmatic); return; }
        BrowserList.ScrollIntoView(row);
        BrowserList.UpdateLayout();
        if (BrowserList.ContainerFromItem(row) is ListViewItem item) item.Focus(FocusState.Programmatic);
        else BrowserList.Focus(FocusState.Programmatic);
    }

    private string? LastSelection(Location target)
    {
        var last = library.LastPlaybackFolder;
        if (last == null || last.Kind != target.Kind) return null;
        if (target.Kind == "local")
        {
            if (string.Equals(last.Path, target.Path, StringComparison.OrdinalIgnoreCase)) return library.LastMediaId;
            if (last.Path.StartsWith(target.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var relative = System.IO.Path.GetRelativePath(target.Path, last.Path).Split('\\')[0];
                return System.IO.Path.Combine(target.Path, relative);
            }
        }
        else if (target.Kind == "dlna" && target.Server?.Id == last.ServerId)
        {
            if (last.Path == target.Path) return library.LastMediaId;
            var index = last.Trail?.FindIndex(x => x.Id == target.Path) ?? -1;
            if (index >= 0 && index + 1 < last.Trail!.Count) return last.Trail[index + 1].Id;
        }
        return null;
    }

    private void ShowBrowserError(string message)
    {
        BrowserError.Message = message;
        BrowserError.IsOpen = true;
    }

    private async void Browser_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BrowserRow row) await ActivateRowAsync(row);
    }

    private async Task ActivateRowAsync(BrowserRow row)
    {
        if (browserBusy || dialogOpen || BrowserOverlay.Visibility != Visibility.Visible || !Vm.Rows.Contains(row)) return;
        if (row.Media != null) { if (!location.Register) await StartPlaybackAsync(row.Media); return; }
        switch (row.Kind)
        {
            case "add":
                additionReturnId = row.Id;
                browserHistory.Clear();
                await NavigateAsync(new("sources", "フォルダーを追加", Register: true), false); break;
            case "picker": await PickFolderAsync(); break;
            case "drives": additionLocalOrigin = "drives"; await NavigateAsync(new("drives", "USB / 外部ストレージ", Register: true)); break;
            case "network": await NavigateAsync(new("servers", "NAS / DLNA", Register: true)); break;
            case "server":
                await NavigateAsync(new("dlna", row.Name, "0", row.Server, [new("0", row.Name)], true)); break;
            case "saved":
                var saved = row.Saved!;
                var target = saved.Kind == "dlna"
                    ? new Location("dlna", saved.Name, saved.Path, Trail: saved.Trail?.ToList(), Saved: saved)
                    : new Location("local", saved.Name, saved.Path, Saved: saved);
                await NavigateAsync(target);
                break;
            case "folder":
                if (location.Kind == "dlna")
                    await NavigateAsync(location with { Path = row.Id, Name = row.Name, Trail = [.. location.Trail ?? [], new(row.Id, row.Name)] });
                else await NavigateAsync(location with { Kind = "local", Path = row.Id, Name = row.Name });
                break;
        }
    }

    private async Task PickFolderAsync()
    {
        if (browserBusy || dialogOpen) return;
        additionLocalOrigin = "sources";
        var request = browserRequests.Begin();
        SetBrowserBusy(true);
        _ = ShowDelayedLoadingAsync(request, CancellationToken.None);
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window.AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (!browserRequests.IsCurrent(request) || disposed || BrowserOverlay.Visibility != Visibility.Visible) return;
            browserRequests.Complete(request);
            SetBrowserBusy(false);
            if (folder != null) await NavigateAsync(new("local", FolderName(folder.Path), folder.Path, Register: true));
            else SelectRow("picker");
        }
        catch (Exception ex) { if (browserRequests.IsCurrent(request) && !disposed) ShowBrowserError("フォルダー選択を開けませんでした。" + ex.Message); }
        finally { if (browserRequests.IsCurrent(request) && !disposed) { browserRequests.Complete(request); SetBrowserBusy(false); } }
    }

    private static string FolderName(string path) => string.IsNullOrEmpty(System.IO.Path.GetFileName(path.TrimEnd('\\'))) ? path : System.IO.Path.GetFileName(path.TrimEnd('\\'));

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        if (browserBusy || dialogOpen || !location.Register || location.Kind is not ("local" or "dlna")) return;
        var request = browserRequests.Begin();
        SetBrowserBusy(true);
        var folder = location.Kind == "dlna"
            ? new SavedFolder("dlna", location.Name, location.Path, location.Server!.Id, location.Server.Name, location.Trail?.ToList(), location.Server.DescriptionUri?.AbsoluteUri)
            : new SavedFolder("local", location.Name, System.IO.Path.GetFullPath(location.Path));
        library.Register(folder);
        var saved = await SaveBrowserLibraryAsync(request);
        if (!browserRequests.IsCurrent(request) || disposed) return;
        browserRequests.Complete(request);
        SetBrowserBusy(false);
        if (!saved) return;
        browserHistory.Clear();
        await NavigateAsync(new("library", "保存済みフォルダー"), false, folder.Key);
    }

    private async Task BackAsync()
    {
        if (browserBusy) { CloseBrowser(); return; }
        if (location.Kind == "library") { CloseBrowser(); return; }
        if (location.Register)
        {
            browserHistory.Clear();
            if (location.Kind == "local" && Directory.GetParent(location.Path) is { } localParent)
                await NavigateAsync(location with { Path = localParent.FullName, Name = FolderName(localParent.FullName) }, false, location.Path);
            else if (location.Kind == "local" && additionLocalOrigin == "drives")
                await NavigateAsync(new("drives", "USB / 外部ストレージ", Register: true), false, location.Path);
            else if (location.Kind == "dlna" && location.Trail is { Count: > 1 } trail)
                await NavigateAsync(location with { Path = trail[^2].Id, Name = trail[^2].Name, Trail = trail.Take(trail.Count - 1).ToList() }, false, location.Path);
            else if (location.Kind == "dlna")
                await NavigateAsync(new("servers", "NAS / DLNA", Register: true), false, location.Server?.Id);
            else if (location.Kind is "drives" or "servers" or "local")
                await NavigateAsync(new("sources", "フォルダーを追加", Register: true), false,
                    location.Kind == "servers" ? "network" : location.Kind == "drives" ? "drives" : "picker");
            else await CancelBrowserAsync();
        }
        else if (location.Kind is "local" or "dlna")
        {
            var root = location.Saved;
            var parent = BrowserSelection.Parent(location.Kind, location.Path, location.Trail, root);
            browserHistory.Clear();
            if (parent == null) await NavigateAsync(new("library", "保存済みフォルダー"), false, root?.Key);
            else await NavigateAsync(location with { Path = parent.Path, Name = string.IsNullOrEmpty(parent.Name) ? parent.Path : parent.Name,
                Trail = parent.Trail?.ToList() }, false, parent.SelectedId);
        }
        else if (browserHistory.TryPeek(out var entry))
        {
            await NavigateAsync(entry.Location, false, entry.Selected);
            if (location == entry.Location) browserHistory.Pop();
            SetBrowserBusy(false);
        }
        else await CancelBrowserAsync();
    }

    private async Task CancelBrowserAsync()
    {
        if (browserBusy) { CloseBrowser(); return; }
        if (location.Register)
        {
            browserHistory.Clear();
            await NavigateAsync(new("library", "保存済みフォルダー"), false, additionReturnId);
        }
        else CloseBrowser();
    }

    private void CloseBrowser(BrowserCloseReason reason = BrowserCloseReason.Cancel)
    {
        browserRequests.Cancel();
        browserCancellation?.Cancel();
        SetBrowserBusy(false);
        BrowserOverlay.Visibility = Visibility.Collapsed;
        Home.IsEnabled = true;
        SetPlaybackInputEnabled(true);
        var saved = browserSuspension;
        browserSuspension = null;
        if (reason == BrowserCloseReason.Cancel)
        {
            RestoreSuspension(saved);
            _ = ShowPendingFailureAsync();
        }
        if (!isPlayback) ChooseButton.Focus(FocusState.Programmatic);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await CancelBrowserAsync();
    private async void Back_Click(object sender, RoutedEventArgs e) => await BackAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await NavigateAsync(location, false, (BrowserList.SelectedItem as BrowserRow)?.Id);
    private async void Retry_Click(object sender, RoutedEventArgs e) { if (!browserBusy && retry != null) await retry(); }

    private async Task HandleBrowserKeyAsync(KeyRoutedEventArgs e, DependencyObject? focused)
    {
        if (dialogOpen || e.Handled) return;
        var alt = IsKeyDown(VirtualKey.Menu);
        if (alt && e.Key != VirtualKey.Left) return;
        var listFocus = IsWithin(focused, BrowserList);
        if (!alt && !listFocus && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            var direction = e.Key switch
            {
                VirtualKey.Left => FocusNavigationDirection.Left, VirtualKey.Right => FocusNavigationDirection.Right,
                VirtualKey.Up => FocusNavigationDirection.Up, _ => FocusNavigationDirection.Down
            };
            FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = BrowserOverlay });
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.PageDown or VirtualKey.PageUp && !listFocus) return;
        if (e.Key is not (VirtualKey.Escape or VirtualKey.GoBack or VirtualKey.Back or VirtualKey.Left or
            VirtualKey.Right or VirtualKey.PageDown or VirtualKey.PageUp or VirtualKey.F5 or VirtualKey.Delete)) return;
        e.Handled = true; // Consume before any asynchronous navigation/dialog work.
        switch (e.Key)
        {
            case VirtualKey.Escape: await CancelBrowserAsync(); break;
            case VirtualKey.GoBack:
            case VirtualKey.Back: await BackAsync(); break;
            case VirtualKey.Left:
                if (alt) await BackAsync();
                else if (IsWithin(focused, BrowserList)) PageBrowser(-1);
                else return;
                break;
            case VirtualKey.Right:
            case VirtualKey.PageDown:
            case VirtualKey.PageUp:
                if (!IsWithin(focused, BrowserList)) return;
                PageBrowser(e.Key == VirtualKey.PageUp ? -1 : 1); break;
            case VirtualKey.F5:
                if (!browserBusy) await NavigateAsync(location, false, (BrowserList.SelectedItem as BrowserRow)?.Id); break;
            case VirtualKey.Delete: await RemoveSelectedAsync(); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void Browser_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        await RemoveAtAsync(e.OriginalSource as FrameworkElement);
    }
    private async void Browser_Holding(object sender, HoldingRoutedEventArgs e)
    {
        e.Handled = true;
        if (e.HoldingState == Microsoft.UI.Input.HoldingState.Started) await RemoveAtAsync(e.OriginalSource as FrameworkElement);
    }
    private async Task RemoveAtAsync(FrameworkElement? element)
    {
        while (element != null && element != BrowserList)
        {
            if (element.DataContext is BrowserRow row)
            {
                if (row.Saved != null) { BrowserList.SelectedItem = row; await RemoveSelectedAsync(); }
                return;
            }
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
        }
    }

    private async Task RemoveSelectedAsync()
    {
        if (browserBusy || dialogOpen || location.Kind != "library" || BrowserList.SelectedItem is not BrowserRow { Saved: not null } row) return;
        dialogOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "フォルダーの登録解除",
                Content = $"「{row.Name}」の登録を解除します。\nフォルダーやファイル自体は削除されません。", PrimaryButtonText = "登録解除", CloseButtonText = "閉じる", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (disposed || BrowserOverlay.Visibility != Visibility.Visible) return;
                var keys = library.Folders.Select(x => x.Key).ToArray();
                var next = BrowserSelection.SelectionAfterRemoval(keys, Array.IndexOf(keys, row.Saved.Key));
                library.Remove(row.Saved.Key);
                var request = browserRequests.Begin();
                SetBrowserBusy(true);
                var saved = await SaveBrowserLibraryAsync(request);
                if (!browserRequests.IsCurrent(request) || disposed) return;
                browserRequests.Complete(request);
                SetBrowserBusy(false);
                if (!saved) return;
                await NavigateAsync(new("library", "保存済みフォルダー"), false, next);
            }
        }
        finally { dialogOpen = false; }
    }

    private static IEnumerable<BrowserRow> PrepareDisplayRows(List<BrowserRow> rows)
    {
        var names = MediaDisplayNames.Create(rows.Where(x => x.Media != null).Select(x => x.Media!),
            rows.Where(x => x.Media == null).Select(x => x.Name)).ToDictionary(x => x.Id);
        return rows.Select(row => row.Media != null && names.TryGetValue(row.Id, out var name)
            ? new BrowserRow { Kind = row.Kind, Id = row.Id, Name = row.Name, Detail = row.Detail, Media = row.Media,
                DisplayName = name.Title, FormatBadge = name.FormatBadge } : row);
    }

    private async Task<bool> SaveBrowserLibraryAsync(long request)
    {
        try { await library.SaveAsync(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (browserRequests.IsCurrent(request) && !disposed && BrowserOverlay.Visibility == Visibility.Visible)
                ShowBrowserError("保存に失敗しました。" + ex.Message);
            return false;
        }
    }

    private void Browser_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not BrowserRow row) return;
        AutomationProperties.SetName(args.ItemContainer, row.AccessibleName);
        AutomationProperties.SetAutomationId(args.ItemContainer, "BrowserItem:" + row.Id);
        ToolTipService.SetToolTip(args.ItemContainer, row.Name);
    }

    public static Visibility BadgeVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
