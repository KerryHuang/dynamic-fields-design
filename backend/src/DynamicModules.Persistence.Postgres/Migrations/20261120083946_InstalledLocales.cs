using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
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
                    Code = table.Column<string>(type: "text", maxLength: 35, nullable: false, collation: "C"),
                    NativeName = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    QuasarLang = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    FallbackCode = table.Column<string>(type: "text", maxLength: 35, nullable: true, collation: "C"),
                    Origin = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
