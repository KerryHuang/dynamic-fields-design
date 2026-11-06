using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Reports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_Report",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Report", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ReportShare",
                columns: table => new
                {
                    ReportId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ReportShare", x => new { x.ReportId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_Dynamic_ReportShare_Dynamic_Report_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Dynamic_Report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Report_OwnerUserId",
                table: "Dynamic_Report",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ReportShare_GroupId",
                table: "Dynamic_ReportShare",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_ReportShare");

            migrationBuilder.DropTable(
                name: "Dynamic_Report");
        }
    }
}
