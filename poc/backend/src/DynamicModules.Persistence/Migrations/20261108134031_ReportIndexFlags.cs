using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReportIndexFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TextTruncated",
                table: "Dynamic_RecordIndex",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_NoScaled",
                table: "Dynamic_RecordIndex",
                column: "FieldId",
                filter: "ScaledValue IS NULL AND NumberValue IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_Truncated",
                table: "Dynamic_RecordIndex",
                columns: new[] { "FieldId", "TextTruncated" },
                filter: "TextTruncated = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dynamic_RecordIndex_NoScaled",
                table: "Dynamic_RecordIndex");

            migrationBuilder.DropIndex(
                name: "IX_Dynamic_RecordIndex_Truncated",
                table: "Dynamic_RecordIndex");

            migrationBuilder.DropColumn(
                name: "TextTruncated",
                table: "Dynamic_RecordIndex");
        }
    }
}
