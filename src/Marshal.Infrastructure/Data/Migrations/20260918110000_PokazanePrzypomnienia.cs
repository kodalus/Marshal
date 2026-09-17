using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PokazanePrzypomnienia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tabela zakładana od nowa, bo zmienia się klucz: z zadania na parę
            // zadanie-chwila. Wolno ją stracić — mówi wyłącznie o tym, co to urządzenie
            // już pokazało, jest lokalna i niesynchronizowana, a najgorsze, co wynika
            // z pustej, to jedno powtórzone przypomnienie.
            migrationBuilder.DropTable(name: "ReminderShown");

            migrationBuilder.CreateTable(
                name: "ReminderShown",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ReminderAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ShownAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderShown", x => new { x.TaskId, x.ReminderAt });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ReminderShown");

            migrationBuilder.CreateTable(
                name: "ReminderShown",
                columns: table => new
                {
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ReminderAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ShownAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderShown", x => x.TaskId);
                });
        }
    }
}
