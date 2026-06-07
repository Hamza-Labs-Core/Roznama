namespace Calendar.Domain;

// Domain-local enums (stored as TEXT per DATA-SCHEMA.md §1). The shared lifecycle enums
// (EventStatus, TravelMode, TripItemKind, TripItemStatus) come from Calendar.Plugin.Abstractions —
// the SDK contract is their single source of truth, and the persisted entities reuse them directly.

/// <summary>Install state of a plugin (DATA-SCHEMA §2.1 <c>Plugin.Status</c>).</summary>
public enum PluginStatus { Installed, Disabled, Error }

/// <summary>Trust tier governing sandboxing decisions (PLUGINS §8; DATA-SCHEMA §2.1).</summary>
public enum TrustTier { InBox, Signed, Community, LocalDev }

/// <summary>Connection state of an account (DATA-SCHEMA §2.1 <c>Account.Status</c>).</summary>
public enum AccountStatus { Connected, NeedsAuth, Disabled, Error }

/// <summary>Hint for dedup/category rules (DATA-SCHEMA §2.3 <c>Calendar.Kind</c>).</summary>
public enum CalendarKind { Primary, Holidays, Birthdays, Subscribed, Generic }

/// <summary>What credential a vault entry holds (DATA-SCHEMA §2.2 <c>SecretRef.Kind</c>).</summary>
public enum SecretKind { OAuthRefreshToken, OAuthAccessToken, ApiKey, BasicPassword, AppPassword, FeedUrl }

/// <summary>Who attached a category to an event (DATA-SCHEMA §2.3 <c>EventCategory.AssignedBy</c>).</summary>
public enum AssignmentSource { Rule, User }

/// <summary>Reversible dedup override kind (DATA-SCHEMA §2.4 <c>DuplicateOverride.Kind</c>).</summary>
public enum OverrideKind { ForceMerge, NeverMerge, SetCanonical }

/// <summary>Fare-watch subject (DATA-SCHEMA §2.6 <c>FareWatch.Kind</c>).</summary>
public enum FareKind { Flight, Stay }

/// <summary>Why a <see cref="Entities.NotificationLog"/> row was written (ARCHITECTURE §14, travel-fares-plugin.md §10).</summary>
public enum NotificationKind { FareDrop, FareTarget }

/// <summary>Delivery state of a notification beyond the persisted in-app log (email/webhook are stubbed).</summary>
public enum NotificationChannel { InApp, Email, Webhook }

/// <summary>What a scenario draft becomes when promoted (DATA-SCHEMA §2.6 <c>ScenarioDraft.Kind</c>).</summary>
public enum DraftKind { Event, Flight, Stay, Activity }

/// <summary>Share visibility scope (DATA-SCHEMA §2.7 <c>Share.Scope</c>).</summary>
public enum ShareScope { FullDetails, FreeBusy }

/// <summary>Per-(account,calendar) sync run state (DATA-SCHEMA §2.8 <c>SyncState.State</c>).</summary>
public enum SyncRunState { Idle, Running, Backoff, Error }

/// <summary>Queued offline write operation (DATA-SCHEMA §2.8 <c>WriteOutbox.Operation</c>).</summary>
public enum OutboxOp { Create, Update, Delete }

/// <summary>Replay status of a queued write (DATA-SCHEMA §2.8 <c>WriteOutbox.Status</c>).</summary>
public enum OutboxStatus { Pending, InFlight, Failed, Done, Conflict }
