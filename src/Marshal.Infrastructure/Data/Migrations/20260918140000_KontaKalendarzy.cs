using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class KontaKalendarzy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adres pocztowy konta, z którego pochodzi kalendarz. Puste znaczy konto
            // główne — to, którym aplikacja synchronizuje przez Dysk — więc wszystko,
            // co podłączono wcześniej, zostaje przy nim bez żadnej przeróbki danych.
            //
            // Adres, nie identyfikator nadany lokalnie: wiersz podłączenia jedzie
            // synchronizacją, a żeton zostaje na urządzeniu. Drugie urządzenie musi
            // rozpoznać to samo konto po czymś, co obie strony widzą tak samo.
            migrationBuilder.AddColumn<string>(
                name: "Account",
                table: "CalendarSources",
                type: "TEXT",
                maxLength: 320,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Account", table: "CalendarSources");
        }
    }
}
