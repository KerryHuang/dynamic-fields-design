using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_App",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PackageVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Requires = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_App", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppPackage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppPackage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppModule",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppModule", x => new { x.AppId, x.Code });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppModule_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppModule_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_App_Key",
                table: "Dynamic_App",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppModule_ModuleId",
                table: "Dynamic_AppModule",
                column: "ModuleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppPackage_Key",
                table: "Dynamic_AppPackage",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppModule");

            migrationBuilder.DropTable(
                name: "Dynamic_AppPackage");

            migrationBuilder.DropTable(
                name: "Dynamic_App");
        }
    }
}
