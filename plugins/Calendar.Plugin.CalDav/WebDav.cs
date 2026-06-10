using System.Net;
using System.Xml.Linq;

namespace Calendar.Plugin.CalDav;

/// <summary>
/// Pure WebDAV/CalDAV XML helpers (caldav-plugin.md §10): namespaces, request-body builders, and
/// <c>207 Multi-Status</c> parsers. Nothing here touches the network, so the protocol parsing is fully
/// unit-testable against canned XML. A 207 can mix <c>200</c>/<c>404</c> per property, so the parsers check
/// the per-prop <c>&lt;status&gt;</c> and never assume success.
/// </summary>
internal static class WebDav
{
    public static readonly XNamespace D = "DAV:";
    public static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CS = "http://calendarserver.org/ns/";
    public static readonly XNamespace Apple = "http://apple.com/ns/ical/";

    // ── Request bodies ──────────────────────────────────────────────────────────────────────────────

    /// <summary>PROPFIND body requesting <c>current-user-principal</c> (caldav-plugin.md §4).</summary>
    public static string CurrentUserPrincipalBody() =>
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:current-user-principal/></d:prop></d:propfind>""";

    /// <summary>PROPFIND body requesting <c>calendar-home-set</c> on a principal (caldav-plugin.md §4).</summary>
    public static string CalendarHomeSetBody() =>
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><c:calendar-home-set/></d:prop></d:propfind>""";

    /// <summary>PROPFIND body enumerating calendar collections in a home set (caldav-plugin.md §5).</summary>
    public static string CalendarCollectionsBody() =>
        """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:ic="http://apple.com/ns/ical/"><d:prop><d:resourcetype/><d:displayname/><d:current-user-privilege-set/><ic:calendar-color/><cs:getctag/><d:sync-token/><c:supported-calendar-component-set/></d:prop></d:propfind>""";

    /// <summary>calendar-query REPORT returning every VEVENT's getetag + calendar-data (caldav-plugin.md §6).</summary>
    public static string CalendarQueryBody() =>
        """<?xml version="1.0" encoding="utf-8"?><c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"/></c:comp-filter></c:filter></c:calendar-query>""";

    /// <summary>calendar-query REPORT returning only getetag (the cheap ETag-diff probe, caldav-plugin.md §6/§7).</summary>
    public static string EtagsOnlyQueryBody() =>
        """<?xml version="1.0" encoding="utf-8"?><c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/></d:prop><c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"/></c:comp-filter></c:filter></c:calendar-query>""";

    /// <summary>calendar-multiget REPORT fetching calendar-data for a specific set of hrefs (caldav-plugin.md §7).</summary>
    public static string MultiGetBody(IEnumerable<string> hrefs)
    {
        var hrefXml = string.Concat(hrefs.Select(h => $"<d:href>{Escape(h)}</d:href>"));
        return $"""<?xml version="1.0" encoding="utf-8"?><c:calendar-multiget xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop>{hrefXml}</c:calendar-multiget>""";
    }

