using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Deterministic, reversible duplicate grouping (ARCHITECTURE §12). Events sharing a
/// <c>DedupSignature</c> across <b>different calendars</b> collapse into one <see cref="DuplicateGroup"/>;
/// the canonical is the member whose account has the highest priority (lowest <c>Priority</c> value).
/// Re-runs idempotently after each sync.
/// </summary>
public sealed class DedupGrouper
{
    private readonly CalendarDbContext _db;

    public DedupGrouper(CalendarDbContext db) => _db = db;

    public async Task RegroupAsync(CancellationToken ct)
    {
        var events = await _db.Events
            .Where(e => e.DedupSignature != null)
            .ToListAsync(ct).ConfigureAwait(false);

        // Account priority per event (via calendar → account). Lower = higher priority.
        var calendarToAccount = await _db.Calendars
            .ToDictionaryAsync(c => c.Id, c => c.AccountId, ct).ConfigureAwait(false);
        var accountPriority = await _db.Accounts
            .ToDictionaryAsync(a => a.Id, a => a.Priority, ct).ConfigureAwait(false);

        var groups = await _db.DuplicateGroups.ToListAsync(ct).ConfigureAwait(false);
        var groupBySignature = groups.ToDictionary(g => g.Signature, StringComparer.Ordinal);

        // Reset memberships; we recompute from scratch (cheap for a local store).
        foreach (var e in events)
            e.DuplicateGroupId = null;

        foreach (var bySignature in events.GroupBy(e => e.DedupSignature!))
        {
            var members = bySignature.ToList();
            var distinctCalendars = members.Select(m => m.CalendarId).Distinct().Count();

            // A duplicate only exists when the same signature appears across more than one calendar.
            if (distinctCalendars < 2)
            {
                if (groupBySignature.TryGetValue(bySignature.Key, out var stale))
                    _db.DuplicateGroups.Remove(stale);
                continue;
            }

            if (!groupBySignature.TryGetValue(bySignature.Key, out var group))
            {
                group = new DuplicateGroup
                {
                    Id = Guid.CreateVersion7(),
                    Signature = bySignature.Key,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Lamport = 0,
                };
                _db.DuplicateGroups.Add(group);
                groupBySignature[bySignature.Key] = group;
            }

            var canonical = members
                .OrderBy(m => Priority(m, calendarToAccount, accountPriority))
                .ThenBy(m => m.Id)
                .First();

            group.CanonicalEventId = canonical.Id;
            foreach (var m in members)
                m.DuplicateGroupId = group.Id;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static int Priority(
        Event e,
        IReadOnlyDictionary<Guid, Guid> calendarToAccount,
        IReadOnlyDictionary<Guid, int> accountPriority) =>
        calendarToAccount.TryGetValue(e.CalendarId, out var accountId) &&
        accountPriority.TryGetValue(accountId, out var priority)
            ? priority
            : int.MaxValue;
}
