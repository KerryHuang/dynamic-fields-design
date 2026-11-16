using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_TaskView",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskView", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskViewShare",
                columns: table => new
                {
                    ViewId = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskViewShare", x => new { x.ViewId, x.TargetKind, x.TargetId });
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskViewShare_Dynamic_TaskView_ViewId",
                        column: x => x.ViewId,
                        principalTable: "Dynamic_TaskView",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskView_OwnerUserId",
                table: "Dynamic_TaskView",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskViewShare_TargetKind_TargetId",
                table: "Dynamic_TaskViewShare",
                columns: new[] { "TargetKind", "TargetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_TaskViewShare");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskView");
        }
    }
}
