using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalePacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_LocalePack",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 35, nullable: false),
                    IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    UiJson = table.Column<string>(type: "TEXT", nullable: false),
                    ServerJson = table.Column<string>(type: "TEXT", nullable: false),
                    BaseHashesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FileHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PackVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    BuiltFor = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    BaseLocale = table.Column<string>(type: "TEXT", maxLength: 35, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ImportedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_LocalePack", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_LocalePackPreview",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 35, nullable: false),
                    FileHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: false),
                    LocaleFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CurrentPackId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_LocalePackPreview", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_LocalePack_Code",
                table: "Dynamic_LocalePack",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_LocalePackPreview_ExpiresUtc",
                table: "Dynamic_LocalePackPreview",
                column: "ExpiresUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_LocalePack");

            migrationBuilder.DropTable(
                name: "Dynamic_LocalePackPreview");
        }
    }
}
