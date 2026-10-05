using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marshal.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Areas",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    QuietDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultNudgeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Areas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ParentProjectId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    AreaId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ProjectId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ParentTaskId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    Deadline = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DoDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DeferUntil = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    WaitingForWho = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WaitingSince = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    WaitingNudgeDays = table.Column<int>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Areas_Deleted",
                table: "Areas",
                column: "Deleted");

            migrationBuilder.CreateIndex(
                name: "IX_Areas_SortOrder",
                table: "Areas",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_AreaId",
                table: "Projects",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ParentProjectId",
                table: "Projects",
                column: "ParentProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_State",
                table: "Projects",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_AreaId",
                table: "Tasks",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Deadline",
                table: "Tasks",
                column: "Deadline");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_DoDate",
                table: "Tasks",
                column: "DoDate");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ProjectId",
                table: "Tasks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_State",
                table: "Tasks",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Areas");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
