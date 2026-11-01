// Test-only SSDP + loopback HTTP fixture. No production switches or saved data injection.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

public sealed class Stage4TestServer : IDisposable
{
    readonly CancellationTokenSource stop = new CancellationTokenSource();
    readonly TcpListener http = new TcpListener(IPAddress.Loopback, 0);
    readonly UdpClient udp;
    readonly Task httpTask, udpTask;
    public readonly string Name, RootName;
    public readonly string Udn;
    public volatile int DelayMilliseconds;
    public int BrowseRequests;
    readonly string origin;

    public Stage4TestServer(string id)
    {
        Name = "Stage4-" + id + "-NAS"; RootName = Name + "-root"; Udn = "uuid:" + Name;
        http.Start(); origin = "http://127.0.0.1:" + ((IPEndPoint)http.LocalEndpoint).Port;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
            udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
        }
        catch { http.Stop(); stop.Dispose(); throw; }
        httpTask = Task.Run(HttpLoop); udpTask = Task.Run(SsdpLoop);
    }

    async Task SsdpLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var request = await udp.ReceiveAsync(stop.Token);
                if (!Encoding.ASCII.GetString(request.Buffer).StartsWith("M-SEARCH ", StringComparison.Ordinal)) continue;
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: " + origin + "/device\r\nST: urn:schemas-upnp-org:device:MediaServer:1\r\nUSN: " + Udn + "\r\n\r\n");
                await udp.SendAsync(response, request.RemoteEndPoint, stop.Token);
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }

    async Task HttpLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await http.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => Respond(client));
            }
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
    }

    async Task Respond(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var first = await reader.ReadLineAsync(stop.Token); if (first == null) return;
            int length = 0; string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token)))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
            string xml;
            if (first.StartsWith("POST ", StringComparison.Ordinal))
            {
                if (length < 0 || length > 65536) return;
                var chars = new char[length]; int offset = 0;
                while (offset < length) { int read = await reader.ReadAsync(chars.AsMemory(offset), stop.Token); if (read == 0) return; offset += read; }
                var request = XDocument.Parse(new string(chars));
                var objectId = request.Descendants().First(x => x.Name.LocalName == "ObjectID").Value;
                Interlocked.Increment(ref BrowseRequests);
                await Task.Delay(DelayMilliseconds, stop.Token);
                xml = Browse(objectId);
            }
            else
                xml = "<root><device><friendlyName>" + Name + "</friendlyName><UDN>" + Udn + "</UDN><serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><controlURL>/browse</controlURL></service></serviceList></device></root>";
            var body = Encoding.UTF8.GetBytes(xml);
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/xml; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(body, stop.Token);
        }
        catch (Exception ex) when (stop.IsCancellationRequested || ex is IOException || ex is SocketException) { }
    }

    string Browse(string id)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XElement Folder(string key, string name) => new XElement(didl + "container", new XAttribute("id", key), new XElement(dc + "title", name));
        var entries = new XElement(didl + "DIDL-Lite");
        if (id == "0") entries.Add(Folder("r", RootName));
        else if (id == "r")
        {
            entries.Add(Folder("child", "NAS Child"));
            foreach (var ext in new[] { "wav", "flac" })
                entries.Add(new XElement(didl + "item", new XAttribute("id", ext), new XElement(dc + "title", "NAS.part.1." + ext),
                    new XElement(didl + "res", new XAttribute("protocolInfo", "http-get:*:audio/" + ext + ":*"), origin + "/" + ext)));
        }
        else if (id == "child") entries.Add(Folder("deep", "NAS Deep"));
        var count = entries.Elements().Count();
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = "urn:schemas-upnp-org:service:ContentDirectory:1";
        return new XElement(soap + "Envelope", new XElement(soap + "Body", new XElement(service + "BrowseResponse",
            new XElement("Result", entries.ToString()), new XElement("NumberReturned", count), new XElement("TotalMatches", count)))).ToString();
    }

    public void Dispose()
    {
        stop.Cancel(); udp.Dispose(); http.Stop();
        try { Task.WaitAll(new[] { httpTask, udpTask }, 2000); } catch (AggregateException) { }
        stop.Dispose();
    }
}
