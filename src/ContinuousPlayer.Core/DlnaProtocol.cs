using System.Xml;
using System.Xml.Linq;

namespace ContinuousPlayer.Core;

public record DlnaServer(string Id, string Name, Uri ControlUrl, string ServiceType, Uri? DescriptionUri = null);
public record DlnaEntry(string Id, string Name, MediaFile? Media = null);
public record DlnaPage(List<DlnaEntry> Entries, int Returned, int Total);

public static class DlnaProtocol
{
    public const int MaxXmlBytes = 4 * 1024 * 1024;
    public static string? Location(string response)
    {
        var lines = response.Split('\n');
        if (!lines[0].Trim().StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !lines[0].Trim().StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase)) return null;
        return lines.Skip(1).Select(x => x.Split(':', 2)).Where(x => x.Length == 2 && x[0].Trim().Equals("location", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[1].Trim()).FirstOrDefault(IsHttp);
    }

    public static DlnaServer Server(string xml, Uri location)
    {
        var root = Parse(xml);
        var baseUrl = Child(root, "URLBase");
        var origin = string.IsNullOrWhiteSpace(baseUrl) ? location : Resolve(location, baseUrl);
        foreach (var device in root.Descendants().Where(x => x.Name.LocalName == "device"))
        {
            var service = device.Elements().FirstOrDefault(x => x.Name.LocalName == "serviceList")?.Elements()
                .FirstOrDefault(x => Child(x, "serviceType").StartsWith("urn:schemas-upnp-org:service:ContentDirectory:", StringComparison.Ordinal));
            if (service == null || string.IsNullOrWhiteSpace(Child(service, "controlURL"))) continue;
            var control = Resolve(origin, Child(service, "controlURL"));
            if (!DlnaAddress.IsHttpTarget(control)) throw new InvalidDataException("DLNAの制御先URLが不正です。");
            return new DlnaServer(Nonempty(Child(device, "UDN"), location.ToString()), Nonempty(Child(device, "friendlyName"), location.Host),
                control, Child(service, "serviceType"), location);
        }
        throw new InvalidDataException("ContentDirectoryサービスが見つかりません。");
    }

    public static string BrowseRequest(DlnaServer server, string objectId, int start)
    {
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace service = server.ServiceType;
        return new XDocument(new XElement(soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", soap),
            new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(soap + "Body", new XElement(service + "Browse", new XAttribute(XNamespace.Xmlns + "u", service),
                new XElement("ObjectID", objectId), new XElement("BrowseFlag", "BrowseDirectChildren"), new XElement("Filter", "*"),
                new XElement("StartingIndex", start), new XElement("RequestedCount", 200), new XElement("SortCriteria", ""))))).ToString();
    }

    public static DlnaPage Page(string xml, Uri origin)
    {
        var root = Parse(xml);
        var fault = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fault");
        if (fault != null) throw new InvalidDataException("UPnP: " + string.Join(" ", fault.Descendants().Where(x => x.Name.LocalName is "errorCode" or "errorDescription").Select(x => x.Value)));
        var response = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "BrowseResponse") ?? throw new InvalidDataException("BrowseResponseがありません。");
        if (!int.TryParse(Child(response, "NumberReturned"), out var returned) || returned < 0 ||
            !int.TryParse(Child(response, "TotalMatches"), out var total) || total < 0) throw new InvalidDataException("一覧の件数が不正です。");
        var entries = new List<DlnaEntry>();
        var result = Child(response, "Result");
        if (!string.IsNullOrWhiteSpace(result))
        {
            foreach (var element in Parse(result).Elements())
            {
                var id = (string?)element.Attribute("id") ?? "";
                var title = Nonempty(Child(element, "title"), id);
                if (title.StartsWith("._", StringComparison.Ordinal)) continue;
                if (element.Name.LocalName == "container") entries.Add(new(id, title));
                else if (element.Name.LocalName == "item")
                {
                    foreach (var res in element.Elements().Where(x => x.Name.LocalName == "res")
                        .OrderBy(x => ((string?)x.Attribute("protocolInfo"))?.Contains("DLNA.ORG_CI=1") == true))
                    {
                        var protocol = ((string?)res.Attribute("protocolInfo") ?? "").Split(':', 4);
                        if (protocol.Length != 4 || !protocol[0].Equals("http-get", StringComparison.OrdinalIgnoreCase)) continue;
                        Uri url;
                        try { url = Resolve(origin, res.Value.Trim()); } catch (Exception ex) when (ex is UriFormatException or InvalidDataException) { continue; }
                        var mime = protocol[2].Split(';')[0].ToLowerInvariant();
                        if (!(mime.StartsWith("video/") || mime.StartsWith("audio/") ||
                            ((mime is "*" or "application/octet-stream") && (MediaCatalog.IsSupported(title) || MediaCatalog.IsSupported(url.AbsolutePath))))) continue;
                        var media = new MediaFile(id, title, url.ToString(), long.TryParse((string?)res.Attribute("size"), out var size) ? size : -1,
                            mime.StartsWith("audio/") || mime.StartsWith("video/") ? mime : null,
                            protocol[3].Contains("DLNA.ORG_CI=1", StringComparison.OrdinalIgnoreCase));
                        entries.Add(new(id, title, media));
                        break;
                    }
                }
            }
        }
        return new(entries, returned, total);
    }

    private static XElement Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
        return XElement.Load(reader);
    }
    private static string Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
    private static string Nonempty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static bool IsHttp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    private static Uri Resolve(Uri origin, string relative)
    {
        var url = new Uri(origin, relative);
        if (!IsHttp(url.ToString())) throw new InvalidDataException("HTTP以外のURLは使用できません。");
        return url;
    }
}
