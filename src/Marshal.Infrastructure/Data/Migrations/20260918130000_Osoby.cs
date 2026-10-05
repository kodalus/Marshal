using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Osoby : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Osoby, którym pokazuje się pojedyncze wydarzenia. Encja, a nie ustawienie
            // urządzenia: lista ma być ta sama na telefonie i na komputerze, a ustawienia
            // są lokalne z rozmysłu.
            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Deleted",
                table: "Contacts",
                column: "Deleted");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Contacts");
        }
    }
}
