using System.Text.Json;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Deterministic, reversible duplicate grouping (ARCHITECTURE §12). Events sharing a
/// <c>DedupSignature</c> across <b>different calendars</b> collapse into one <see cref="DuplicateGroup"/>;
/// the canonical is the member whose account has the highest priority (lowest <c>Priority</c> value).
/// User <see cref="DuplicateOverride"/>s (bound by iCal UID so they survive provider re-sync) bend the
/// automatic result, in this order:
/// <list type="bullet">
///   <item><see cref="OverrideKind.ForceMerge"/> — the listed UIDs form one group regardless of signature
///     (claimed before signature grouping; an event belongs to at most one group).</item>
///   <item><see cref="OverrideKind.NeverMerge"/> — the listed UIDs sit out automatic signature grouping
///     entirely ("this is actually a different event").</item>
///   <item><see cref="OverrideKind.SetCanonical"/> — the listed UID wins canonical in whatever group it
///     lands in, beating account priority.</item>
/// </list>
/// Re-runs idempotently after each sync; removing an override (tombstone) restores the automatic answer.
/// </summary>
public sealed class DedupGrouper
{
    /// <summary>Synthetic group key for a force-merge (never collides with a hex signature).</summary>
    private const string ForceMergeKeyPrefix = "override:";

    private readonly CalendarDbContext _db;

    public DedupGrouper(CalendarDbContext db) => _db = db;

    public async Task RegroupAsync(CancellationToken ct)
    {
        // Include events whose signature became null but still reference a group, so their stale
        // membership is reset rather than left pointing at a row this pass may delete.
        var events = await _db.Events
            .Where(e => e.DedupSignature != null || e.DuplicateGroupId != null)
            .ToListAsync(ct).ConfigureAwait(false);

        // Account priority per event (via calendar → account). Lower = higher priority.
        var calendarToAccount = await _db.Calendars
            .ToDictionaryAsync(c => c.Id, c => c.AccountId, ct).ConfigureAwait(false);
        var accountPriority = await _db.Accounts
            .ToDictionaryAsync(a => a.Id, a => a.Priority, ct).ConfigureAwait(false);

        // User overrides (the !IsDeleted query filter hides tombstoned ones — undo = automatic again).
        var (neverMergeUids, forceMerges, canonicalUids) = await LoadOverridesAsync(ct).ConfigureAwait(false);

        // Reset memberships; we recompute from scratch (cheap for a local store).
        foreach (var e in events)
            e.DuplicateGroupId = null;

        // The grouping we want, keyed by signature (auto) or by the force-merge override key.
        var desired = new Dictionary<string, List<Event>>(StringComparer.Ordinal);

        // 1. Force-merges claim their members first — an event belongs to at most one group. A merge that
        //    currently matches fewer than 2 events claims NOTHING (its lone member must keep participating
        //    in automatic grouping, not silently degrade into a never-merge).
        var claimed = new HashSet<Guid>();
        foreach (var (key, uids) in forceMerges)
        {
            var members = events
                .Where(e => e.DedupSignature != null && uids.Contains(e.Uid) && !claimed.Contains(e.Id))
                .ToList();
            if (members.Count < 2)
                continue;
            foreach (var m in members)
                claimed.Add(m.Id);
            desired[key] = members;
        }

        // 2. Automatic signature groups over the unclaimed, non-never-merge remainder.
        var groupable = events.Where(e =>
            e.DedupSignature != null && !claimed.Contains(e.Id) && !neverMergeUids.Contains(e.Uid));
        foreach (var bySignature in groupable.GroupBy(e => e.DedupSignature!))
        {
            var members = bySignature.ToList();

            // A duplicate only exists when the same signature appears across more than one calendar.
            if (members.Select(m => m.CalendarId).Distinct().Count() < 2)
                continue;
            desired[bySignature.Key] = members;
        }

        // 3. Reconcile the DuplicateGroup rows with the desired keys.
        var groups = await _db.DuplicateGroups.ToListAsync(ct).ConfigureAwait(false);
        var groupByKey = groups.ToDictionary(g => g.Signature, StringComparer.Ordinal);
        foreach (var stale in groups.Where(g => !desired.ContainsKey(g.Signature)))
            _db.DuplicateGroups.Remove(stale);

        foreach (var (key, members) in desired)
        {
            if (!groupByKey.TryGetValue(key, out var group))
            {
                group = new DuplicateGroup
                {
                    Id = Guid.CreateVersion7(),
                    Signature = key,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Lamport = 0,
                };
                _db.DuplicateGroups.Add(group);
                groupByKey[key] = group;
            }

            // SetCanonical wins among members; account priority (then id, for determinism) decides otherwise.
            var ordered = members
                .OrderBy(m => Priority(m, calendarToAccount, accountPriority))
                .ThenBy(m => m.Id)
                .ToList();
            var canonical = ordered.FirstOrDefault(m => canonicalUids.Contains(m.Uid)) ?? ordered[0];

            group.CanonicalEventId = canonical.Id;
            foreach (var m in members)
                m.DuplicateGroupId = group.Id;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<(HashSet<string> NeverMerge, List<(string Key, HashSet<string> Uids)> ForceMerges,
        HashSet<string> Canonical)> LoadOverridesAsync(CancellationToken ct)
    {
        // Deterministic claim order: when two ForceMerge overrides list the same UID, the older one wins
        // every run (an unordered query could flip groups between runs with no data change).
        var overrides = await _db.DuplicateOverrides
            .OrderBy(o => o.CreatedAtUtc).ThenBy(o => o.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var neverMerge = new HashSet<string>(StringComparer.Ordinal);
        var forceMerges = new List<(string Key, HashSet<string> Uids)>();
        var canonical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in overrides)
        {
            var uids = ParseUids(o.EventIds);
            switch (o.Kind)
            {
                case OverrideKind.NeverMerge:
                    neverMerge.UnionWith(uids);
                    break;
                case OverrideKind.ForceMerge:
                    forceMerges.Add(($"{ForceMergeKeyPrefix}{o.Id:N}", uids.ToHashSet(StringComparer.Ordinal)));
                    break;
                case OverrideKind.SetCanonical:
                    canonical.UnionWith(uids);
                    break;
            }
        }
        return (neverMerge, forceMerges, canonical);
    }

    private static IReadOnlyList<string> ParseUids(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();   // a malformed override never breaks regrouping.
        }
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
