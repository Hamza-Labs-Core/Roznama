using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// The scheduled sync engine (ROADMAP Phase 3, ARCHITECTURE §10). Cadence and health live in the
/// account-level <see cref="SyncState"/> row (CalendarId = null — per-calendar rows keep holding the
/// provider delta cursors): an account is due when NextRunAtUtc has passed and it isn't in backoff.
/// Failures back off exponentially per account (base × 2^(attempts−1), capped), so one provider's outage
/// never throttles the others; repeated failures surface as <see cref="AccountStatus.Error"/> in the sidebar.
/// </summary>
public sealed class SyncScheduler : ISyncScheduler
{
    private const int MaxErrorLength = 500;

    private readonly CalendarDbContext _db;
    private readonly ICalendarSyncService _sync;
    private readonly DeviceProvider _device;
    private readonly SyncSchedulerOptions _options;
    private readonly ILogger<SyncScheduler> _logger;

    public SyncScheduler(
        CalendarDbContext db, ICalendarSyncService sync, DeviceProvider device,
        IOptions<SyncSchedulerOptions> options, ILogger<SyncScheduler> logger)
    {
        _db = db;
        _sync = sync;
        _device = device;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SyncSweepSummary> SweepAsync(bool force, CancellationToken ct)
    {
        // Error accounts stay in the rotation (backoff governs their cadence); NeedsAuth/Disabled need a
        // user action first and would fail every round, so they sit out until reconnected.
        var accountIds = await _db.Accounts
            .Where(a => !a.IsDeleted && (a.Status == AccountStatus.Connected || a.Status == AccountStatus.Error))
            .OrderBy(a => a.Priority)
            .Select(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        int synced = 0, failed = 0, skipped = 0;
        foreach (var accountId in accountIds)
        {
            ct.ThrowIfCancellationRequested();

            var state = await GetOrCreateAccountStateAsync(accountId, ct).ConfigureAwait(false);
            if (!force && !IsDue(state, DateTimeOffset.UtcNow))
            {
                skipped++;
                continue;
            }

            try
            {
                await RunWithBookkeepingAsync(accountId, state, ct).ConfigureAwait(false);
                synced++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failed++;     // backoff already recorded; one account's outage never aborts the sweep.
            }
        }

        return new SyncSweepSummary(accountIds.Count, synced, failed, skipped);
    }

    public async Task<SyncSummary?> SyncNowAsync(Guid accountId, CancellationToken ct)
    {
        var exists = await _db.Accounts.AnyAsync(a => a.Id == accountId && !a.IsDeleted, ct).ConfigureAwait(false);
        if (!exists)
            return null;

        var state = await GetOrCreateAccountStateAsync(accountId, ct).ConfigureAwait(false);
        return await RunWithBookkeepingAsync(accountId, state, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncStatusDto>> GetStatusAsync(CancellationToken ct)
    {
        var rows = await (
            from a in _db.Accounts
            where !a.IsDeleted
            join s in _db.SyncStates.Where(s => s.CalendarId == null)
                on a.Id equals s.AccountId into states
            from s in states.DefaultIfEmpty()
            orderby a.Priority
            select new { Account = a, State = s })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new SyncStatusDto(
            r.Account.Id,
            r.Account.DisplayName,
            r.Account.PluginId,
            r.Account.Status.ToString(),
            (r.State?.State ?? SyncRunState.Idle).ToString(),
            r.State?.LastSyncAtUtc ?? r.Account.LastSyncAtUtc,
            r.State?.NextRunAtUtc,
            r.State?.BackoffUntilUtc,
            r.State?.Attempts ?? 0,
            r.State?.LastError)).ToList();
    }

    private static bool IsDue(SyncState state, DateTimeOffset now)
    {
        if (state.BackoffUntilUtc is { } backoff && backoff > now)
            return false;
        return state.NextRunAtUtc is null || state.NextRunAtUtc <= now;
    }

    /// <summary>Runs one account's sync and records the outcome; failures set backoff and rethrow.</summary>
    private async Task<SyncSummary> RunWithBookkeepingAsync(Guid accountId, SyncState state, CancellationToken ct)
    {
        state.State = SyncRunState.Running;
        Touch(state);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var summary = await _sync.SyncAccountAsync(accountId, ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            state.State = SyncRunState.Idle;
            state.Attempts = 0;
            state.LastError = null;
            state.BackoffUntilUtc = null;
            state.LastSyncAtUtc = now;
            state.NextRunAtUtc = now + _options.Interval;
            Touch(state);
            await SetAccountStatusAsync(accountId, AccountStatus.Connected, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Synced account {AccountId}: {Calendars} calendars, {Upserts} upserts, {Deletes} deletes; next run {NextRun}.",
                accountId, summary.Calendars, summary.Upserts, summary.Deletes, state.NextRunAtUtc);
            return summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed sync can leave half-tracked entities behind; drop them so the bookkeeping write
            // persists only the scheduling row (per-calendar progress was already saved incrementally).
            _db.ChangeTracker.Clear();
            var fresh = await _db.SyncStates.FirstAsync(s => s.Id == state.Id, ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            fresh.Attempts = state.Attempts + 1;
            var delay = BackoffDelay(fresh.Attempts);
            fresh.State = SyncRunState.Backoff;
            fresh.LastError = ex.Message.Length > MaxErrorLength ? ex.Message[..MaxErrorLength] : ex.Message;
            fresh.BackoffUntilUtc = now + delay;
            fresh.NextRunAtUtc = fresh.BackoffUntilUtc;
            Touch(fresh);

            if (fresh.Attempts >= _options.ErrorAfterAttempts)
                await SetAccountStatusAsync(accountId, AccountStatus.Error, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Keep the caller's detached copy coherent for the rest of the sweep.
            state.Attempts = fresh.Attempts;
            state.BackoffUntilUtc = fresh.BackoffUntilUtc;
            state.NextRunAtUtc = fresh.NextRunAtUtc;

            _logger.LogWarning(
                ex, "Sync failed for account {AccountId} (attempt {Attempts}); backing off until {BackoffUntil}.",
                accountId, fresh.Attempts, fresh.BackoffUntilUtc);
            throw;
        }
    }

    /// <summary>base × 2^(attempts−1), capped at <see cref="SyncSchedulerOptions.BackoffMax"/>.</summary>
    private TimeSpan BackoffDelay(int attempts)
    {
        var shift = Math.Clamp(attempts - 1, 0, 20);    // 2^20 ≫ any sane cap; avoids tick overflow.
        var ticks = _options.BackoffBase.Ticks << shift;
        return ticks >= _options.BackoffMax.Ticks || ticks <= 0 ? _options.BackoffMax : TimeSpan.FromTicks(ticks);
    }

    private async Task<SyncState> GetOrCreateAccountStateAsync(Guid accountId, CancellationToken ct)
    {
        var state = await _db.SyncStates
            .FirstOrDefaultAsync(s => s.AccountId == accountId && s.CalendarId == null, ct)
            .ConfigureAwait(false);
        if (state is not null)
            return state;

        state = new SyncState
        {
            Id = Guid.CreateVersion7(),
            AccountId = accountId,
            CalendarId = null,
            State = SyncRunState.Idle,
        };
        Touch(state);
        _db.SyncStates.Add(state);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return state;
    }

    private async Task SetAccountStatusAsync(Guid accountId, AccountStatus status, CancellationToken ct)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false);
        if (account is null || account.Status == status)
            return;

        account.Status = status;
        account.UpdatedAtUtc = DateTimeOffset.UtcNow;
        account.DeviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        account.Lamport++;
    }

    private static void Touch(SyncState state) => state.RowVersion = Guid.NewGuid().ToString("N");
}

/// <summary>Cadence + backoff knobs for the sync scheduler (bound from the <c>SyncScheduler</c> config section).</summary>
public sealed class SyncSchedulerOptions
{
    /// <summary>How often each account re-syncs after a successful run.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>First-failure backoff; doubles per consecutive failure.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Backoff ceiling.</summary>
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Consecutive failures before the account's sidebar status flips to Error.</summary>
    public int ErrorAfterAttempts { get; set; } = 3;
}
