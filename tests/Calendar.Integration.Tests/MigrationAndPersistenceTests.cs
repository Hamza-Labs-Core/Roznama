using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 0 acceptance: the InitialSchema migration applies to a real SQLite file and the EF mapping
/// round-trips — primitive collections, enum-as-string, UTC instant conversion, and the ISyncEntity
/// tombstone query filter (DATA-SCHEMA §5, §6).
/// </summary>
public sealed class MigrationAndPersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-ef-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewContext() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public void Migration_applies_and_data_round_trips()
    {
        using (var ctx = NewContext())
        {
            ctx.Database.Migrate();

            ctx.Plugins.Add(new PluginEntity
            {
                Id = "org.unifiedcalendar.ics",
                Name = "ICS",
                Version = "1.0.0",
                SdkVersion = "1.x",
                Kind = PluginKind.Assembly,
                Status = PluginStatus.Installed,
                TrustTier = TrustTier.InBox,
                Capabilities = { "calendar.read" },
                Manifest = "{}",
                InstalledAtUtc = new DateTimeOffset(2026, 6, 1, 9, 30, 0, TimeSpan.FromHours(2)),
            });
            ctx.SaveChanges();
        }

        using (var ctx = NewContext())
        {
            var plugin = ctx.Plugins.Single();
            Assert.Equal(PluginKind.Assembly, plugin.Kind);
            Assert.Equal(new[] { "calendar.read" }, plugin.Capabilities);
            // Stored UTC; the +02:00 wall time becomes 07:30Z.
            Assert.Equal(TimeSpan.Zero, plugin.InstalledAtUtc.Offset);
            Assert.Equal(new DateTimeOffset(2026, 6, 1, 7, 30, 0, TimeSpan.Zero), plugin.InstalledAtUtc);
        }
    }

    [Fact]
    public void Enums_persist_as_text_and_tombstones_are_filtered()
    {
        using var ctx = NewContext();
        ctx.Database.Migrate();

        var accountId = Guid.CreateVersion7();
        ctx.Plugins.Add(new PluginEntity
        {
            Id = "p", Name = "P", Version = "1.0.0", SdkVersion = "1.x",
            Kind = PluginKind.Assembly, Status = PluginStatus.Installed, TrustTier = TrustTier.InBox,
            Manifest = "{}", InstalledAtUtc = DateTimeOffset.UtcNow,
        });
        ctx.Accounts.Add(new Account
        {
            Id = accountId, PluginId = "p", DisplayName = "My ICS",
            Priority = 0, Status = AccountStatus.Connected,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.CreateVersion7(), Lamport = 1,
        });
        ctx.SaveChanges();

        // Enum stored as its member name, not an ordinal.
        var status = ctx.Database
            .SqlQueryRaw<string>("SELECT Status AS Value FROM Account WHERE Id = {0}", accountId.ToString("D"))
            .AsEnumerable()
            .Single();
        Assert.Equal("Connected", status);

        // Tombstone → filtered out of the default query (DATA-SCHEMA §2.8).
        ctx.Accounts.Single().IsDeleted = true;
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();

        Assert.Empty(ctx.Accounts);
        Assert.Single(ctx.Accounts.IgnoreQueryFilters());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
