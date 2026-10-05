using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notatki : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    AreaId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    FromTaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    NoteId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_Notes_IsPinned", table: "Notes", column: "IsPinned");
            migrationBuilder.CreateIndex(name: "IX_Notes_AreaId", table: "Notes", column: "AreaId");
            migrationBuilder.CreateIndex(name: "IX_Attachments_TaskId", table: "Attachments", column: "TaskId");
            migrationBuilder.CreateIndex(name: "IX_Attachments_NoteId", table: "Attachments", column: "NoteId");
            migrationBuilder.CreateIndex(name: "IX_Attachments_Sha256", table: "Attachments", column: "Sha256");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Attachments");
            migrationBuilder.DropTable(name: "Notes");
        }
    }
}
