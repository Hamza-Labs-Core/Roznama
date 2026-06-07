using System.Text.Json;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// <c>calendar.write</c> write-back with an offline outbox (ARCHITECTURE §8/§10; SDK-CONTRACT §4.write).
/// A write to a writable calendar (<c>Calendar.IsReadOnly == false</c>) is reflected locally and pushed to the
/// bound plugin's <see cref="ICalendarWriter"/>. When the provider is unreachable the mutation is enqueued in
/// the <see cref="WriteOutbox"/> (Operation, Payload, BaseETag) and the call returns optimistically; a later
/// <see cref="ReplayAsync"/> drains Pending rows in FIFO order honoring <c>If-Match</c> — a 412 marks the row
/// <see cref="OutboxStatus.Conflict"/>. Writes to a read-only calendar are rejected.
/// </summary>
public sealed class WriteService : IWriteService
{
    private const string EventEntityType = "Event";
    private const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CalendarDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly PluginHostServicesFactory _hostFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger<WriteService> _logger;

    public WriteService(
        CalendarDbContext db,
        IPluginRegistry registry,
        PluginHostServicesFactory hostFactory,
        ILogger<WriteService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _registry = registry;
        _hostFactory = hostFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<EventWriteResult> CreateEventAsync(Guid calendarId, EventWriteRequest request, CancellationToken ct)
    {
        var calendar = await _db.Calendars.FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Calendar {calendarId} not found.");
        EnsureWritable(calendar);

        // Reflect locally first (optimistic): a draft event the UI can render immediately (ARCHITECTURE §8).
        var uid = Guid.NewGuid().ToString("N");
        var ev = new Event
        {
            Id = Guid.CreateVersion7(),
            CalendarId = calendar.Id,
            Uid = uid,
            RemoteId = uid, // provisional until the provider assigns one; replaced on apply
            Status = EventStatus.Confirmed,
        };
        ApplyRequest(ev, request);
        _db.Events.Add(ev);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var (writer, host, remoteCalendarId) = await ResolveWriterAsync(calendar, ct).ConfigureAwait(false);
        var draft = ToRemote(ev, request);

        try
        {
            await InitializeAsync(writer, host, ct).ConfigureAwait(false);
            var created = await writer.CreateEventAsync(remoteCalendarId, draft, ct).ConfigureAwait(false);
            ev.RemoteId = created.RemoteId;
            ev.Uid = string.IsNullOrEmpty(created.Uid) ? ev.Uid : created.Uid;
            ev.ETag = created.ChangeTag;
            Stamp(ev);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new EventWriteResult(ev.Id, calendar.Id, WriteDisposition.Applied, null);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            var outboxId = await EnqueueAsync(OutboxOp.Create, ev, calendar.Id, remoteCalendarId, draft, baseETag: null, ct).ConfigureAwait(false);
            _logger.LogInformation(ex, "Provider unreachable creating event {EventId}; queued outbox {OutboxId}.", ev.Id, outboxId);
            return new EventWriteResult(ev.Id, calendar.Id, WriteDisposition.Queued, outboxId);
        }
    }

    public async Task<EventWriteResult> UpdateEventAsync(Guid eventId, EventWriteRequest request, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");
        var calendar = await _db.Calendars.FirstOrDefaultAsync(c => c.Id == ev.CalendarId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Calendar {ev.CalendarId} not found.");
        EnsureWritable(calendar);

        var baseETag = ev.ETag;
        ApplyRequest(ev, request);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var (writer, host, remoteCalendarId) = await ResolveWriterAsync(calendar, ct).ConfigureAwait(false);
        var updated = ToRemote(ev, request);

        try
        {
            await InitializeAsync(writer, host, ct).ConfigureAwait(false);
            var result = await writer.UpdateEventAsync(remoteCalendarId, updated, baseETag, ct).ConfigureAwait(false);
            ev.ETag = result.ChangeTag;
            Stamp(ev);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new EventWriteResult(ev.Id, calendar.Id, WriteDisposition.Applied, null);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            var outboxId = await EnqueueAsync(OutboxOp.Update, ev, calendar.Id, remoteCalendarId, updated, baseETag, ct).ConfigureAwait(false);
            _logger.LogInformation(ex, "Provider unreachable updating event {EventId}; queued outbox {OutboxId}.", ev.Id, outboxId);
            return new EventWriteResult(ev.Id, calendar.Id, WriteDisposition.Queued, outboxId);
        }
    }

    public async Task<EventWriteResult> DeleteEventAsync(Guid eventId, CancellationToken ct)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");
        var calendar = await _db.Calendars.FirstOrDefaultAsync(c => c.Id == ev.CalendarId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Calendar {ev.CalendarId} not found.");
        EnsureWritable(calendar);

        var baseETag = ev.ETag;
        var remoteId = ev.RemoteId;
        var remoteSnapshot = ToRemote(ev, requestForDelete: true);

        var (writer, host, remoteCalendarId) = await ResolveWriterAsync(calendar, ct).ConfigureAwait(false);

        try
        {
            await InitializeAsync(writer, host, ct).ConfigureAwait(false);
            await writer.DeleteEventAsync(remoteCalendarId, remoteId, baseETag, ct).ConfigureAwait(false);
            _db.Events.Remove(ev);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new EventWriteResult(eventId, calendar.Id, WriteDisposition.Applied, null);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // Tombstone locally (remove) but keep the deletion queued so it replays against the provider.
            var outboxId = await EnqueueAsync(OutboxOp.Delete, ev, calendar.Id, remoteCalendarId, remoteSnapshot, baseETag, ct).ConfigureAwait(false);
            _db.Events.Remove(ev);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(ex, "Provider unreachable deleting event {EventId}; queued outbox {OutboxId}.", eventId, outboxId);
            return new EventWriteResult(eventId, calendar.Id, WriteDisposition.Queued, outboxId);
        }
    }

    public async Task<ReplaySummary> ReplayAsync(CancellationToken ct)
    {
        int drained = 0, conflicts = 0, stillPending = 0;

        // FIFO over Pending rows (ARCHITECTURE §8). Re-query each loop so a row marked Done/Conflict drops out.
        var pending = await _db.WriteOutbox
            .Where(w => w.Status == OutboxStatus.Pending && w.EntityType == EventEntityType)
            .OrderBy(w => w.EnqueuedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await ReplayOneAsync(row, ct).ConfigureAwait(false);
            switch (outcome)
            {
                case ReplayOutcome.Done: drained++; break;
                case ReplayOutcome.Conflict: conflicts++; break;
                default: stillPending++; break;
            }
        }

        return new ReplaySummary(drained, conflicts, stillPending);
    }

    private async Task<ReplayOutcome> ReplayOneAsync(WriteOutbox row, CancellationToken ct)
    {
        OutboxEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OutboxEnvelope>(row.Payload, Json)
                ?? throw new JsonException("null payload");
        }
        catch (JsonException ex)
        {
            return await MarkFailedAsync(row, $"corrupt payload: {ex.Message}", ct).ConfigureAwait(false);
        }

        var payload = envelope.Event;
        var calendar = await _db.Calendars.FirstOrDefaultAsync(c => c.Id == envelope.CalendarId, ct).ConfigureAwait(false);
        if (calendar is null)
            return await MarkFailedAsync(row, "owning calendar no longer present", ct).ConfigureAwait(false);

        // The local event still exists for Create/Update replays; a queued Delete already tombstoned it.
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == row.EntityId, ct).ConfigureAwait(false);

        ICalendarWriter writer;
        IPluginHost host;
        string remoteCalendarId;
        try
        {
            (writer, host, remoteCalendarId) = await ResolveWriterAsync(calendar, ct).ConfigureAwait(false);
            await InitializeAsync(writer, host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            return ReplayOutcome.Pending; // still offline — leave queued
        }

        row.Status = OutboxStatus.InFlight;
        row.Attempts++;
        StampRow(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            switch (row.Operation)
            {
                case OutboxOp.Create:
                {
                    var created = await writer.CreateEventAsync(remoteCalendarId, payload, ct).ConfigureAwait(false);
                    if (ev is not null)
                    {
                        ev.RemoteId = created.RemoteId;
                        ev.Uid = string.IsNullOrEmpty(created.Uid) ? ev.Uid : created.Uid;
                        ev.ETag = created.ChangeTag;
                    }
                    break;
                }
                case OutboxOp.Update:
                {
                    var updated = await writer.UpdateEventAsync(remoteCalendarId, payload, row.BaseETag, ct).ConfigureAwait(false);
                    if (ev is not null)
                        ev.ETag = updated.ChangeTag;
                    break;
                }
                case OutboxOp.Delete:
                    await writer.DeleteEventAsync(remoteCalendarId, payload.RemoteId, row.BaseETag, ct).ConfigureAwait(false);
                    break;
            }

            row.Status = OutboxStatus.Done;
            row.LastError = null;
            StampRow(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ReplayOutcome.Done;
        }
        catch (ConcurrencyConflictException ex)
        {
            // 412: the provider's copy diverged. Surface as a conflict; the UI re-fetches and merges (ARCHITECTURE §8).
            row.Status = OutboxStatus.Conflict;
            row.LastError = ex.Message;
            StampRow(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ReplayOutcome.Conflict;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // Still transient — return to Pending for the next replay (backoff is by Attempts).
            row.Status = row.Attempts >= MaxAttempts ? OutboxStatus.Failed : OutboxStatus.Pending;
            row.LastError = ex.Message;
            StampRow(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row.Status == OutboxStatus.Failed ? ReplayOutcome.Failed : ReplayOutcome.Pending;
        }
        catch (Exception ex)
        {
            return await MarkFailedAsync(row, ex.Message, ct).ConfigureAwait(false);
        }
    }

    public async Task<OutboxStatusDto> GetOutboxStatusAsync(CancellationToken ct)
    {
        var rows = await _db.WriteOutbox
            .OrderBy(w => w.EnqueuedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        var items = rows.Select(r => new OutboxItemDto(
            r.Id, r.EntityType, r.EntityId, r.Operation.ToString(), r.Status.ToString(),
            r.Attempts, r.LastError, r.EnqueuedAtUtc)).ToList();

        return new OutboxStatusDto(
            rows.Count,
            rows.Count(r => r.Status == OutboxStatus.Pending),
            rows.Count(r => r.Status == OutboxStatus.InFlight),
            rows.Count(r => r.Status == OutboxStatus.Failed),
            rows.Count(r => r.Status == OutboxStatus.Conflict),
            rows.Count(r => r.Status == OutboxStatus.Done),
            items);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static void EnsureWritable(CalendarEntity calendar)
    {
        if (calendar.IsReadOnly)
            throw new ReadOnlyCalendarException(
                $"Calendar '{calendar.Name}' is read-only and does not accept writes.");
    }

    private async Task<(ICalendarWriter Writer, IPluginHost Host, string RemoteCalendarId)> ResolveWriterAsync(
        CalendarEntity calendar, CancellationToken ct)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == calendar.AccountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Account {calendar.AccountId} not found.");

        if (!_registry.TryGet(account.PluginId, out var registration) ||
            registration.State != PluginState.Running ||
            registration.Instance.Plugin is not ICalendarWriter writer)
        {
            throw new ReadOnlyCalendarException(
                $"No running calendar.write plugin '{account.PluginId}' for calendar {calendar.Id}.");
        }

        var host = await _hostFactory.BuildAsync(account, registration.Manifest, ct).ConfigureAwait(false);
        return (writer, host, calendar.RemoteId);
    }

    private static Task InitializeAsync(ICalendarWriter writer, IPluginHost host, CancellationToken ct) =>
        writer is IPlugin plugin ? plugin.InitializeAsync(host, ct) : Task.CompletedTask;

    private async Task<Guid> EnqueueAsync(
        OutboxOp op, Event ev, Guid calendarId, string remoteCalendarId, RemoteEvent payload, string? baseETag,
        CancellationToken ct)
    {
        var envelope = new OutboxEnvelope(calendarId, remoteCalendarId, payload);
        var row = new WriteOutbox
        {
            Id = Guid.CreateVersion7(),
            EntityType = EventEntityType,
            EntityId = ev.Id,
            Operation = op,
            Payload = JsonSerializer.Serialize(envelope, Json),
            BaseETag = baseETag,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            EnqueuedAtUtc = _clock.GetUtcNow(),
            RowVersion = Guid.NewGuid().ToString("N"),
        };
        _db.WriteOutbox.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    private async Task<ReplayOutcome> MarkFailedAsync(WriteOutbox row, string error, CancellationToken ct)
    {
        row.Status = OutboxStatus.Failed;
        row.LastError = error;
        StampRow(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ReplayOutcome.Failed;
    }

    private static void ApplyRequest(Event ev, EventWriteRequest request)
    {
        ev.Title = request.Title;
        ev.StartUtc = request.StartUtc;
        ev.EndUtc = request.EndUtc;
        ev.AllDay = request.AllDay;
        ev.StartDate = request.AllDay ? DateOnly.FromDateTime(request.StartUtc.UtcDateTime) : null;
        ev.Rrule = request.Rrule;
        ev.Location = request.Location;
        ev.DedupSignature = Domain.Engines.DedupSignatureCalculator.Compute(
            request.Title, DateOnly.FromDateTime(request.StartUtc.UtcDateTime), request.AllDay);
        Stamp(ev);
    }

    private RemoteEvent ToRemote(Event ev, EventWriteRequest request) =>
        new(
            RemoteId: ev.RemoteId,
            Uid: ev.Uid,
            Title: request.Title,
            StartUtc: request.StartUtc,
            EndUtc: request.EndUtc,
            TimeZoneId: null,
            AllDay: request.AllDay,
            Rrule: request.Rrule,
            RecurrenceId: null,
            Location: request.Location,
            Geo: null,
            Categories: request.Categories ?? Array.Empty<string>(),
            ChangeTag: ev.ETag,
            Status: EventStatus.Confirmed);

    private RemoteEvent ToRemote(Event ev, bool requestForDelete) =>
        new(
            RemoteId: ev.RemoteId,
            Uid: ev.Uid,
            Title: ev.Title ?? string.Empty,
            StartUtc: ev.StartUtc,
            EndUtc: ev.EndUtc,
            TimeZoneId: null,
            AllDay: ev.AllDay,
            Rrule: ev.Rrule,
            RecurrenceId: null,
            Location: ev.Location,
            Geo: null,
            Categories: Array.Empty<string>(),
            ChangeTag: ev.ETag,
            Status: EventStatus.Cancelled);

    /// <summary>An unreachable/transient provider — queue offline. A 412 conflict is NOT unreachable.</summary>
    private static bool IsUnreachable(Exception ex) =>
        ex is HttpRequestException
            or TaskCanceledException
            or TimeoutException
            or System.Net.Sockets.SocketException;

    private static void Stamp(Event ev) =>
        ev.RowVersion = Guid.NewGuid().ToString("N");

    private static void StampRow(WriteOutbox row) => row.RowVersion = Guid.NewGuid().ToString("N");

    private enum ReplayOutcome { Done, Conflict, Pending, Failed }

    /// <summary>
    /// The self-contained payload an outbox row replays from (ARCHITECTURE §8): the owning calendar (so replay
    /// resolves the writer even after the local event is tombstoned by a queued delete), the provider calendar
    /// id, and the normalized mutation.
    /// </summary>
    private sealed record OutboxEnvelope(Guid CalendarId, string RemoteCalendarId, RemoteEvent Event);
}
