using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ContinuousPlayer.Core;

public sealed class DlnaClient : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    public async Task<List<DlnaServer>> DiscoverAsync(CancellationToken cancellation)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:MediaServer:1\r\n\r\n");
        var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        discovery.CancelAfter(TimeSpan.FromSeconds(4));
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task SendAsync()
        {
            while (!discovery.IsCancellationRequested)
            {
                await udp.SendAsync(request, endpoint, discovery.Token);
                await Task.Delay(1500, discovery.Token);
            }
        }
        var sender = SendAsync();
        try
        {
            while (locations.Count < 32)
            {
                var packet = await udp.ReceiveAsync(discovery.Token);
                var location = DlnaProtocol.Location(Encoding.UTF8.GetString(packet.Buffer));
                if (location != null) locations.Add(location);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        finally
        {
            discovery.Cancel();
            try { await sender; } catch (OperationCanceledException) { }
        }
        cancellation.ThrowIfCancellationRequested();
        var servers = new List<DlnaServer>();
        foreach (var location in locations)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, location);
                var server = DlnaProtocol.Server(await ReadAsync(requestMessage, cancellation), new Uri(location));
                if (servers.All(x => x.Id != server.Id)) servers.Add(server);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Xml.XmlException or UriFormatException) { Debug.WriteLine(ex); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        }
        return servers.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<List<DlnaEntry>> BrowseAsync(DlnaServer server, string objectId, CancellationToken cancellation)
    {
        var entries = new List<DlnaEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, server.ControlUrl);
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{server.ServiceType}#Browse\"");
            request.Content = new StringContent(DlnaProtocol.BrowseRequest(server, objectId, start), Encoding.UTF8, "text/xml");
            var page = DlnaProtocol.Page(await ReadAsync(request, cancellation), server.ControlUrl);
            if (page.Returned == 0 && start < page.Total) throw new InvalidDataException("一覧が途中で終了しました。");
            foreach (var entry in page.Entries)
                if (!seen.Add(entry.Id)) throw new InvalidDataException("サーバーが同じ項目を繰り返しました。");
            entries.AddRange(page.Entries);
            if (page.Returned > 100_000 - start) throw new InvalidDataException("一覧の上限を超えました。");
            start += page.Returned;
            if (start >= page.Total) return entries;
            if (start >= 100_000) throw new InvalidDataException("一覧の上限を超えました。");
        }
    }

    private async Task<string> ReadAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.Content.Headers.ContentLength > DlnaProtocol.MaxXmlBytes) throw new InvalidDataException("XML応答が大きすぎます。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (memory.Length + count > DlnaProtocol.MaxXmlBytes) throw new InvalidDataException("XML応答が大きすぎます。");
            memory.Write(buffer, 0, count);
        }
        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, true);
        var xml = await reader.ReadToEndAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            if (request.Method == HttpMethod.Post && !string.IsNullOrWhiteSpace(xml))
                DlnaProtocol.Page(xml, request.RequestUri!); // Preserve a SOAP fault's useful error code.
            response.EnsureSuccessStatusCode();
        }
        return xml;
    }
    public void Dispose() => http.Dispose();
}
