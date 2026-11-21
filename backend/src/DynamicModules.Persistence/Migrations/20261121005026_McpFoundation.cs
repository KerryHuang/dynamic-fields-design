using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromAi",
                table: "Dynamic_Field",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Dynamic_McpConfirmation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TokenId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ArgumentsHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PreviewSummary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_McpConfirmation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_McpToken",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Last4 = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RevokeReason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_McpToken", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_McpConfirmation_CodeHash",
                table: "Dynamic_McpConfirmation",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_McpConfirmation_ExpiresUtc",
                table: "Dynamic_McpConfirmation",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_McpConfirmation_TokenId_CreatedUtc",
                table: "Dynamic_McpConfirmation",
                columns: new[] { "TokenId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_McpToken_TokenHash",
                table: "Dynamic_McpToken",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_McpToken_UserId",
                table: "Dynamic_McpToken",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_McpConfirmation");

            migrationBuilder.DropTable(
                name: "Dynamic_McpToken");

            migrationBuilder.DropColumn(
                name: "ExcludeFromAi",
                table: "Dynamic_Field");
        }
    }
}
