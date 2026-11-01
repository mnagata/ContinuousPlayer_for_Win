// Test-only loopback DLNA/HTTP server. Deliberately offers no SSDP discovery.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

public sealed class Stage5TestServer : IDisposable
{
    readonly CancellationTokenSource stop = new CancellationTokenSource();
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    readonly ConcurrentDictionary<TcpClient, byte> streams = new ConcurrentDictionary<TcpClient, byte>();
    readonly Task loop;
    readonly string directory, origin;
    public readonly string Name, RootName, Udn;
    public Uri DescriptionUri { get { return new Uri(origin + "/device"); } }
    public Uri SlowDescriptionUri { get { return new Uri(origin + "/slow-device"); } }
    public volatile bool DeviceAvailable = true, NetworkAvailable = true, DifferentIdentity, FailSecond;
    public volatile int Generation, MediaOpenDelayMilliseconds, DescriptionDelayMilliseconds = 6000;
    public int DescriptionRequests, BrowseRequests, MediaRequests;

    public Stage5TestServer(string id, string mediaDirectory)
    {
        directory = mediaDirectory; Name = "Stage5-" + id + "-NAS"; RootName = Name + "-root"; Udn = "uuid:" + Name;
        listener.Start(); origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(Accept);
    }
    async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => Respond(client)); } }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    async Task Respond(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var first = await reader.ReadLineAsync(stop.Token); if (first == null) return;
            var method = first.Split(' ')[0]; var path = first.Split(' ')[1]; string? line; int length = 0; string? range = null;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
            }
            if (path.StartsWith("/media/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref MediaRequests);
                if (!NetworkAvailable || !path.EndsWith("?v=" + Generation, StringComparison.Ordinal) || FailSecond && path.Contains("/b.wav"))
                { await Text(stream, "Unavailable", 503); return; }
                streams.TryAdd(client, 0);
                await Task.Delay(MediaOpenDelayMilliseconds, stop.Token);
                var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "Sample OP.wav"), stop.Token);
                long start = 0, end = bytes.Length - 1; bool partial = range != null;
                if (range != null)
                {
                    var parts = range.Substring("bytes=".Length).Split('-');
                    if (!long.TryParse(parts[0], out start) || start < 0 || start >= bytes.Length) { await Text(stream, "Range", 416); return; }
                    if (parts.Length > 1 && long.TryParse(parts[1], out var requested)) end = Math.Min(end, requested);
                }
                var header = "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\nContent-Type: audio/wav\r\nAccept-Ranges: bytes\r\nContent-Length: " + (end - start + 1) + "\r\n" +
                    (partial ? "Content-Range: bytes " + start + "-" + end + "/" + bytes.Length + "\r\n" : "") + "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), stop.Token);
                if (method == "HEAD") return;
                // PCM is 16 KB/sec. Throttle so a mid-playback reset cannot be hidden by a full-file cache.
                for (long offset = start; offset <= end;)
                {
                    int count = (int)Math.Min(8192, end - offset + 1);
                    await stream.WriteAsync(bytes.AsMemory((int)offset, count), stop.Token); offset += count;
                    await Task.Delay(500, stop.Token);
                }
                return;
            }
            if (method == "POST")
            {
                if (length < 0 || length > 65536) return;
                var chars = new char[length]; int read = 0;
                while (read < length) { int count = await reader.ReadAsync(chars.AsMemory(read), stop.Token); if (count == 0) return; read += count; }
                var objectId = XDocument.Parse(new string(chars)).Descendants().First(x => x.Name.LocalName == "ObjectID").Value;
                Interlocked.Increment(ref BrowseRequests); await Text(stream, Browse(objectId)); return;
            }
            Interlocked.Increment(ref DescriptionRequests);
            if (path == "/slow-device") await Task.Delay(DescriptionDelayMilliseconds, stop.Token);
            if (path == "/denied") { await Text(stream, "Forbidden", 403); return; }
            if (!DeviceAvailable) { await Text(stream, "Offline", 503); return; }
            if (path == "/not-device") { await Text(stream, "<html>Not a DLNA server</html>"); return; }
            await Text(stream, "<root><device><friendlyName>" + Name + "</friendlyName><UDN>" + Udn + (DifferentIdentity ? "-different" : "") +
                "</UDN><serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><controlURL>/browse</controlURL></service></serviceList></device></root>");
        }
        catch (Exception ex) when (stop.IsCancellationRequested || ex is IOException || ex is SocketException || ex is ObjectDisposedException) { }
        finally { streams.TryRemove(client, out _); }
    }
    async Task Text(NetworkStream stream, string text, int status = 200)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(body, stop.Token);
    }
    string Browse(string id)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/", dc = "http://purl.org/dc/elements/1.1/";
        var entries = new XElement(didl + "DIDL-Lite");
        if (id == "0") entries.Add(new XElement(didl + "container", new XAttribute("id", "r"), new XElement(dc + "title", RootName)));
        else if (id == "r") foreach (var key in new[] { "a", "b" })
            entries.Add(new XElement(didl + "item", new XAttribute("id", key), new XElement(dc + "title", "Remote " + key.ToUpperInvariant() + ".wav"),
                new XElement(didl + "res", new XAttribute("protocolInfo", "http-get:*:audio/wav:*"), origin + "/media/" + key + ".wav?v=" + Generation)));
        int count = entries.Elements().Count();
        return new XElement("Envelope", new XElement("BrowseResponse", new XElement("Result", entries.ToString()),
            new XElement("NumberReturned", count), new XElement("TotalMatches", count))).ToString();
    }
    public void CutNetwork()
    {
        NetworkAvailable = false;
        foreach (var client in streams.Keys) { try { client.Client.LingerState = new LingerOption(true, 0); client.Close(); } catch (ObjectDisposedException) { } }
    }
    public void Recover() { Generation++; NetworkAvailable = true; }
    public void Dispose()
    {
        stop.Cancel(); CutNetwork(); listener.Stop();
        try { loop.Wait(2000); } catch (AggregateException) { }
        // Workers finish through the canceled token; keep the source alive until process exit.
    }
}
