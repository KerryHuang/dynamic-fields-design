using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Dynamic_McpConfirmation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CodeHash = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    ToolName = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    ArgumentsHash = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    TargetVersion = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    PreviewSummary = table.Column<string>(type: "text", maxLength: 2000, nullable: false, collation: "C"),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_McpConfirmation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_McpToken",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    TokenHash = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    Last4 = table.Column<string>(type: "text", maxLength: 4, nullable: false, collation: "C"),
                    Scopes = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedIp = table.Column<string>(type: "text", maxLength: 45, nullable: true, collation: "C"),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    RevokeReason = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C")
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
