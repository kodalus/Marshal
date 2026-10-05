using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Powtorzenia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecurrenceJson",
                table: "Tasks",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CarriedSince",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RollCount",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_State_DoDate",
                table: "Tasks",
                columns: new[] { "State", "DoDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_State_DoDate",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "RecurrenceJson",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CarriedSince",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "RollCount",
                table: "Tasks");
        }
    }
}
