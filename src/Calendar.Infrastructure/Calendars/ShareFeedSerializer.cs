using System.Security.Cryptography;
using System.Text;
using Calendar.Application.Calendars;
using Calendar.Domain;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Serializes a projected share window into a live RFC 5545 VCALENDAR (ARCHITECTURE §16). Scope drives detail:
/// <see cref="ShareScope.FullDetails"/> emits full VEVENTs (summary + location); <see cref="ShareScope.FreeBusy"/>
/// emits detail-free busy blocks (times only — no SUMMARY/LOCATION), so subscribers see availability without
/// leaking titles or places. Recurrence is already expanded by the projection, so every instance is a concrete
/// VEVENT. Built on Ical.Net (the host's iCalendar library); the output round-trips through any RFC 5545 parser.
/// </summary>
public static class ShareFeedSerializer
{
    private const string ProductId = "-//Unified Calendar//Share Feed//EN";

    public static string Serialize(string feedName, ShareScope scope, IReadOnlyList<ProjectedEvent> events)
    {
        var calendar = new Ical.Net.Calendar();
        calendar.ProductId = ProductId;
        calendar.Properties.Set("X-WR-CALNAME", feedName);

        foreach (var ev in events)
            calendar.Events.Add(ToComponent(ev, scope));

        var serializer = new CalendarSerializer();
        return serializer.SerializeToString(calendar);
    }

    private static CalendarEvent ToComponent(ProjectedEvent ev, ShareScope scope)
    {
        var component = new CalendarEvent
        {
            // Stable per-instance UID so subscribers can diff/update; recurrence instances get a distinct UID.
            Uid = BuildUid(ev),
            Start = ToCalDateTime(ev.StartUtc, ev.AllDay),
            End = ToCalDateTime(EffectiveEnd(ev), ev.AllDay),
        };

        if (scope == ShareScope.FreeBusy)
        {
            // Detail-free busy block: opaque time only. No SUMMARY/LOCATION/CATEGORIES → no information leak.
            component.Summary = "Busy";
            component.Transparency = TransparencyType.Opaque;
        }
        else
        {
            component.Summary = ev.Title;
            if (!string.IsNullOrWhiteSpace(ev.Location))
                component.Location = ev.Location;
            if (ev.Categories.Count > 0)
                component.Categories = ev.Categories.ToList();
        }

        return component;
    }

    /// <summary>All-day end is stored inclusive; RFC 5545 DTEND is exclusive, so add a day for the all-day case.</summary>
    private static DateTimeOffset EffectiveEnd(ProjectedEvent ev) =>
        ev.AllDay ? ev.EndUtc.AddDays(1) : ev.EndUtc;

    private static CalDateTime ToCalDateTime(DateTimeOffset value, bool allDay)
    {
        var utc = value.UtcDateTime;
        return allDay
            ? new CalDateTime(utc.Year, utc.Month, utc.Day)   // VALUE=DATE
            : new CalDateTime(utc, "UTC");
    }

    /// <summary>
    /// Derive a deterministic UID per projected instance: the event id plus the instance start, so recurring
    /// occurrences serialize as distinct VEVENTs without colliding. FreeBusy keeps the same scheme (times only).
    /// </summary>
    private static string BuildUid(ProjectedEvent ev)
    {
        var seed = $"{ev.Id:N}|{ev.StartUtc.UtcDateTime:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"{Convert.ToHexStringLower(hash)[..24]}@unified-calendar.share";
    }
}
