using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>Lists calendars/categories and toggles their visibility (UI-WIREFRAMES §1; ARCHITECTURE §13).</summary>
public sealed class CalendarCatalog : ICalendarCatalog
{
    private readonly CalendarDbContext _db;
    private readonly DeviceProvider _device;

    public CalendarCatalog(CalendarDbContext db, DeviceProvider device)
    {
        _db = db;
        _device = device;
    }

    public async Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(CancellationToken ct) =>
        await _db.Calendars
            .OrderBy(c => c.Name)
            .Select(c => new CalendarDto(c.Id, c.AccountId, c.Name, c.Color, c.IsVisible, c.IsReadOnly))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> SetCalendarVisibilityAsync(Guid calendarId, bool isVisible, CancellationToken ct)
    {
        var calendar = await _db.Calendars.FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);
        if (calendar is null)
            return false;

        calendar.IsVisible = isVisible;
        await StampAndSaveAsync(calendar, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct) =>
        await _db.Categories
            .OrderByDescending(c => c.IsBuiltIn).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.IsVisible, c.IsBuiltIn))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> SetCategoryVisibilityAsync(Guid categoryId, bool isVisible, CancellationToken ct)
    {
        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct).ConfigureAwait(false);
        if (category is null)
            return false;

        category.IsVisible = isVisible;
        await StampAndSaveAsync(category, ct).ConfigureAwait(false);
        return true;
    }

    private async Task StampAndSaveAsync(ISyncEntity entity, CancellationToken ct)
    {
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        entity.DeviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        entity.Lamport++;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