    /// <summary>sync-collection REPORT (RFC 6578). Empty token on the first run (caldav-plugin.md §7).</summary>
    public static string SyncCollectionBody(string? syncToken)
    {
        var token = string.IsNullOrEmpty(syncToken) ? string.Empty : Escape(syncToken);
        return $"""<?xml version="1.0" encoding="utf-8"?><d:sync-collection xmlns:d="DAV:"><d:sync-token>{token}</d:sync-token><d:sync-level>1</d:sync-level><d:prop><d:getetag/></d:prop></d:sync-collection>""";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ── Response parsers ────────────────────────────────────────────────────────────────────────────

    /// <summary>Pull the first 200-status <c>current-user-principal</c> href out of a 207 response.</summary>
    public static string? ParseCurrentUserPrincipal(string xml)
    {
        foreach (var response in Responses(xml))
        {
            var href = OkProp(response, D + "current-user-principal")?
                .Element(D + "href")?.Value;
            if (!string.IsNullOrWhiteSpace(href))
                return href.Trim();
        }
        return null;
    }

    /// <summary>Pull the first 200-status <c>calendar-home-set</c> href out of a 207 response.</summary>
    public static string? ParseCalendarHomeSet(string xml)
    {
        foreach (var response in Responses(xml))
        {
            var href = OkProp(response, C + "calendar-home-set")?
                .Element(D + "href")?.Value;
            if (!string.IsNullOrWhiteSpace(href))
                return href.Trim();
        }
        return null;
    }

    /// <summary>Parse the calendar-home enumeration into the calendar collections it advertises (caldav-plugin.md §5).</summary>
    public static IReadOnlyList<CalDavCollection> ParseCalendarCollections(string xml)
    {
        var result = new List<CalDavCollection>();
        foreach (var response in Responses(xml))
        {
            var href = response.Element(D + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var resourceType = OkProp(response, D + "resourcetype");
            var isCalendar = resourceType?.Element(C + "calendar") is not null;
            if (!isCalendar)
                continue; // skip the home set itself and non-calendar collections

            // Keep only collections advertising VEVENT support (skip VTODO-only).
            var comps = OkProp(response, C + "supported-calendar-component-set");
            if (comps is not null)
            {
                var supportsVevent = comps.Elements(C + "comp")
                    .Any(c => string.Equals((string?)c.Attribute("name"), "VEVENT", StringComparison.OrdinalIgnoreCase));
                if (!supportsVevent)
                    continue;
            }

            var name = OkProp(response, D + "displayname")?.Value;
            var color = NormalizeColor(OkProp(response, Apple + "calendar-color")?.Value);
            var ctag = OkProp(response, CS + "getctag")?.Value;
            var syncToken = OkProp(response, D + "sync-token")?.Value;

            // Read-only when the privilege set is present and lacks write (caldav-plugin.md §5).
            var privileges = OkProp(response, D + "current-user-privilege-set");
            var canWrite = privileges is null || privileges.Elements(D + "privilege")
                .Any(p => p.Element(D + "write") is not null
                          || p.Element(D + "write-content") is not null
                          || p.Element(D + "all") is not null);

            result.Add(new CalDavCollection(
                Href: href,
                Name: string.IsNullOrWhiteSpace(name) ? href : name,
                Color: color,
                CTag: string.IsNullOrWhiteSpace(ctag) ? null : ctag.Trim(),
                SyncToken: string.IsNullOrWhiteSpace(syncToken) ? null : syncToken.Trim(),
                IsReadOnly: !canWrite));
        }
        return result;
    }

    /// <summary>Parse a calendar-query / calendar-multiget 207 into per-resource (href, etag, calendar-data).</summary>
    public static IReadOnlyList<CalDavResource> ParseResources(string xml)
    {
        var result = new List<CalDavResource>();
        foreach (var response in Responses(xml))
        {
            var href = response.Element(D + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var etag = OkProp(response, D + "getetag")?.Value?.Trim();
            var data = OkProp(response, C + "calendar-data")?.Value;
            result.Add(new CalDavResource(href, etag, string.IsNullOrEmpty(data) ? null : data));
        }
        return result;
    }

    /// <summary>
    /// Parse a sync-collection 207 into changed resources (200, with new ETags), removed hrefs (404),
    /// and the new sync-token (RFC 6578, caldav-plugin.md §7).
    /// </summary>
    public static SyncCollectionResult ParseSyncCollection(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        var newToken = root.Element(D + "sync-token")?.Value?.Trim() ?? string.Empty;

        var changed = new List<CalDavResource>();
        var removed = new List<string>();
        foreach (var response in root.Elements(D + "response"))
        {
            var href = response.Element(D + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            // A response-level <status> of 404 marks a removed resource.
            var responseStatus = response.Element(D + "status")?.Value;
            if (responseStatus is not null && StatusCodeOf(responseStatus) == HttpStatusCode.NotFound)
            {
                removed.Add(href);
                continue;
            }

            var etag = OkProp(response, D + "getetag")?.Value?.Trim();
            changed.Add(new CalDavResource(href, etag, null));
        }
        return new SyncCollectionResult(changed, removed, newToken);
    }

    // ── Multi-status plumbing ───────────────────────────────────────────────────────────────────────

    private static IEnumerable<XElement> Responses(string xml) =>
        XDocument.Parse(xml).Root?.Elements(D + "response") ?? Enumerable.Empty<XElement>();

    /// <summary>
    /// Return the named property element only if it lives in a <c>propstat</c> whose status is 2xx — so a
    /// 207 that mixes 200/404 per prop never yields data from a 404 block (caldav-plugin.md §10).
    /// </summary>
    private static XElement? OkProp(XElement response, XName propName)
    {
        foreach (var propstat in response.Elements(D + "propstat"))
        {
            var status = propstat.Element(D + "status")?.Value;
            if (status is not null && !IsSuccess(status))
                continue;
            var prop = propstat.Element(D + "prop")?.Element(propName);
            if (prop is not null)
                return prop;
        }
        return null;
    }

    private static bool IsSuccess(string statusLine)
    {
        var code = StatusCodeOf(statusLine);
        return code is { } c && (int)c is >= 200 and < 300;
    }

    /// <summary>Parse <c>HTTP/1.1 200 OK</c> → <see cref="HttpStatusCode"/>.</summary>
    public static HttpStatusCode? StatusCodeOf(string statusLine)
    {
        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (int.TryParse(part, out var code) && code is >= 100 and < 600)
                return (HttpStatusCode)code;
        return null;
    }

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        color = color.Trim();
        // Apple ships #RRGGBBAA; drop the alpha byte so we store #RRGGBB (caldav-plugin.md §5).
        if (color.Length == 9 && color[0] == '#')
            return color[..7];
        return color;
    }
}

/// <summary>A calendar collection discovered in the home set (caldav-plugin.md §5).</summary>
internal sealed record CalDavCollection(
    string Href,
    string Name,
    string? Color,
    string? CTag,
    string? SyncToken,
    bool IsReadOnly);

/// <summary>One resource row from a calendar-query / multiget / sync-collection response.</summary>
internal sealed record CalDavResource(string Href, string? ETag, string? CalendarData);

/// <summary>The parsed result of a sync-collection REPORT (RFC 6578).</summary>
internal sealed record SyncCollectionResult(
    IReadOnlyList<CalDavResource> Changed,
    IReadOnlyList<string> Removed,
    string NewSyncToken);
