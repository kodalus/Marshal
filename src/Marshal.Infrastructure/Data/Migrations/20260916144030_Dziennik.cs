using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Dziennik : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Changes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    Hlc = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FieldStamps",
                columns: table => new
                {
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Hlc = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldStamps", x => new { x.EntityType, x.EntityId, x.Field });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Changes_Sent_Hlc",
                table: "Changes",
                columns: new[] { "Sent", "Hlc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Changes");

            migrationBuilder.DropTable(
                name: "FieldStamps");
        }
    }
}
