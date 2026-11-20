using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstalledLocales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_Locale",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 35, nullable: false),
                    NativeName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    QuasarLang = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    FallbackCode = table.Column<string>(type: "TEXT", maxLength: 35, nullable: true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Locale", x => x.Code);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_Locale");
        }
    }
}
