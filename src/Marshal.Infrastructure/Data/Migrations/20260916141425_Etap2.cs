using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Etap2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Tasks",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Projects",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskTags",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    TagId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskTags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Priority",
                table: "Tasks",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TaskTags_TagId",
                table: "TaskTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskTags_TaskId",
                table: "TaskTags",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "TaskTags");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_Priority",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "Projects");
        }
    }
}
