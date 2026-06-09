using System.Linq.Expressions;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using CalendarEntity = Calendar.Domain.Entities.Calendar;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Infrastructure.Persistence;

/// <summary>
/// The on-device EF Core store (DATA-SCHEMA.md). One DbContext over the full catalog: plugins/accounts,
/// the secrets vault, calendars/events/categories, duplicates, places/geocoding/routing, trips/fares/drafts,
/// sharing, and sync/outbox state. Mapping lives here (Infrastructure); the entities stay POCO in Domain.
/// </summary>
public sealed class CalendarDbContext : DbContext
{
    public CalendarDbContext(DbContextOptions<CalendarDbContext> options) : base(options) { }

    public DbSet<PluginEntity> Plugins => Set<PluginEntity>();
    public DbSet<PluginConfig> PluginConfigs => Set<PluginConfig>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<SecretRef> Secrets => Set<SecretRef>();
    public DbSet<CalendarEntity> Calendars => Set<CalendarEntity>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<EventCategory> EventCategories => Set<EventCategory>();
    public DbSet<DuplicateGroup> DuplicateGroups => Set<DuplicateGroup>();
    public DbSet<DuplicateOverride> DuplicateOverrides => Set<DuplicateOverride>();
    public DbSet<Place> Places => Set<Place>();
    public DbSet<GeocodeCache> GeocodeCache => Set<GeocodeCache>();
    public DbSet<RouteLeg> RouteLegs => Set<RouteLeg>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<TripItem> TripItems => Set<TripItem>();
    public DbSet<FareWatch> FareWatches => Set<FareWatch>();
    public DbSet<FareSample> FareSamples => Set<FareSample>();
    public DbSet<NotificationLog> Notifications => Set<NotificationLog>();
    public DbSet<ScenarioDraft> ScenarioDrafts => Set<ScenarioDraft>();
    public DbSet<Share> Shares => Set<Share>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<CloudSyncConfig> CloudSyncConfigs => Set<CloudSyncConfig>();
    public DbSet<RelaySpace> RelaySpaces => Set<RelaySpace>();
    public DbSet<RelayBlob> RelayBlobs => Set<RelayBlob>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<WriteOutbox> WriteOutbox => Set<WriteOutbox>();
    public DbSet<Device> Devices => Set<Device>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Global value converters (DATA-SCHEMA §5.1): UTC ISO text for instants, lower-case D text for GUIDs.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcStringConverter>();
        configurationBuilder.Properties<Guid>().HaveConversion<GuidToStringConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        // 2.1 Plugins & accounts
        b.Entity<PluginEntity>(e =>
        {
            e.ToTable("Plugin");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();
            e.PrimitiveCollection(p => p.Capabilities); // JSON array column
        });

        b.Entity<PluginConfig>(e =>
        {
            e.ToTable("PluginConfig");
            e.HasKey(p => p.Id);
            e.HasOne<PluginEntity>().WithMany().HasForeignKey(p => p.PluginId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.PluginId).IsUnique().HasDatabaseName("UX_PluginConfig_PluginId");
        });

