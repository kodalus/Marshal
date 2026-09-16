using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Kalendarz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "DoTime",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            // Decyzja, więc synchronizowana.
            migrationBuilder.CreateTable(
                name: "CalendarSources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSources", x => x.Id);
                });

            // Kopia cudzych danych — lokalna, poza dziennikiem zmian.
            migrationBuilder.CreateTable(
                name: "CalendarEvents",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    StartsAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EndsAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Cancelled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEvents", x => new { x.SourceId, x.ExternalId });
                });

            migrationBuilder.CreateTable(
                name: "CalendarCursors",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SyncToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarCursors", x => x.SourceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_StartsAt",
                table: "CalendarEvents",
                column: "StartsAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CalendarCursors");
            migrationBuilder.DropTable(name: "CalendarEvents");
            migrationBuilder.DropTable(name: "CalendarSources");

            migrationBuilder.DropColumn(name: "DoTime", table: "Tasks");
        }
    }
}
