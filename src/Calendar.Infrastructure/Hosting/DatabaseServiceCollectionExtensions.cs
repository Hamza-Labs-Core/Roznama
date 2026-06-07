using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Infrastructure.Hosting;

/// <summary>Registers the on-device EF Core SQLite store (DATA-SCHEMA §7).</summary>
public static class DatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="CalendarDbContext"/> on SQLite. The connection string comes from
    /// <c>ConnectionStrings:Calendar</c> or defaults to a local <c>calendar.db</c>. SQLCipher keying is layered
    /// on in a later phase (DATA-SCHEMA §5.5).
    /// </summary>
    public static IServiceCollection AddCalendarDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Calendar") ?? "Data Source=calendar.db";
        services.AddDbContext<CalendarDbContext>(options => options
            .UseSqlite(connectionString)
            // Provider-cache entities (Event) intentionally reference sync-filtered user-metadata entities
            // (Calendar) — a hidden calendar's events simply won't render. The interaction is by design.
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
        return services;
    }
}
