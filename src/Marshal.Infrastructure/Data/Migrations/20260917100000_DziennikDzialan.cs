using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DziennikDzialan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityEntries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_At", table: "ActivityEntries", column: "At");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ActivityEntries");
        }
    }
}
