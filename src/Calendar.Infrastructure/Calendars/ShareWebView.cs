using System.Net;
using System.Text;
using Calendar.Application.Calendars;
using Calendar.Domain;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Renders the optional minimal public web view for a share (ARCHITECTURE §16): a self-contained, read-only
/// HTML page listing the shared events plus a link to subscribe to the live <c>.ics</c> feed. Honors scope —
/// <see cref="ShareScope.FreeBusy"/> shows only "Busy" time blocks, never titles or locations. All dynamic
/// text is HTML-encoded to keep the public page safe from injection.
/// </summary>
public static class ShareWebView
{
    public static string Render(string feedName, ShareScope scope, string feedPath, IReadOnlyList<ProjectedEvent> events)
    {
        var title = WebUtility.HtmlEncode(feedName);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{title}</title>");
        sb.Append("<style>body{font-family:system-ui,sans-serif;max-width:680px;margin:2rem auto;padding:0 1rem;color:#1f2937}"
            + "h1{font-size:1.4rem}.sub{color:#6b7280;font-size:.9rem}ul{list-style:none;padding:0}"
            + "li{padding:.6rem 0;border-bottom:1px solid #e5e7eb}.t{font-weight:600}.m{color:#6b7280;font-size:.85rem}"
            + "a.feed{display:inline-block;margin:.5rem 0 1rem;font-size:.9rem}</style></head><body>");
        sb.Append($"<h1>{title}</h1>");
        sb.Append($"<p class=\"sub\">Read-only {(scope == ShareScope.FreeBusy ? "free/busy" : "calendar")} share.</p>");
        sb.Append($"<a class=\"feed\" href=\"{WebUtility.HtmlEncode(feedPath)}\">Subscribe (.ics)</a>");

        if (events.Count == 0)
        {
            sb.Append("<p class=\"m\">No events in the shared window.</p>");
        }
        else
        {
            sb.Append("<ul>");
            foreach (var ev in events)
            {
                var label = scope == ShareScope.FreeBusy ? "Busy" : WebUtility.HtmlEncode(ev.Title);
                sb.Append("<li><div class=\"t\">").Append(label).Append("</div>");
                sb.Append("<div class=\"m\">").Append(WebUtility.HtmlEncode(FormatWhen(ev))).Append("</div>");
                if (scope == ShareScope.FullDetails && !string.IsNullOrWhiteSpace(ev.Location))
                    sb.Append("<div class=\"m\">").Append(WebUtility.HtmlEncode(ev.Location)).Append("</div>");
                sb.Append("</li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string FormatWhen(ProjectedEvent ev)
    {
        if (ev.AllDay)
        {
            var start = ev.StartUtc.UtcDateTime.ToString("ddd, dd MMM yyyy");
            return ev.EndUtc.Date > ev.StartUtc.Date
                ? $"{start} – {ev.EndUtc.UtcDateTime:ddd, dd MMM yyyy}"
                : start;
        }
        return $"{ev.StartUtc.UtcDateTime:ddd, dd MMM yyyy HH:mm} – {ev.EndUtc.UtcDateTime:HH:mm} UTC";
    }
}
