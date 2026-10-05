using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DostepDoKalendarza : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Czy do kalendarza wolno tylko czytać. Google podaje to przy każdym jako
            // poziom dostępu; kalendarze świąteczne, fazy księżyca i cudze udostępnione
            // bez prawa zmian są czytelnikami.
            //
            // Domyślnie „wolno pisać", bo tak zachowywała się aplikacja do tej pory
            // i tak jest dla większości kalendarzy. Prawdziwa wartość dojdzie przy
            // pierwszym pobraniu — zgadywanie w drugą stronę zablokowałoby na chwilę
            // zapis do kalendarzy, do których wolno.
            migrationBuilder.AddColumn<bool>(
                name: "ReadOnly",
                table: "CalendarSources",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReadOnly", table: "CalendarSources");
        }
    }
}
