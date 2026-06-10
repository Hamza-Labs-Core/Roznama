using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Calendar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFareWatchPayloadAndNotificationLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestIata",
                table: "FareWatch",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DropThreshold",
                table: "FareWatch",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Lat",
                table: "FareWatch",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Lng",
                table: "FareWatch",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginIata",
                table: "FareWatch",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RadiusKm",
                table: "FareWatch",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Rooms",
                table: "FareWatch",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationLog",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FareWatchId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationLog_FareWatch_FareWatchId",
                        column: x => x.FareWatchId,
                        principalTable: "FareWatch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLog_CreatedAt",
                table: "NotificationLog",
                column: "CreatedAtUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationLog_FareWatchId",
                table: "NotificationLog",
                column: "FareWatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationLog");

            migrationBuilder.DropColumn(
                name: "DestIata",
                table: "FareWatch");

            migrationBuilder.DropColumn(
                name: "DropThreshold",
                table: "FareWatch");

            migrationBuilder.DropColumn(
                name: "Lat",
                table: "FareWatch");

            migrationBuilder.DropColumn(
                name: "Lng",
                table: "FareWatch");

            migrationBuilder.DropColumn(
                name: "OriginIata",
                table: "FareWatch");

            migrationBuilder.DropColumn(
                name: "RadiusKm",
                table: "FareWatch");

            migrationBuilder.DropColumn(
                name: "Rooms",
                table: "FareWatch");
        }
    }
}