        b.Entity<Account>(e =>
        {
            e.ToTable("Account");
            e.HasKey(a => a.Id);
            e.HasOne<PluginEntity>().WithMany().HasForeignKey(a => a.PluginId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SecretRef>().WithMany().HasForeignKey(a => a.AuthRef).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(a => a.PluginId).HasDatabaseName("IX_Account_PluginId");
            e.HasIndex(a => a.Priority).HasDatabaseName("IX_Account_Priority");
        });

        // 2.2 Secrets vault
        b.Entity<SecretRef>(e =>
        {
            e.ToTable("SecretRef");
            e.HasKey(s => s.Id);
            e.Property(s => s.RowVersion).IsConcurrencyToken();
            e.HasIndex(s => s.Kind).HasDatabaseName("IX_SecretRef_Kind");
        });

        // 2.3 Calendars, events, categories
        b.Entity<CalendarEntity>(e =>
        {
            e.ToTable("Calendar");
            e.HasKey(c => c.Id);
            e.HasOne<Account>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.AccountId, c.RemoteId }).IsUnique().HasDatabaseName("UX_Calendar_Account_RemoteId");
            e.HasIndex(c => c.AccountId).HasDatabaseName("IX_Calendar_AccountId");
        });

        b.Entity<Event>(e =>
        {
            e.ToTable("Event");
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.RowVersion).IsConcurrencyToken();

            e.HasOne(ev => ev.Calendar).WithMany().HasForeignKey(ev => ev.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ev => ev.Place).WithMany().HasForeignKey(ev => ev.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(ev => ev.Master).WithMany(ev => ev.Overrides).HasForeignKey(ev => ev.MasterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<DuplicateGroup>().WithMany().HasForeignKey(ev => ev.DuplicateGroupId).OnDelete(DeleteBehavior.SetNull);

            e.HasMany(ev => ev.Categories).WithMany().UsingEntity<EventCategory>(
                r => r.HasOne<Category>().WithMany().HasForeignKey(ec => ec.CategoryId).OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<Event>().WithMany().HasForeignKey(ec => ec.EventId).OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("EventCategory");
                    j.HasKey(ec => new { ec.EventId, ec.CategoryId });
                    j.HasIndex(ec => ec.CategoryId).HasDatabaseName("IX_EventCategory_CategoryId");
                });

            e.HasIndex(ev => new { ev.CalendarId, ev.StartUtc, ev.EndUtc }).HasDatabaseName("IX_Event_Calendar_Time");
            e.HasIndex(ev => new { ev.CalendarId, ev.RemoteId }).IsUnique().HasDatabaseName("UX_Event_Calendar_RemoteId");
            e.HasIndex(ev => ev.DedupSignature).HasDatabaseName("IX_Event_DedupSignature")
                .HasFilter("\"DedupSignature\" IS NOT NULL");
            e.HasIndex(ev => ev.CalendarId).HasDatabaseName("IX_Event_Recurring")
                .HasFilter("\"Rrule\" IS NOT NULL");
            e.HasIndex(ev => ev.MasterId).HasDatabaseName("IX_Event_Master")
                .HasFilter("\"MasterId\" IS NOT NULL");
            e.HasIndex(ev => ev.Uid).HasDatabaseName("IX_Event_Uid");
            e.HasIndex(ev => ev.PlaceId).HasDatabaseName("IX_Event_PlaceId");
        });

        b.Entity<Category>(e =>
        {
            e.ToTable("Category");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.Name).IsUnique().HasDatabaseName("UX_Category_Name");
            e.HasIndex(c => c.IsVisible).HasDatabaseName("IX_Category_IsVisible");
        });

        b.Entity<EventCategory>(e => e.Property(ec => ec.AssignedBy).HasConversion<string>());

        // 2.4 Duplicates
        b.Entity<DuplicateGroup>(e =>
        {
            e.ToTable("DuplicateGroup");
            e.HasKey(d => d.Id);
            e.HasOne<Event>().WithMany().HasForeignKey(d => d.CanonicalEventId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(d => d.Signature).IsUnique().HasDatabaseName("UX_DuplicateGroup_Signature");
        });

        b.Entity<DuplicateOverride>(e =>
        {
            e.ToTable("DuplicateOverride");
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.Kind).HasDatabaseName("IX_DuplicateOverride_Kind");
        });

        // 2.5 Places, geocoding, routing
        b.Entity<Place>(e =>
        {
            e.ToTable("Place");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.NormalizedKey).IsUnique().HasDatabaseName("UX_Place_NormalizedKey");
            e.HasIndex(p => new { p.Lat, p.Lng }).HasDatabaseName("IX_Place_LatLng");
        });

        b.Entity<GeocodeCache>(e =>
        {
            e.ToTable("GeocodeCache");
            e.HasKey(g => g.Id);
            e.HasIndex(g => g.QueryHash).IsUnique().HasDatabaseName("UX_GeocodeCache_QueryHash");
        });

        b.Entity<RouteLeg>(e =>
        {
            e.ToTable("RouteLeg");
            e.HasKey(r => r.Id);
            e.Property(r => r.Mode).HasConversion<string>();
            e.HasOne<Event>().WithMany().HasForeignKey(r => r.FromEventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Event>().WithMany().HasForeignKey(r => r.ToEventId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.FromEventId, r.ToEventId, r.Mode }).IsUnique().HasDatabaseName("UX_RouteLeg_From_To_Mode");
        });

        // 2.6 Trips, fares, drafts
        b.Entity<Trip>(e =>
        {
            e.ToTable("Trip");
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.StartUtc, t.EndUtc }).HasDatabaseName("IX_Trip_Time");
        });

        b.Entity<TripItem>(e =>
        {
            e.ToTable("TripItem");
            e.HasKey(t => t.Id);
            e.HasOne<Trip>().WithMany().HasForeignKey(t => t.TripId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Place>().WithMany().HasForeignKey(t => t.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Event>().WithMany().HasForeignKey(t => t.ProjectedEventId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(t => t.TripId).HasDatabaseName("IX_TripItem_TripId");
            e.HasIndex(t => t.PlaceId).HasDatabaseName("IX_TripItem_PlaceId");
        });

        b.Entity<FareWatch>(e =>
        {
            e.ToTable("FareWatch");
            e.HasKey(f => f.Id);
            e.HasOne<Place>().WithMany().HasForeignKey(f => f.OriginPlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Place>().WithMany().HasForeignKey(f => f.DestPlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(f => f.IsActive).HasDatabaseName("IX_FareWatch_IsActive");
        });

        b.Entity<FareSample>(e =>
        {
            e.ToTable("FareSample");
            e.HasKey(f => f.Id);
            e.HasOne<FareWatch>().WithMany().HasForeignKey(f => f.FareWatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => new { f.FareWatchId, f.SampledAtUtc }).HasDatabaseName("IX_FareSample_Watch_Time")
                .IsDescending(false, true);
        });

        b.Entity<NotificationLog>(e =>
        {
            e.ToTable("NotificationLog");
            e.HasKey(n => n.Id);
            e.HasOne<FareWatch>().WithMany().HasForeignKey(n => n.FareWatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(n => n.CreatedAtUtc).HasDatabaseName("IX_NotificationLog_CreatedAt").IsDescending();
        });

        b.Entity<ScenarioDraft>(e =>
        {
            e.ToTable("ScenarioDraft");
            e.HasKey(s => s.Id);
            e.HasOne<Trip>().WithMany().HasForeignKey(s => s.TripId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Place>().WithMany().HasForeignKey(s => s.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(s => new { s.StartUtc, s.EndUtc }).HasDatabaseName("IX_ScenarioDraft_Time");
        });

        b.Entity<Reminder>(e =>
        {
            e.ToTable("Reminder");
            e.HasKey(r => r.Id);
            e.HasOne<Event>().WithMany().HasForeignKey(r => r.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.FiredAtUtc, r.EventId }).HasDatabaseName("IX_Reminder_Fired_Event");
        });

        // Cloud sync (ADR-0003): the device's enrollment + the relay-side encrypted change feed.
        b.Entity<CloudSyncConfig>(e =>
        {
            e.ToTable("CloudSyncConfig");
            e.HasKey(c => c.Id);
        });

        b.Entity<RelaySpace>(e =>
        {
            e.ToTable("RelaySpace");
            e.HasKey(s => s.Id);
        });

        b.Entity<RelayBlob>(e =>
        {
            e.ToTable("RelayBlob");
            e.HasKey(r => r.Seq);
            e.Property(r => r.Seq).ValueGeneratedOnAdd();
            e.HasOne<RelaySpace>().WithMany().HasForeignKey(r => r.SpaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.SpaceId, r.Seq }).HasDatabaseName("IX_RelayBlob_Space_Seq");
        });

        // 2.7 Sharing
        b.Entity<Share>(e =>
        {
            e.ToTable("Share");
            e.HasKey(s => s.Id);
            e.HasOne<CalendarEntity>().WithMany().HasForeignKey(s => s.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.Token).IsUnique().HasDatabaseName("UX_Share_Token");
            e.HasIndex(s => s.CalendarId).HasDatabaseName("IX_Share_CalendarId");
        });

        // 2.8 Sync, outbox, device
        b.Entity<SyncState>(e =>
        {
            e.ToTable("SyncState");
            e.HasKey(s => s.Id);
            e.Property(s => s.RowVersion).IsConcurrencyToken();
            e.Property(s => s.State).HasConversion<string>();
            e.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CalendarEntity>().WithMany().HasForeignKey(s => s.CalendarId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.AccountId, s.CalendarId }).IsUnique().HasDatabaseName("UX_SyncState_Account_Calendar");
        });

        b.Entity<WriteOutbox>(e =>
        {
            e.ToTable("WriteOutbox");
            e.HasKey(w => w.Id);
            e.Property(w => w.RowVersion).IsConcurrencyToken();
            e.Property(w => w.Operation).HasConversion<string>();
            e.Property(w => w.Status).HasConversion<string>();
            e.HasIndex(w => new { w.Status, w.EnqueuedAtUtc }).HasDatabaseName("IX_WriteOutbox_Status_Enqueued");
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Device");
            e.HasKey(d => d.Id);
        });

        ApplyEnumToStringConversions(b);
        ApplySyncEntityFilters(b);
    }

    /// <summary>Store every enum as its member name (DATA-SCHEMA §1) â€” stable across reorderings, readable in the DB.</summary>
    private static void ApplyEnumToStringConversions(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().ToList())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type.IsEnum)
                    b.Entity(entity.ClrType).Property(property.Name).HasConversion<string>();
            }
        }
    }

    /// <summary>
    /// Apply the <c>!IsDeleted</c> tombstone filter to every <see cref="ISyncEntity"/> (DATA-SCHEMA §2.8, §6).
    /// The four sync columns are concrete properties on each entity, so only the filter needs wiring here.
    /// </summary>
    private static void ApplySyncEntityFilters(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            if (!typeof(ISyncEntity).IsAssignableFrom(entity.ClrType))
                continue;

            var parameter = Expression.Parameter(entity.ClrType, "e");
            var body = Expression.Not(Expression.Property(parameter, nameof(ISyncEntity.IsDeleted)));
            b.Entity(entity.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }
}
