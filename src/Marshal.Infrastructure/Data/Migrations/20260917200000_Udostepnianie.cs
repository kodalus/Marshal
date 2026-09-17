using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Udostepnianie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SharedCalendarId",
                table: "Tasks",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedEventId",
                table: "Tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // Po udostępnionych zadaniach chodzi się przy każdym zapisie, żeby wiedzieć,
            // czy jest co odświeżyć na zewnątrz.
            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SharedCalendarId", table: "Tasks", column: "SharedCalendarId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Tasks_SharedCalendarId", table: "Tasks");
            migrationBuilder.DropColumn(name: "SharedCalendarId", table: "Tasks");
            migrationBuilder.DropColumn(name: "SharedEventId", table: "Tasks");
        }
    }
}
