using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Calendar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudSyncConfig",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RelayUrl = table.Column<string>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<string>(type: "TEXT", nullable: false),
                    KeySecretRef = table.Column<string>(type: "TEXT", nullable: false),
                    TokenSecretRef = table.Column<string>(type: "TEXT", nullable: false),
                    LastPulledSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPushedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudSyncConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RelaySpace",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelaySpace", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RelayBlob",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SpaceId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelayBlob", x => x.Seq);
                    table.ForeignKey(
                        name: "FK_RelayBlob_RelaySpace_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "RelaySpace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RelayBlob_Space_Seq",
                table: "RelayBlob",
                columns: new[] { "SpaceId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudSyncConfig");

            migrationBuilder.DropTable(
                name: "RelayBlob");

            migrationBuilder.DropTable(
                name: "RelaySpace");
        }
    }
}
