namespace ContinuousPlayer.Core;

/// <summary>Tracks only engine-confirmed positions for the current file; never persists media URLs.</summary>
public sealed class PlaybackRecovery
{
    private string? fileId;
    public TimeSpan Position { get; private set; }
    public void Begin(string id, bool retry)
    {
        if (!retry || fileId != id) Position = TimeSpan.Zero;
        fileId = id;
    }
    public void Observe(string id, TimeSpan position)
    {
        if (id == fileId && position >= TimeSpan.Zero) Position = position;
    }
    public TimeSpan RestoreTarget(TimeSpan duration) => duration > TimeSpan.Zero && Position >= duration
        ? TimeSpan.FromTicks(Math.Max(0, duration.Ticks - TimeSpan.FromMilliseconds(10).Ticks)) : Position;
    public void Reset() { fileId = null; Position = TimeSpan.Zero; }
}

/// <summary>Bounds an engine's silent buffering/reconnect wait without treating brief buffering as pause.</summary>
public sealed class PlaybackProgress
{
    private TimeSpan? lastPosition;
    private TimeSpan lastProgress;
    private bool watching;
    public void Reset() { lastPosition = null; watching = false; }
    public bool TimedOut(TimeSpan position, TimeSpan now, bool expectedToAdvance)
    {
        if (!expectedToAdvance) { Reset(); return false; }
        if (!watching || lastPosition != position)
        {
            watching = true; lastPosition = position; lastProgress = now;
        }
        return now - lastProgress >= TimeSpan.FromSeconds(30);
    }
}

public static class FailureGuidance
{
    public static string Playback(bool network, int code) => code is unchecked((int)0x80070005) or unchecked((int)0x80070020)
        ? "ファイルのアクセス許可や、他のアプリで使用中でないか確認してください。"
        : network ? "NASとネットワークの接続、公開フォルダーとアクセス許可を確認してください。再試行では接続先を確認して開き直します。"
        : "ドライブの接続、ファイルの場所とアクセス許可、Windowsで再生できる形式かを確認してください。";

    public static string Connection(Exception error) => error switch
    {
        FormatException => error.Message,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } =>
            "NASがアクセスを拒否しました。メディアサーバー側のアクセス許可を確認してください。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "デバイス記述が見つかりません。ポート番号とURLのパスを確認してください。",
        OperationCanceledException => "接続がタイムアウトしました。アドレスとネットワークを確認して再試行してください。",
        System.Xml.XmlException or InvalidDataException => "DLNAサーバーの情報を読み取れません。デバイス記述URLを確認してください。",
        _ => "接続できませんでした。NASのアドレス、ネットワーク、メディアサーバーの起動状態を確認して再試行してください。"
    };
}
