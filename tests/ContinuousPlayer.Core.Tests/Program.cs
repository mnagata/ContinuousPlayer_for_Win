using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ContinuousPlayer.Core;

var failed = 0;
var passed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex}"); failed++; }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
void True(bool value) { if (!value) throw new Exception("Expected true"); }
Task Check(Action action) { action(); return Task.CompletedTask; }
MediaFile Media(string name) => new(name, name, name);
var fixture = Path.Combine(Path.GetTempPath(), "ContinuousPlayerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    await Test("OP/ED numeric order, mixed case, omitted number", () => Check(() =>
    {
        var names = new[] { "作品 ED2.mp4", "作品 OP10.mp4", "作品 ED.mp4", "作品 op2.mp4", "作品 OP.mp4" };
        Equal("作品 OP.mp4|作品 ED.mp4|作品 op2.mp4|作品 ED2.mp4|作品 OP10.mp4", string.Join('|', MediaCatalog.SortMedia(names.Select(Media)).Select(x => x.Name)));
    }));
    await Test("Non OP/ED files keep their original sorted positions", () => Check(() =>
    {
        var names = new[] { "Series ED.mp4", "Series EXTRA.mp4", "Series OP.mp4", "Other.mp4" };
        var baseline = names.OrderBy(x => x, StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true)).ToList();
        var sorted = MediaCatalog.SortMedia(names.Select(Media)).Select(x => x.Name).ToList();
        Equal(baseline.IndexOf("Series EXTRA.mp4"), sorted.IndexOf("Series EXTRA.mp4"));
        Equal(baseline.IndexOf("Other.mp4"), sorted.IndexOf("Other.mp4"));
    }));
    await Test("Extension whitelist excludes AppleDouble only", () => Check(() =>
    {
        True(MediaCatalog.IsSupported("hidden.MP4")); True(MediaCatalog.IsSupported(".hidden.flac"));
        True(!MediaCatalog.IsSupported("._作品 OP.mp4")); True(!MediaCatalog.IsSupported("video.mkv"));
        foreach (var ext in new[] { "mp4", "m4v", "mp3", "flac", "m4a", "aac", "wav", "ogg", "opus" }) True(MediaCatalog.IsSupported("test." + ext));
    }));
    await Test("Season series preserve unrelated folders", () => Check(() =>
    {
        var names = new[] { "アニメ2025秋", "別フォルダー", "アニメ2025春", "アニメ2024冬", "アニメ2025夏", "アニメ2025冬" };
        var sorted = MediaCatalog.SortFolders(names, x => x);
        Equal("アニメ2024冬|アニメ2025冬|アニメ2025春|アニメ2025夏|アニメ2025秋", string.Join('|', sorted.Where(x => x.StartsWith("アニメ"))));
        var baseIndex = names.OrderBy(x => x, StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true)).ToList().IndexOf("別フォルダー");
        Equal(baseIndex, sorted.IndexOf("別フォルダー"));
    }));
    await Test("Nonrecursive local scan and folder/media separation", async () =>
    {
        var root = Path.Combine(fixture, "scan"); Directory.CreateDirectory(Path.Combine(root, "child"));
        foreach (var name in new[] { "作品 ED.mp4", "作品 OP.mp4", "._作品 OP.mp4", "readme.txt" }) await File.WriteAllTextAsync(Path.Combine(root, name), "fixture");
        await File.WriteAllTextAsync(Path.Combine(root, "child", "nested.mp4"), "fixture");
        var result = await LocalFolders.ReadAsync(root, default);
        Equal(1, result.Folders.Count); Equal(2, result.Media.Count); Equal("作品 OP.mp4", result.Media[0].Name);
    });
    await Test("Missing folder is an error, not an empty list", async () =>
    {
        try { await LocalFolders.ReadAsync(Path.Combine(fixture, "missing"), default); throw new Exception("Expected failure"); }
        catch (DirectoryNotFoundException) { }
    });
    await Test("Queue preserves selected start, boundaries, and seek semantics", () => Check(() =>
    {
        var queue = new PlaybackQueue(); queue.Start(new[] { Media("a"), Media("b"), Media("c") }, 1);
        Equal("b", queue.Current!.Name); True(queue.Move(1)); True(!queue.Move(1)); Equal(2, queue.Index);
        Equal(TimeSpan.Zero, queue.Seek(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), -10).Position);
        var atEnd = queue.Seek(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30), 10);
        True(!atEnd.Next); Equal(TimeSpan.FromSeconds(25), atEnd.Position);
        True(queue.Move(-1)); True(queue.Seek(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30), 10).Next);
    }));
    await Test("Saved folders update without duplicates and survive offline removal", async () =>
    {
        var dir = Path.Combine(fixture, "library"); var store = new LibraryStore(dir);
        store.Register(new("local", "first", fixture)); store.Register(new("local", "updated", fixture));
        store.Register(new("dlna", "NAS", "12", "uuid:nas", "NAS", [new("0", "root"), new("12", "NAS")]));
        store.LastFolderKey = store.Folders[0].Key; store.LastMediaId = "id"; store.LastPlaybackFolder = store.Folders[0]; await store.SaveAsync();
        var restored = new LibraryStore(dir); await restored.LoadAsync(); Equal(2, restored.Folders.Count);
        Equal("updated", restored.Folders[0].Name); Equal("id", restored.LastMediaId); Equal(fixture, restored.LastPlaybackFolder!.Path);
        restored.Remove(restored.Folders[1].Key); await restored.SaveAsync(); Equal(1, restored.Folders.Count);
    });
    await Test("Corrupt library remains intact and produces a warning", async () =>
    {
        var dir = Path.Combine(fixture, "broken"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "library.json"), "{broken");
        var store = new LibraryStore(dir); await store.LoadAsync(); True(store.LoadWarning != null);
        Equal("{broken", await File.ReadAllTextAsync(Path.Combine(dir, "library.json")));
    });
    await Test("Null and invalid library entries are ignored", async () =>
    {
        var dir = Path.Combine(fixture, "nulls"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "library.json"), "{\"Version\":1,\"Folders\":[null,{\"Kind\":\"local\",\"Name\":\"invalid\",\"Path\":\"relative\"}]}");
        var store = new LibraryStore(dir); await store.LoadAsync(); Equal(0, store.Folders.Count);
    });
    await Test("Overlapping saves serialize without corrupting the library", async () =>
    {
        var dir = Path.Combine(fixture, "concurrent"); var store = new LibraryStore(dir);
        store.Register(new("local", "folder", fixture));
        var writes = new List<Task>();
        for (var i = 0; i < 16; i++) { store.LastMediaId = i.ToString(); writes.Add(store.SaveAsync()); }
        await Task.WhenAll(writes);
        var restored = new LibraryStore(dir); await restored.LoadAsync(); Equal("15", restored.LastMediaId); Equal(1, restored.Folders.Count);
    });
    await Test("Local directory scan honors cancellation", async () =>
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await LocalFolders.ReadAsync(fixture, cancellation.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    });
    await Test("SSDP headers are case insensitive and reject non HTTP locations", () => Check(() =>
    {
        Equal("http://127.0.0.1/device.xml", DlnaProtocol.Location("HTTP/1.1 200 OK\r\nlOcAtIoN: http://127.0.0.1/device.xml\r\n"));
        True(DlnaProtocol.Location("HTTP/1.1 200 OK\nLOCATION: file:///C:/test") == null);
        True(DlnaProtocol.Location("NOTIFY * HTTP/1.1\nLOCATION: http://localhost/") == null);
    }));
    await Test("Device description resolves relative URLs and stable UDN", () => Check(() =>
    {
        var server = DlnaProtocol.Server("<root><device><UDN>uuid:nas</UDN><friendlyName>NAS</friendlyName><serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><controlURL>/browse</controlURL></service></serviceList></device></root>", new Uri("http://localhost/device.xml"));
        Equal("uuid:nas", server.Id); Equal("http://localhost/browse", server.ControlUrl.ToString());
        var request = XDocument.Parse(DlnaProtocol.BrowseRequest(server, "a&<b", 200));
        Equal("a&<b", request.Descendants("ObjectID").Single().Value); Equal("200", request.Descendants("StartingIndex").Single().Value);
    }));
    await Test("DIDL filtering, unknown size, MIME, and preferred untranscoded resource", () => Check(() =>
    {
        var didl = "<DIDL-Lite xmlns:dc='urn:title'><container id='c'><dc:title>Folder</dc:title></container><item id='m'><dc:title>作品 OP.mp4</dc:title><res protocolInfo='http-get:*:video/mp4:DLNA.ORG_CI=1'>/transcoded</res><res protocolInfo='http-get:*:video/mp4:*'>/original</res></item><item id='sidecar'><dc:title>._作品 OP.mp4</dc:title><res protocolInfo='http-get:*:video/mp4:*'>/bad</res></item><item id='bad'><dc:title>x.txt</dc:title><res protocolInfo='http-get:*:text/plain:*'>/bad</res></item></DIDL-Lite>";
        var page = DlnaProtocol.Page(Page(didl, 4, 4), new Uri("http://localhost/browse"));
        Equal(4, page.Returned); Equal(2, page.Entries.Count); Equal("http://localhost/original", page.Entries[1].Media!.Location); Equal(-1L, page.Entries[1].Media!.Size);
    }));
    await Test("DTD/external entities are prohibited", () => Check(() =>
    {
        try { DlnaProtocol.Server("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///C:/secret'>]><root>&x;</root>", new Uri("http://localhost/")); throw new Exception("Accepted DTD"); }
        catch (XmlException) { }
    }));
    await Test("SOAP fault is surfaced", () => Check(() =>
    {
        try { DlnaProtocol.Page("<Envelope><Fault><errorCode>701</errorCode><errorDescription>Missing</errorDescription></Fault></Envelope>", new Uri("http://localhost/")); throw new Exception("Expected SOAP failure"); }
        catch (InvalidDataException ex) { True(ex.Message.Contains("701")); }
    }));
    await Test("Real HTTP pagination uses NumberReturned rather than filtered count", async () =>
    {
        using var server = new TestServer((body, index) =>
        {
            True(body.Contains("BrowseDirectChildren"));
            var start = XDocument.Parse(body).Descendants("StartingIndex").Single().Value;
            Equal(index == 0 ? "0" : "2", start);
            var didl = index == 0 ? "<DIDL-Lite><item id='ignored'><title>bad.txt</title></item><container id='folder'><title>Folder</title></container></DIDL-Lite>" : "<DIDL-Lite><item id='audio'><title>Music</title><res protocolInfo='http-get:*:audio/flac:*'>/song</res></item></DIDL-Lite>";
            return Page(didl, index == 0 ? 2 : 1, 3);
        }, 2);
        using var client = new DlnaClient();
        var result = await client.BrowseAsync(new("nas", "NAS", server.Url, "urn:schemas-upnp-org:service:ContentDirectory:1"), "0", default);
        Equal(2, result.Count); Equal("audio", result[1].Id); await server.Completion;
    });
    await Test("Repeated pages fail rather than loop indefinitely", async () =>
    {
        using var server = new TestServer((_, _) => Page("<DIDL-Lite><container id='same'><title>Same</title></container></DIDL-Lite>", 1, 3), 2);
        using var client = new DlnaClient();
        try { await client.BrowseAsync(new("nas", "NAS", server.Url, "urn:schemas-upnp-org:service:ContentDirectory:1"), "0", default); throw new Exception("Expected repeated-page error"); }
        catch (InvalidDataException) { }
        await server.Completion;
    });
    await Test("Canceled browsing does not start a request", async () =>
    {
        using var client = new DlnaClient(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await client.BrowseAsync(new("nas", "NAS", new Uri("http://127.0.0.1:1"), "urn:test"), "0", cancellation.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    });
}
finally { Directory.Delete(fixture, true); }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static string Page(string didl, int returned, int total) => new XElement("Envelope", new XElement("BrowseResponse", new XElement("Result", didl), new XElement("NumberReturned", returned), new XElement("TotalMatches", total))).ToString();

sealed class TestServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public Uri Url { get; }
    public Task Completion { get; }
    public TestServer(Func<string, int, string> response, int count)
    {
        listener.Start(); Url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/browse");
        Completion = Task.Run(async () =>
        {
            for (var index = 0; index < count; index++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                var length = 0; var action = false;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
                    if (line.StartsWith("SOAPAction:", StringComparison.OrdinalIgnoreCase)) action = line.Contains("#Browse");
                }
                if (!action) throw new Exception("SOAPAction missing");
                var body = new char[length]; var read = 0;
                while (read < length) { var n = await reader.ReadAsync(body.AsMemory(read)); if (n == 0) break; read += n; }
                var payload = Encoding.UTF8.GetBytes(response(new string(body, 0, read), index));
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header); await stream.WriteAsync(payload);
            }
        });
    }
    public void Dispose() => listener.Stop();
}
