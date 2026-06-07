using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Resolves this install's <see cref="Device"/> id, used to stamp the sync-metadata quartet on every
/// user-metadata write (DATA-SCHEMA §2.8, §7). Created on first use.
/// </summary>
public sealed class DeviceProvider
{
    private readonly CalendarDbContext _db;

    public DeviceProvider(CalendarDbContext db) => _db = db;

    public async Task<Guid> GetDeviceIdAsync(CancellationToken ct)
    {
        var existing = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is not null)
            return existing.Id;

        var device = new Device
        {
            Id = Guid.CreateVersion7(),
            Name = Environment.MachineName,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _db.Devices.Add(device);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return device.Id;
    }
}
