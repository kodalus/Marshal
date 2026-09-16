using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Porcje : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Kursor przestał być przesunięciem w bajtach, a stał się nazwą ostatniej
            // przeczytanej porcji. Przeliczyć się tego nie da i nie trzeba: kursor jest
            // przyspieszeniem, nie danymi. Pusty znaczy „przeczytaj wszystko od nowa",
            // co daje ten sam stan, tylko raz wolniej.
            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    RemoteDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastSegment = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.RemoteDeviceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncCursors");

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    RemoteDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Offset = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.RemoteDeviceId);
                });
        }
    }
}
