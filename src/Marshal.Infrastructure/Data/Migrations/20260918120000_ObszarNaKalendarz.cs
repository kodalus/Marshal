using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ObszarNaKalendarz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Obszar wskazuje kalendarz, nie odwrotnie. Wiersz podłączenia jest odbiciem
            // tego, co jest u Google — bywa zakładany dwukrotnie i odrzucany przy
            // składaniu duplikatów — a stan położony na czymś, co ginie, ginie z tym.
            migrationBuilder.AddColumn<string>(
                name: "CalendarId",
                table: "Areas",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Areas_CalendarId",
                table: "Areas",
                column: "CalendarId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Areas_CalendarId", table: "Areas");
            migrationBuilder.DropColumn(name: "CalendarId", table: "Areas");
        }
    }
}
