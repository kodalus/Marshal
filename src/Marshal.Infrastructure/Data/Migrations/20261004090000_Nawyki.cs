using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Nawyki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nawyk i jego dni. Dwie tabele, nie jedna z listą w kolumnie: dni jest
            // tyle, ile dni, a scalanie idzie po polach jednego wiersza — rok historii
            // zbity w jedno pole byłby jednym polem przepisywanym przy każdym
            // dotknięciu i rozstrzyganym po zegarze jako całość.
            migrationBuilder.CreateTable(
                name: "Habits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    Target = table.Column<int>(type: "INTEGER", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Habits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HabitMarks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    HabitId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HabitMarks", x => x.Id);
                });

            // Lista pyta o żywe i nieodłożone przy każdym otwarciu ekranu.
            migrationBuilder.CreateIndex(
                name: "IX_Habits_Archived",
                table: "Habits",
                column: "Archived");

            // Siatka pyta o jeden nawyk i zakres dni — dokładnie ten indeks. Bez niego
            // rok historii sześciu nawyków przeglądałby się po kolei przy każdym
            // przerysowaniu listy.
            migrationBuilder.CreateIndex(
                name: "IX_HabitMarks_HabitId_Day",
                table: "HabitMarks",
                columns: ["HabitId", "Day"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HabitMarks");
            migrationBuilder.DropTable(name: "Habits");
        }
    }
}
