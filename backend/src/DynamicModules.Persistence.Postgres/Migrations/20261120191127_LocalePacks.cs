using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", maxLength: 35, nullable: false, collation: "C"),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    UiJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ServerJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    BaseHashesJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    FileHash = table.Column<string>(type: "text", maxLength: 64, nullable: true, collation: "C"),
                    FileName = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    PackVersion = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    BuiltFor = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    BaseLocale = table.Column<string>(type: "text", maxLength: 35, nullable: true, collation: "C"),
                    Note = table.Column<string>(type: "text", maxLength: 500, nullable: true, collation: "C"),
                    ReportJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ImportedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ImportedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_LocalePack", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_LocalePackPreview",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", maxLength: 32, nullable: false, collation: "C"),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Code = table.Column<string>(type: "text", maxLength: 35, nullable: false, collation: "C"),
                    FileHash = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    FileName = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    PlanJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ReportJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    LocaleFingerprint = table.Column<string>(type: "text", maxLength: 64, nullable: true, collation: "C"),
                    CurrentPackId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
