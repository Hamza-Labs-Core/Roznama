using System.Text.Json;
using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// The duplicate merge/split override surface (ARCHITECTURE §12; the Phase 1 follow-up). Overrides bind
/// events by iCal UID — not row id — so a provider re-sync that recreates rows can't orphan a user's
/// correction, and they carry the sync quartet so corrections replicate like other user state. Every
/// mutation re-runs the <see cref="DedupGrouper"/> so the projection reflects the change immediately.
/// </summary>
public sealed class DuplicateService : IDuplicateService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CalendarDbContext _db;
    private readonly DedupGrouper _grouper;
    private readonly DeviceProvider _device;

    public DuplicateService(CalendarDbContext db, DedupGrouper grouper, DeviceProvider device)
    {
        _db = db;
        _grouper = grouper;
        _device = device;
    }

    public async Task<DuplicateGroupDto?> GetGroupForEventAsync(Guid eventId, CancellationToken ct)
    {
        var ev = await _db.Events.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);
        if (ev?.DuplicateGroupId is not { } groupId)
            return null;

        var groupRow = await _db.DuplicateGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId, ct).ConfigureAwait(false);
        if (groupRow is null)
            return null;

        var canonicalId = groupRow.CanonicalEventId;
        var members = await (
            from e in _db.Events
            join c in _db.Calendars on e.CalendarId equals c.Id
            join a in _db.Accounts on c.AccountId equals a.Id
            where e.DuplicateGroupId == groupId
            orderby a.Priority, e.Id
            select new DuplicateMemberDto(
                e.Id, e.Uid, e.Title ?? string.Empty, e.StartUtc,
                c.Name, a.DisplayName, e.Id == canonicalId))
            .ToListAsync(ct).ConfigureAwait(false);

        return new DuplicateGroupDto(groupId, members);
    }

    public async Task<DuplicateOverrideDto> CreateOverrideAsync(
        OverrideKind kind, IReadOnlyList<string> uids, string? reason, CancellationToken ct)
    {
        var cleaned = (uids ?? Array.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var minimum = kind switch
        {
            OverrideKind.ForceMerge => 2,
            OverrideKind.NeverMerge => 1,
            OverrideKind.SetCanonical => 1,
            _ => throw new ArgumentException($"Unknown override kind '{kind}'.", nameof(kind)),
        };
        if (cleaned.Count < minimum)
            throw new ArgumentException($"{kind} needs at least {minimum} event UID(s).", nameof(uids));
        if (kind == OverrideKind.SetCanonical && cleaned.Count != 1)
            throw new ArgumentException("SetCanonical takes exactly one event UID.", nameof(uids));

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var entity = new DuplicateOverride
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            EventIds = JsonSerializer.Serialize(cleaned, Json),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            Lamport = 1,
        };
        _db.DuplicateOverrides.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _grouper.RegroupAsync(ct).ConfigureAwait(false);
        return ToDto(entity, cleaned);
    }

    public async Task<IReadOnlyList<DuplicateOverrideDto>> ListOverridesAsync(CancellationToken ct)
    {
        var rows = await _db.DuplicateOverrides
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(o => ToDto(o, ParseUids(o.EventIds))).ToList();
    }

    public async Task<bool> RemoveOverrideAsync(Guid id, CancellationToken ct)
    {
        var entity = await _db.DuplicateOverrides
            .FirstOrDefaultAsync(o => o.Id == id, ct).ConfigureAwait(false);
        if (entity is null)
            return false;

        // Tombstone, not delete — reversible by design and replicable like other user state.
        entity.IsDeleted = true;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        entity.DeviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        entity.Lamport++;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _grouper.RegroupAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static DuplicateOverrideDto ToDto(DuplicateOverride o, IReadOnlyList<string> uids) =>
        new(o.Id, o.Kind.ToString(), uids, o.Reason, o.CreatedAtUtc);

    private static IReadOnlyList<string> ParseUids(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
