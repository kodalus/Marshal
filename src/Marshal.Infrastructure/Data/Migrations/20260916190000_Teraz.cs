using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Teraz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EstimatedMinutes",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Energy",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FocusDate",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusMissCount",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_FocusDate",
                table: "Tasks",
                column: "FocusDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_FocusDate",
                table: "Tasks");

            migrationBuilder.DropColumn(name: "EstimatedMinutes", table: "Tasks");
            migrationBuilder.DropColumn(name: "Energy", table: "Tasks");
            migrationBuilder.DropColumn(name: "FocusDate", table: "Tasks");
            migrationBuilder.DropColumn(name: "FocusMissCount", table: "Tasks");
        }
    }
}
