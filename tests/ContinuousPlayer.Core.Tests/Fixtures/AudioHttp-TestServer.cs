// Loopback-only DLNA/HTTP fixture shared by Core, production-engine and UI checks.
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

public sealed class AudioHttpTestServer : IDisposable
{
    public sealed record Resource(string Id, string Name, byte[] Bytes, string Mime, bool Transcoded = false);
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<TcpClient, byte> active = new();
    readonly ConcurrentDictionary<string, Resource> resources = new();
    readonly Task loop;
    readonly string origin;
    public readonly string Udn = "uuid:AudioHttp-" + Guid.NewGuid().ToString("N");
    public readonly string Name = "Audio HTTP validation";
    public readonly string RootName = "Audio-" + Guid.NewGuid().ToString("N")[..8];
    public Uri DescriptionUri => new(origin + "/device");
    public Uri MediaUri(string id) => new(origin + "/audio/" + id + "?v=" + Generation);
    public volatile int Generation, HeaderDelayMilliseconds, ChunkDelayMilliseconds, TruncateAfterBytes;
    public volatile bool Available = true, IgnoreRange, WrongRange, ChangeTag;
    public int MediaRequests, BrowseRequests;
    public readonly ConcurrentQueue<string> Requests = new();
    public void Add(string id, string name, byte[] bytes, string mime, bool transcoded = false) => resources[id] = new(id, name, bytes, mime, transcoded);
    public void Recover() { Generation++; Available = true; }
    public void Disconnect()
    {
        Available = false;
        foreach (var client in active.Keys) try { client.Dispose(); } catch { }
    }
    public AudioHttpTestServer()
    {
        listener.Start(); origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptAsync);
    }
    async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = RespondAsync(client);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }
    async Task RespondAsync(TcpClient client)
    {
        active[client] = 0;
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var line = await reader.ReadLineAsync(stop.Token); if (line == null) return;
            var requestLine = line.Split(' '); var method = requestLine[0]; var target = requestLine[1];
            var headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
            { var parts = line.Split(':', 2); if (parts.Length == 2) headers[parts[0]] = parts[1].Trim(); }
            if (target == "/device")
            {
                await TextAsync(stream, $"<root xmlns='urn:schemas-upnp-org:device-1-0'><device><friendlyName>{Name}</friendlyName><UDN>{Udn}</UDN><serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><controlURL>/control</controlURL></service></serviceList></device></root>"); return;
            }
            if (target == "/control" && method == "POST")
            {
                Interlocked.Increment(ref BrowseRequests);
                var length = int.Parse(headers["Content-Length"]); if (length > 65536) return;
                var chars = new char[length]; var read = 0;
                while (read < length) { var n = await reader.ReadAsync(chars.AsMemory(read), stop.Token); if (n == 0) return; read += n; }
                var id = XDocument.Parse(new string(chars)).Descendants().First(x => x.Name.LocalName == "ObjectID").Value;
                XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
                XNamespace dc = "http://purl.org/dc/elements/1.1/";
                var result = new XElement(didl + "DIDL-Lite");
                if (id == "0") result.Add(new XElement(didl + "container", new XAttribute("id", "r"), new XElement(dc + "title", RootName)));
                else foreach (var resource in resources.Values.OrderBy(x => x.Id))
                    result.Add(new XElement(didl + "item", new XAttribute("id", resource.Id), new XElement(dc + "title", resource.Name),
                        new XElement(didl + "res", new XAttribute("protocolInfo", "http-get:*:" + resource.Mime + ":DLNA.ORG_CI=" + (resource.Transcoded ? "1" : "0")),
                            new XAttribute("sampleFrequency", "192000"), new XAttribute("size", resource.Bytes.Length), MediaUri(resource.Id).AbsoluteUri)));
                XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
                var count = result.Elements().Count();
                var xml = new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(XName.Get("BrowseResponse", "urn:schemas-upnp-org:service:ContentDirectory:1"),
                    new XElement("Result", result.ToString()), new XElement("NumberReturned", count), new XElement("TotalMatches", count))));
                await TextAsync(stream, xml.ToString()); return;
            }
            if (!target.StartsWith("/audio/", StringComparison.Ordinal)) { await TextAsync(stream, "Not found", 404); return; }
            Interlocked.Increment(ref MediaRequests);
            var key = target.Substring(7).Split('?')[0];
            if (!Available || !target.EndsWith("?v=" + Generation, StringComparison.Ordinal)) { await TextAsync(stream, "Expired", 403); return; }
            if (!resources.TryGetValue(key, out var item)) { await TextAsync(stream, "Missing", 404); return; }
            await Task.Delay(HeaderDelayMilliseconds, stop.Token);
            var partial = !IgnoreRange && headers.TryGetValue("Range", out _);
            long from = 0, to = item.Bytes.Length - 1;
            if (partial)
            {
                var parts = headers["Range"].Substring(6).Split('-');
                from = long.Parse(parts[0]);
                if (from >= item.Bytes.Length) { await TextAsync(stream, "Out of range", 416); return; }
                if (long.TryParse(parts[1], out var last)) to = Math.Min(to, last);
            }
            Requests.Enqueue(key + ":" + from + "-" + to + ":v" + Generation);
            var tag = "\"" + key + "-" + Generation + (ChangeTag ? "-changed" : "") + "\"";
            var header = "HTTP/1.1 " + (partial ? "206 Partial Content" : "200 OK") + "\r\nContent-Type: " + item.Mime + "\r\nContent-Length: " + (to-from+1) + "\r\nETag: " + tag + "\r\n" +
                (partial ? "Content-Range: bytes " + (WrongRange ? from+1 : from) + "-" + to + "/" + item.Bytes.Length + "\r\n" : "") + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), stop.Token);
            if (method == "HEAD") return;
            var sent = 0;
            for (var offset = from; offset <= to;)
            {
                var count = (int)Math.Min(16384, to-offset+1);
                if (TruncateAfterBytes > 0) count = Math.Min(count, TruncateAfterBytes-sent);
                if (count <= 0) return;
                await stream.WriteAsync(item.Bytes.AsMemory((int)offset, count), stop.Token);
                offset += count; sent += count;
                if (ChunkDelayMilliseconds > 0) await Task.Delay(ChunkDelayMilliseconds, stop.Token);
            }
        }
        catch (Exception) { }
        finally { active.TryRemove(client, out _); }
    }
    async Task TextAsync(NetworkStream stream, string text, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Result\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"), stop.Token);
        await stream.WriteAsync(bytes, stop.Token);
    }
    public void Dispose()
    {
        stop.Cancel(); listener.Stop();
        foreach (var client in active.Keys) try { client.Dispose(); } catch { }
        try { loop.GetAwaiter().GetResult(); } catch { }
        stop.Dispose();
    }
}
