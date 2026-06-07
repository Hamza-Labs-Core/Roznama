namespace Calendar.Plugin.Abstractions;

/// <summary>capability <c>calendar.read</c>. Read &amp; normalize calendars and events with delta sync.</summary>
public interface ICalendarSource : IPlugin
{
    /// <summary>Enumerate the calendars this account exposes (primary, shared, Holidays, Birthdays, …).</summary>
    Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct);

    /// <summary>
    /// Delta sync one calendar. <paramref name="syncToken"/> is null on the first (full) sync; otherwise it
    /// is the opaque token returned by a prior call (Google <c>nextSyncToken</c>, Graph
    /// <c>@odata.deltaLink</c>, CalDAV <c>sync-token</c>, or the ICS plugin's self-minted
    /// <c>etag|lastmod|bodyhash</c>). On an invalidated token (e.g. Google/Graph <c>410</c>) the plugin
    /// throws <see cref="SyncResetRequiredException"/> so the host wipes the token and re-runs a full sync.
    /// </summary>
    Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct);
}

/// <summary>capability <c>calendar.write</c> (later phase). Optimistic-concurrency writes via change tags.</summary>
public interface ICalendarWriter : IPlugin
{
    /// <summary>Create an event on a calendar; returns the created event with its assigned RemoteId/ChangeTag.</summary>
    Task<RemoteEvent> CreateEventAsync(string remoteCalendarId, RemoteEvent draft, CancellationToken ct);

    /// <summary>
    /// Update an event. <paramref name="ifMatchChangeTag"/> carries the prior <see cref="RemoteEvent.ChangeTag"/>
    /// (ETag/changeKey) for optimistic concurrency; on a precondition failure (CalDAV/Google/Graph <c>412</c>)
    /// the plugin throws <see cref="ConcurrencyConflictException"/> so the host re-fetches and merges.
    /// </summary>
    Task<RemoteEvent> UpdateEventAsync(string remoteCalendarId, RemoteEvent updated, string? ifMatchChangeTag, CancellationToken ct);

    /// <summary>Delete an event (optionally guarded by its change tag).</summary>
    Task DeleteEventAsync(string remoteCalendarId, string remoteEventId, string? ifMatchChangeTag, CancellationToken ct);
}

/// <summary>A calendar as the provider exposes it, normalized. Maps to the domain CALENDAR (ARCHITECTURE.md §9).</summary>
public sealed record RemoteCalendar(
    string RemoteId,                            // provider id / CalDAV collection href / feed URL — passed back to SyncAsync
    string Name,                                // display name (Google summary, Graph name, CalDAV displayname, ICS X-WR-CALNAME)
    string? Color,                              // hex color if the provider supplies one
    bool IsReadOnly);                           // true for Holidays/Birthdays, reader-role calendars, ICS feeds

/// <summary>
/// A normalized event. The field set is the canonical union the calendar deep-dives map onto; storage keys
/// on <see cref="Uid"/> for dedup and <see cref="RemoteId"/> for addressing.
/// </summary>
public sealed record RemoteEvent(
    string RemoteId,                            // provider-unique id within the calendar (Google id, Graph id, CalDAV href, ICS (UID,RECURRENCE-ID))
    string Uid,                                 // stable cross-system id (iCalUID / UID) — FEEDS DEDUP across accounts
    string Title,                               // SUMMARY / summary / subject
    DateTimeOffset StartUtc,                    // resolved to UTC for storage
    DateTimeOffset EndUtc,
    string? TimeZoneId,                         // originating IANA tz id (null for all-day/floating); UI renders in user's zone
    bool AllDay,                                // DTSTART;VALUE=DATE / start.date / isAllDay (note: all-day DTEND is exclusive — host adjusts)
    string? Rrule,                              // master RRULE (+RDATE/EXDATE); recurrence is stored, expanded on demand — never materialized
    string? RecurrenceId,                       // set on an override/exception instance; key occurrences by (Uid, RecurrenceId)
    string? Location,                           // free-form LOCATION text → geocoded to a Place by the host
    GeoPoint? Geo,                              // provider-supplied lat/lng (iCal GEO, Graph location.coordinates) — skips geocoding
    IReadOnlyList<string> Categories,           // CATEGORIES / categories[] → domain category rules (ARCHITECTURE.md §13)
    string? ChangeTag,                          // per-event ETag / @odata.etag / changeKey — optimistic concurrency for writes
    EventStatus Status);                        // Confirmed/Tentative → upsert; Cancelled → delete (tombstone)

/// <summary>Event lifecycle status. <see cref="Cancelled"/> is a tombstone → the host removes the event.</summary>
public enum EventStatus { Confirmed, Tentative, Cancelled }

/// <summary>
/// The result of one delta sync round. <see cref="Upserts"/> are normalized events to add/update;
/// <see cref="Deletes"/> are RemoteIds to remove (from tombstones / vanished resources);
/// <see cref="NewSyncToken"/> is the opaque token to replay next time. Upserts are idempotent (Uid-keyed).
/// </summary>
public sealed record SyncResult(
    IReadOnlyList<RemoteEvent> Upserts,
    IReadOnlyList<string> Deletes,
    string NewSyncToken);

/// <summary>
/// Thrown by <see cref="ICalendarSource.SyncAsync"/> when the supplied token is no longer valid (Google/
/// Graph <c>410</c>, CalDAV CTag divergence). The host discards the token, wipes the calendar cache, and
/// re-runs a full sync (token = null).
/// </summary>
public sealed class SyncResetRequiredException : Exception
{
    public SyncResetRequiredException(string? message = null, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Thrown on an optimistic-concurrency precondition failure (HTTP 412) during a write.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string? message = null, Exception? inner = null) : base(message, inner) { }
}
