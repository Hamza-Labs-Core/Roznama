using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Calendar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Category",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchRules = table.Column<string>(type: "TEXT", nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Category", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Device",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Device", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateOverride",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    EventIds = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateOverride", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GeocodeCache",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Query = table.Column<string>(type: "TEXT", nullable: false),
                    QueryHash = table.Column<string>(type: "TEXT", nullable: false),
                    Lat = table.Column<double>(type: "REAL", nullable: false),
                    Lng = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    IsReverse = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResolvedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeocodeCache", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Place",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Lat = table.Column<double>(type: "REAL", nullable: false),
                    Lng = table.Column<double>(type: "REAL", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    NormalizedKey = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Place", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plugin",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    SdkVersion = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Capabilities = table.Column<string>(type: "TEXT", nullable: false),
                    Manifest = table.Column<string>(type: "TEXT", nullable: false),
                    TrustTier = table.Column<string>(type: "TEXT", nullable: false),
                    InstalledAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plugin", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecretRef",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AuthTag = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Aad = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    RotatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecretRef", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trip",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trip", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WriteOutbox",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    BaseETag = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    EnqueuedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WriteOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FareWatch",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    OriginPlaceId = table.Column<string>(type: "TEXT", nullable: true),
                    DestPlaceId = table.Column<string>(type: "TEXT", nullable: true),
                    RangeStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RangeEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Pax = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    TargetPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    LastLowPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FareWatch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FareWatch_Place_DestPlaceId",
                        column: x => x.DestPlaceId,
                        principalTable: "Place",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FareWatch_Place_OriginPlaceId",
                        column: x => x.OriginPlaceId,
                        principalTable: "Place",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PluginConfig",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: false),
                    Values = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginConfig", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginConfig_Plugin_PluginId",
                        column: x => x.PluginId,
                        principalTable: "Plugin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Account",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    AuthRef = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastSyncAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Account", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Account_Plugin_PluginId",
                        column: x => x.PluginId,
                        principalTable: "Plugin",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Account_SecretRef_AuthRef",
                        column: x => x.AuthRef,
                        principalTable: "SecretRef",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ScenarioDraft",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TripId = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: false),
                    PlaceId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScenarioDraft", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScenarioDraft_Place_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "Place",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ScenarioDraft_Trip_TripId",
                        column: x => x.TripId,
                        principalTable: "Trip",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "FareSample",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FareWatchId = table.Column<string>(type: "TEXT", nullable: false),
                    SampledAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FareSample", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FareSample_FareWatch_FareWatchId",
                        column: x => x.FareWatchId,
                        principalTable: "FareWatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Calendar",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calendar", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Calendar_Account_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Share",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<string>(type: "TEXT", nullable: true),
                    Filter = table.Column<string>(type: "TEXT", nullable: true),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Share", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Share_Calendar_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendar",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncState",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<string>(type: "TEXT", nullable: true),
                    SyncToken = table.Column<string>(type: "TEXT", nullable: true),
                    Ctag = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    NextRunAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    BackoffUntilUtc = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncState", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncState_Account_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Account",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SyncState_Calendar_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendar",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateGroup",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", nullable: false),
                    CanonicalEventId = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Event",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<string>(type: "TEXT", nullable: false),
                    Uid = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: false),
                    AllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Rrule = table.Column<string>(type: "TEXT", nullable: true),
                    MasterId = table.Column<string>(type: "TEXT", nullable: true),
                    RecurrenceId = table.Column<string>(type: "TEXT", nullable: true),
                    PlaceId = table.Column<string>(type: "TEXT", nullable: true),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    DedupSignature = table.Column<string>(type: "TEXT", nullable: true),
                    DuplicateGroupId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", nullable: true),
                    LastModifiedUtc = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Event_Calendar_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendar",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Event_DuplicateGroup_DuplicateGroupId",
                        column: x => x.DuplicateGroupId,
                        principalTable: "DuplicateGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Event_Event_MasterId",
                        column: x => x.MasterId,
                        principalTable: "Event",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Event_Place_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "Place",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EventCategory",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventCategory", x => new { x.EventId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_EventCategory_Category_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Category",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventCategory_Event_EventId",
                        column: x => x.EventId,
                        principalTable: "Event",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RouteLeg",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FromEventId = table.Column<string>(type: "TEXT", nullable: false),
                    ToEventId = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    DurationSec = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaveByUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Feasible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Geometry = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    ComputedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    IsStale = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteLeg", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteLeg_Event_FromEventId",
                        column: x => x.FromEventId,
                        principalTable: "Event",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RouteLeg_Event_ToEventId",
                        column: x => x.ToEventId,
                        principalTable: "Event",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TripItem",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TripId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PlaceId = table.Column<string>(type: "TEXT", nullable: true),
                    Confirmation = table.Column<string>(type: "TEXT", nullable: true),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: true),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectedEventId = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Lamport = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripItem", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripItem_Event_ProjectedEventId",
                        column: x => x.ProjectedEventId,
                        principalTable: "Event",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TripItem_Place_PlaceId",
                        column: x => x.PlaceId,
                        principalTable: "Place",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TripItem_Trip_TripId",
                        column: x => x.TripId,
                        principalTable: "Trip",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Account_AuthRef",
                table: "Account",
                column: "AuthRef");

            migrationBuilder.CreateIndex(
                name: "IX_Account_PluginId",
                table: "Account",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_Account_Priority",
                table: "Account",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_Calendar_AccountId",
                table: "Calendar",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "UX_Calendar_Account_RemoteId",
                table: "Calendar",
                columns: new[] { "AccountId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_IsVisible",
                table: "Category",
                column: "IsVisible");

            migrationBuilder.CreateIndex(
                name: "UX_Category_Name",
                table: "Category",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroup_CanonicalEventId",
                table: "DuplicateGroup",
                column: "CanonicalEventId");

            migrationBuilder.CreateIndex(
                name: "UX_DuplicateGroup_Signature",
                table: "DuplicateGroup",
                column: "Signature",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateOverride_Kind",
                table: "DuplicateOverride",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_Event_Calendar_Time",
                table: "Event",
                columns: new[] { "CalendarId", "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Event_DedupSignature",
                table: "Event",
                column: "DedupSignature",
                filter: "\"DedupSignature\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Event_DuplicateGroupId",
                table: "Event",
                column: "DuplicateGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Event_Master",
                table: "Event",
                column: "MasterId",
                filter: "\"MasterId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Event_PlaceId",
                table: "Event",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Event_Recurring",
                table: "Event",
                column: "CalendarId",
                filter: "\"Rrule\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Event_Uid",
                table: "Event",
                column: "Uid");

            migrationBuilder.CreateIndex(
                name: "UX_Event_Calendar_RemoteId",
                table: "Event",
                columns: new[] { "CalendarId", "RemoteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventCategory_CategoryId",
                table: "EventCategory",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_FareSample_Watch_Time",
                table: "FareSample",
                columns: new[] { "FareWatchId", "SampledAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_FareWatch_DestPlaceId",
                table: "FareWatch",
                column: "DestPlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_FareWatch_IsActive",
                table: "FareWatch",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_FareWatch_OriginPlaceId",
                table: "FareWatch",
                column: "OriginPlaceId");

            migrationBuilder.CreateIndex(
                name: "UX_GeocodeCache_QueryHash",
                table: "GeocodeCache",
                column: "QueryHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Place_LatLng",
                table: "Place",
                columns: new[] { "Lat", "Lng" });

            migrationBuilder.CreateIndex(
                name: "UX_Place_NormalizedKey",
                table: "Place",
                column: "NormalizedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PluginConfig_PluginId",
                table: "PluginConfig",
                column: "PluginId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RouteLeg_ToEventId",
                table: "RouteLeg",
                column: "ToEventId");

            migrationBuilder.CreateIndex(
                name: "UX_RouteLeg_From_To_Mode",
                table: "RouteLeg",
                columns: new[] { "FromEventId", "ToEventId", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioDraft_PlaceId",
                table: "ScenarioDraft",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioDraft_Time",
                table: "ScenarioDraft",
                columns: new[] { "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ScenarioDraft_TripId",
                table: "ScenarioDraft",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_SecretRef_Kind",
                table: "SecretRef",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_Share_CalendarId",
                table: "Share",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "UX_Share_Token",
                table: "Share",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncState_CalendarId",
                table: "SyncState",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "UX_SyncState_Account_Calendar",
                table: "SyncState",
                columns: new[] { "AccountId", "CalendarId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trip_Time",
                table: "Trip",
                columns: new[] { "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TripItem_PlaceId",
                table: "TripItem",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_TripItem_ProjectedEventId",
                table: "TripItem",
                column: "ProjectedEventId");

            migrationBuilder.CreateIndex(
                name: "IX_TripItem_TripId",
                table: "TripItem",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_WriteOutbox_Status_Enqueued",
                table: "WriteOutbox",
                columns: new[] { "Status", "EnqueuedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_DuplicateGroup_Event_CanonicalEventId",
                table: "DuplicateGroup",
                column: "CanonicalEventId",
                principalTable: "Event",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Account_Plugin_PluginId",
                table: "Account");

            migrationBuilder.DropForeignKey(
                name: "FK_Account_SecretRef_AuthRef",
                table: "Account");

            migrationBuilder.DropForeignKey(
                name: "FK_Calendar_Account_AccountId",
                table: "Calendar");

            migrationBuilder.DropForeignKey(
                name: "FK_DuplicateGroup_Event_CanonicalEventId",
                table: "DuplicateGroup");

            migrationBuilder.DropTable(
                name: "Device");

            migrationBuilder.DropTable(
                name: "DuplicateOverride");

            migrationBuilder.DropTable(
                name: "EventCategory");

            migrationBuilder.DropTable(
                name: "FareSample");

            migrationBuilder.DropTable(
                name: "GeocodeCache");

            migrationBuilder.DropTable(
                name: "PluginConfig");

            migrationBuilder.DropTable(
                name: "RouteLeg");

            migrationBuilder.DropTable(
                name: "ScenarioDraft");

            migrationBuilder.DropTable(
                name: "Share");

            migrationBuilder.DropTable(
                name: "SyncState");

            migrationBuilder.DropTable(
                name: "TripItem");

            migrationBuilder.DropTable(
                name: "WriteOutbox");

            migrationBuilder.DropTable(
                name: "Category");

            migrationBuilder.DropTable(
                name: "FareWatch");

            migrationBuilder.DropTable(
                name: "Trip");

            migrationBuilder.DropTable(
                name: "Plugin");

            migrationBuilder.DropTable(
                name: "SecretRef");

            migrationBuilder.DropTable(
                name: "Account");

            migrationBuilder.DropTable(
                name: "Event");

            migrationBuilder.DropTable(
                name: "Calendar");

            migrationBuilder.DropTable(
                name: "DuplicateGroup");

            migrationBuilder.DropTable(
                name: "Place");
        }
    }
}
