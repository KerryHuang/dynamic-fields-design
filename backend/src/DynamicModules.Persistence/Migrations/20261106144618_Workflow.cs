using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Workflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_Workflow",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    StatusFieldId = table.Column<long>(type: "INTEGER", nullable: false),
                    InitialOptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    States = table.Column<string>(type: "TEXT", nullable: false),
                    Transitions = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Workflow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Workflow_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Workflow_ModuleId",
                table: "Dynamic_Workflow",
                column: "ModuleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_Workflow");
        }
    }
}
