using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeriaZadan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Overridden",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SeriesId",
                table: "Tasks",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskSeries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Starts = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RuleJson = table.Column<string>(type: "TEXT", nullable: false),
                    TemplateJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSeries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SeriesId",
                table: "Tasks",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskSeries");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_SeriesId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Overridden",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Tasks");
        }
    }
}
