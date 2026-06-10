using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Calendar.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct the context without booting the API
/// (DATA-SCHEMA §7). The connection string here is only used to build the model — never at runtime.
/// </summary>
public sealed class CalendarDbContextFactory : IDesignTimeDbContextFactory<CalendarDbContext>
{
    public CalendarDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CalendarDbContext>()
            .UseSqlite("Data Source=calendar-design.db")
            .Options;
        return new CalendarDbContext(options);
    }
}
