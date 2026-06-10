using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Seeds the built-in categories (DATA-SCHEMA §7) with stable well-known GUIDs so "hide all Birthdays" works
/// before any event exists. Idempotent — safe to run at every startup.
/// </summary>
public static class CategorySeeder
{
    private static readonly (string Name, string Id)[] BuiltIns =
    {
        ("Work",     "00000000-0000-0000-0000-0000000000c1"),
        ("Personal", "00000000-0000-0000-0000-0000000000c2"),
        ("Birthday", "00000000-0000-0000-0000-0000000000c3"),
        ("Holiday",  "00000000-0000-0000-0000-0000000000c4"),
        ("Travel",   "00000000-0000-0000-0000-0000000000c5"),
        ("Busy",     "00000000-0000-0000-0000-0000000000c6"),
    };

    public static async Task EnsureAsync(CalendarDbContext db, Guid deviceId, CancellationToken ct)
    {
        var existing = await db.Categories.Select(c => c.Name).ToListAsync(ct).ConfigureAwait(false);
        var present = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, id) in BuiltIns)
        {
            if (present.Contains(name))
                continue;

            db.Categories.Add(new Category
            {
                Id = Guid.Parse(id),
                Name = name,
                IsVisible = true,
                IsBuiltIn = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                DeviceId = deviceId,
                Lamport = 0,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
