using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Wyprzedzenia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>(
                name: "ReminderLeadsCsv",
                table: "Tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "ReminderLeadsCsv", table: "Tasks");
    }
}
