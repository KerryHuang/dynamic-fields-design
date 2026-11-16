using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dashboards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_Dashboard",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LayoutJson = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Dashboard", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_DashboardAssignment",
                columns: table => new
                {
                    DashboardId = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_DashboardAssignment", x => new { x.DashboardId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_Dynamic_DashboardAssignment_Dynamic_Dashboard_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dynamic_Dashboard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_DashboardPersonalVersion",
                columns: table => new
                {
                    DashboardId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LayoutJson = table.Column<string>(type: "TEXT", nullable: false),
                    BaseVersion = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_DashboardPersonalVersion", x => new { x.DashboardId, x.UserId });
                    table.ForeignKey(
                        name: "FK_Dynamic_DashboardPersonalVersion_Dynamic_Dashboard_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dynamic_Dashboard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Dashboard_OwnerUserId",
                table: "Dynamic_Dashboard",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_DashboardAssignment_GroupId",
                table: "Dynamic_DashboardAssignment",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_DashboardPersonalVersion_UserId",
                table: "Dynamic_DashboardPersonalVersion",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_DashboardAssignment");

            migrationBuilder.DropTable(
                name: "Dynamic_DashboardPersonalVersion");

            migrationBuilder.DropTable(
                name: "Dynamic_Dashboard");
        }
    }
}
