using ContinuousPlayer.Audio.Windows;
using ContinuousPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ContinuousPlayer;

public sealed partial class AudioSettingsDialog : ContentDialog
{
    private readonly Func<AudioOutputSettings, Task> save;
    private bool closed;
    private bool refreshing;
    private bool saving;
    public AudioOutputSettings Settings { get; private set; }

    public AudioSettingsDialog(AudioOutputSettings settings, Func<AudioOutputSettings, Task> save)
    {
        this.save = save;
        Settings = settings;
        InitializeComponent();
        ModeSelection.SelectedIndex = settings.Exclusive ? 1 : 0;
        SetDevices([]);
    }

    private void SetDevices(IReadOnlyList<AudioEndpoint> endpoints)
    {
        var previous = (DeviceSelection.SelectedItem as ComboBoxItem)?.Tag as AudioEndpoint;
        var selectedId = previous?.Id ?? Settings.DeviceId;
        if (DeviceSelection.SelectedItem is ComboBoxItem { Tag: null }) selectedId = null;
        DeviceSelection.Items.Clear();
        DeviceSelection.Items.Add(new ComboBoxItem { Content = "Windows既定の出力機器" });
        foreach (var endpoint in endpoints)
            DeviceSelection.Items.Add(new ComboBoxItem { Content = endpoint.Name, Tag = endpoint });
        if (selectedId != null && !endpoints.Any(x => x.Id == selectedId))
            DeviceSelection.Items.Add(new ComboBoxItem { Content = $"{previous?.Name ?? Settings.DeviceName ?? "保存した出力機器"}（未接続）",
                Tag = new AudioEndpoint(selectedId, previous?.Name ?? Settings.DeviceName ?? "保存した出力機器") });
        DeviceSelection.SelectedIndex = 0;
        for (var index = 1; index < DeviceSelection.Items.Count; index++)
            if (((ComboBoxItem)DeviceSelection.Items[index]).Tag is AudioEndpoint item && item.Id == selectedId)
                DeviceSelection.SelectedIndex = index;
    }

    private async void Dialog_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => closed = true;
    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args) { if (saving) args.Cancel = true; }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelection != null) DeviceSelection.IsEnabled = ModeSelection.SelectedIndex == 1 && !refreshing;
    }

    private async Task RefreshAsync()
    {
        if (refreshing || closed) return;
        refreshing = true; RefreshButton.IsEnabled = false; DeviceSelection.IsEnabled = false; IsPrimaryButtonEnabled = false;
        SettingsMessage.IsOpen = false; SettingsMessage.Visibility = Visibility.Collapsed;
        try { var devices = await Task.Run(AudioDevices.Enumerate); if (!closed) SetDevices(devices); }
        catch (Exception e)
        {
            if (!closed) { SettingsMessage.Message = "機器を取得できませんでした。保存した選択は保持しています。\n" + e.Message; SettingsMessage.IsOpen = true; SettingsMessage.Visibility = Visibility.Visible; }
        }
        finally
        {
            refreshing = false;
            if (!closed) { RefreshButton.IsEnabled = true; IsPrimaryButtonEnabled = true; Mode_Changed(this, null!); }
        }
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        saving = true; ModeSelection.IsEnabled = false; DeviceSelection.IsEnabled = false;
        IsPrimaryButtonEnabled = false; RefreshButton.IsEnabled = false;
        try
        {
            var endpoint = (DeviceSelection.SelectedItem as ComboBoxItem)?.Tag as AudioEndpoint;
            var candidate = new AudioOutputSettings(ModeSelection.SelectedIndex == 1, endpoint?.Id, endpoint?.Name);
            await save(candidate);
            Settings = candidate;
        }
        catch (Exception e)
        {
            args.Cancel = true;
            SettingsMessage.Message = "設定を保存できませんでした。\n" + e.Message;
            SettingsMessage.IsOpen = true;
            SettingsMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            saving = false; IsPrimaryButtonEnabled = true; RefreshButton.IsEnabled = true;
            ModeSelection.IsEnabled = true; Mode_Changed(this, null!); deferral.Complete();
        }
    }
}
