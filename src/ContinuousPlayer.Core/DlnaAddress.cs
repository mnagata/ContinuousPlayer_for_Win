namespace ContinuousPlayer.Core;

public static class DlnaAddress
{
    public static Uri Parse(string input)
    {
        var text = input.Trim();
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) throw new FormatException("ホスト名・IPアドレス、またはデバイス記述URLを入力してください。");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !IsHttpTarget(uri))
            throw new FormatException("HTTP/HTTPSのアドレスを入力してください。ユーザー名・パスワード・フラグメントは指定できません。");
        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        var explicitPort = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        if (explicitPort)
        {
            var port = authority[(authority.LastIndexOf(':') + 1)..];
            if (port.Length == 0 || !port.All(char.IsAsciiDigit) || !int.TryParse(port, out var number) || number is < 1 or > 65535)
                throw new FormatException("ポート番号は1〜65535で指定してください。");
        }
        var builder = new UriBuilder(uri);
        if (builder.Path is "" or "/")
        {
            if (builder.Query.Length != 0) throw new FormatException("クエリを含む場合は完全なデバイス記述URLを入力してください。");
            if (!explicitPort) builder.Port = 50001;
            builder.Path = "/desc/device.xml";
        }
        return builder.Uri;
    }

    public static bool IsHttpTarget(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" &&
        !string.IsNullOrWhiteSpace(uri.Host) && uri.Port is >= 1 and <= 65535 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}
