using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ContinuousPlayer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace ContinuousPlayer.Interop;

/// <summary>Reports real OS interruptions independently of input activation.</summary>
internal sealed class PlaybackInterruptionMonitor : IDisposable
{
    private const nuint SubclassId = 0x43504C59;
    private readonly Window window;
    private readonly DispatcherQueue dispatcher;
    private readonly Action<PlaybackInterruption, bool> changed;
    private readonly SubclassProc callback;
    private readonly nint hwnd;
    private DeviceWatcher? audioWatcher;
    private PlaybackInterruption reported;
    private bool disposed;
    private bool subclassed;
    private bool sessionRegistered;
    private bool trackingAudio;
    private bool usesDefaultOutput = true;
    private string? explicitAudioId;
    private string? outputId;
    private int audioGeneration;
    private bool audioReady;
    private string audioSelector = "";
    private string defaultOutputId = "";

    public PlaybackInterruptionMonitor(Window window, Action<PlaybackInterruption, bool> changed)
    {
        this.window = window;
        this.changed = changed;
        dispatcher = window.DispatcherQueue;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback = WindowMessage;
        try
        {
            if (!SetWindowSubclass(hwnd, callback, SubclassId, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ウィンドウの中断監視を開始できません。");
            subclassed = true;
            if (!WTSRegisterSessionNotification(hwnd, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ロックの監視を開始できません。");
            sessionRegistered = true;
            window.AppWindow.Changed += WindowChanged;
            EvaluateWindow();
        }
        catch { Dispose(); throw; }
    }

    public async Task InitializeAudioAsync()
    {
        // Activating the MediaDevice WinRT factory may pump COM messages. Doing
        // this inside XAML's Loaded callback can re-enter WinUI and fail fast.
        var initialized = await Task.Run(() =>
        {
            var selector = MediaDevice.GetAudioRenderSelector();
            var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            var watcher = DeviceInformation.CreateWatcher(selector);
            try
            {
                watcher.Added += AudioAdded;
                watcher.Removed += AudioRemoved;
                watcher.Updated += AudioUpdated;
                MediaDevice.DefaultAudioRenderDeviceChanged += DefaultOutputChanged;
                watcher.Start();
                return (Watcher: watcher, Selector: selector, Default: defaultId);
            }
            catch { ReleaseAudioWatcher(watcher); throw; }
        });
        if (disposed) { ReleaseAudioWatcher(initialized.Watcher); return; }
        audioWatcher = initialized.Watcher;
        audioSelector = initialized.Selector;
        defaultOutputId = initialized.Default;
        audioReady = true;
        RefreshTrackedOutput();
    }

    public void TrackAudioOutput(PlaybackOutputInfo? output)
    {
        var nextExplicit = output?.UsesDefaultDevice == false ? output.DeviceId : null;
        if (trackingAudio == (output != null) && explicitAudioId == nextExplicit &&
            usesDefaultOutput == (output?.UsesDefaultDevice == true) &&
            (output == null || output.DeviceId == null || SameDevice(outputId, output.DeviceId))) return;
        trackingAudio = output != null;
        explicitAudioId = nextExplicit;
        usesDefaultOutput = output?.UsesDefaultDevice == true;
        outputId = output?.DeviceId;
        RefreshTrackedOutput();
    }

    private void RefreshTrackedOutput()
    {
        audioGeneration++;
        outputId = trackingAudio ? explicitAudioId ?? outputId ?? (usesDefaultOutput ? defaultOutputId : null) : null;
        if (!trackingAudio) Report(PlaybackInterruption.AudioOutputUnavailable, false);
        else if (audioReady) _ = RefreshAudioAsync();
    }

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => EvaluateWindow();
    private void EvaluateWindow()
    {
        if (disposed) return;
        Report(PlaybackInterruption.Minimized, IsIconic(hwnd));
        Report(PlaybackInterruption.Hidden, !IsWindowVisible(hwnd));
    }

    private void Report(PlaybackInterruption reason, bool active)
    {
        if (disposed || reported.HasFlag(reason) == active) return;
        reported = active ? reported | reason : reported & ~reason;
        // Native window callbacks may run inside WinUI layout/dispatch. Apply UI
        // changes only after that dispatch has returned, preserving event order.
        dispatcher.TryEnqueue(() => { if (!disposed) changed(reason, active); });
    }

    private nint WindowMessage(nint handle, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Never let a managed exception cross the native callback boundary.
        try
        {
            if (!disposed)
            {
                switch (message)
                {
                    case 0x0005: // WM_SIZE: includes minimization in all presenter modes.
                    case 0x0018: // WM_SHOWWINDOW
                        EvaluateWindow(); break;
                    case 0x02B1: // WM_WTSSESSION_CHANGE
                        if (wParam == 7 || wParam == 8) Report(PlaybackInterruption.Locked, wParam == 7);
                        if (wParam is 1 or 2 or 3 or 4)
                            Report(PlaybackInterruption.SessionUnavailable, wParam is 2 or 4);
                        break;
                    case 0x0218: // WM_POWERBROADCAST
                        if (wParam == 4) Report(PlaybackInterruption.Suspended, true);
                        else if (wParam is 6 or 7 or 0x12) Report(PlaybackInterruption.Suspended, false);
                        break;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private void AudioRemoved(DeviceWatcher sender, DeviceInformationUpdate device)
        => dispatcher.TryEnqueue(() =>
        {
            if (disposed || !trackingAudio || !SameDevice(device.Id, outputId)) return;
            Report(PlaybackInterruption.AudioOutputUnavailable, true);
            _ = RefreshAudioAsync();
        });

    private void AudioAdded(DeviceWatcher sender, DeviceInformation device)
        => dispatcher.TryEnqueue(() => { if (!disposed) _ = RefreshAudioAsync(); });

    private void AudioUpdated(DeviceWatcher sender, DeviceInformationUpdate device)
        => dispatcher.TryEnqueue(() => { if (!disposed) _ = RefreshAudioAsync(); });

    private void DefaultOutputChanged(object? sender, DefaultAudioRenderDeviceChangedEventArgs args)
        => dispatcher.TryEnqueue(() =>
        {
            if (disposed || args.Role != AudioDeviceRole.Default) return;
            if (!trackingAudio) defaultOutputId = args.Id;
            else _ = RefreshAudioAsync();
        });

    private async Task RefreshAudioAsync()
    {
        if (disposed || !audioReady || !trackingAudio) return;
        var generation = ++audioGeneration;
        var previous = outputId;
        try
        {
            // Keep the old output identity until enumeration determines whether it
            // was removed. Default-change and watcher notifications can race.
            var refreshed = await Task.Run(async () => (
                Devices: await DeviceInformation.FindAllAsync(audioSelector),
                Default: MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)));
            if (disposed || !trackingAudio || generation != audioGeneration) return;
            var devices = refreshed.Devices;
            defaultOutputId = refreshed.Default;
            if (usesDefaultOutput && !string.IsNullOrEmpty(previous) && !SameDevice(previous, defaultOutputId))
                Report(PlaybackInterruption.AudioOutputUnavailable, true);
            if (!string.IsNullOrEmpty(previous) && !devices.Any(x => SameDevice(x.Id, previous) && x.IsEnabled))
                Report(PlaybackInterruption.AudioOutputUnavailable, true);
            outputId = explicitAudioId ?? (usesDefaultOutput ? defaultOutputId : null);
            Report(PlaybackInterruption.AudioOutputUnavailable,
                string.IsNullOrEmpty(outputId) || !devices.Any(x => SameDevice(x.Id, outputId) && x.IsEnabled));
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private static bool SameDevice(string? first, string? second) =>
        ExclusiveAudioPolicy.SameEndpoint(first, second);

    private void ReleaseAudioWatcher(DeviceWatcher watcher)
    {
        MediaDevice.DefaultAudioRenderDeviceChanged -= DefaultOutputChanged;
        watcher.Added -= AudioAdded;
        watcher.Removed -= AudioRemoved;
        watcher.Updated -= AudioUpdated;
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            watcher.Stop();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        audioGeneration++;
        window.AppWindow.Changed -= WindowChanged;
        if (audioWatcher != null)
        {
            ReleaseAudioWatcher(audioWatcher);
            audioWatcher = null;
        }
        if (sessionRegistered) WTSUnRegisterSessionNotification(hwnd);
        if (subclassed) RemoveWindowSubclass(hwnd, callback, SubclassId);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
}
