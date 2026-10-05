using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Przypomnienia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReminderAt",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            // Lokalna, poza synchronizacją: „czy już pokazałam" jest faktem o tym
            // urządzeniu, a nie decyzją do rozesłania.
            migrationBuilder.CreateTable(
                name: "ReminderShown",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ReminderAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ShownAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderShown", x => x.TaskId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ReminderAt",
                table: "Tasks",
                column: "ReminderAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReminderShown");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ReminderAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ReminderAt",
                table: "Tasks");
        }
    }
}
