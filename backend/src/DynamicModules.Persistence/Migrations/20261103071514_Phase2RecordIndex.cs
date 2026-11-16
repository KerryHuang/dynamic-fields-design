using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2RecordIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_RecordIndex",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<long>(type: "INTEGER", nullable: false),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    TextValue = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    NumberValue = table.Column<double>(type: "REAL", nullable: true),
                    DateValue = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_RecordIndex", x => new { x.RecordId, x.FieldId, x.Seq });
                    table.ForeignKey(
                        name: "FK_Dynamic_RecordIndex_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dynamic_RecordIndex_Dynamic_Record_RecordId",
                        column: x => x.RecordId,
                        principalTable: "Dynamic_Record",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_FieldId_DateValue",
                table: "Dynamic_RecordIndex",
                columns: new[] { "FieldId", "DateValue" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_FieldId_NumberValue",
                table: "Dynamic_RecordIndex",
                columns: new[] { "FieldId", "NumberValue" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_FieldId_RefKey",
                table: "Dynamic_RecordIndex",
                columns: new[] { "FieldId", "RefKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_FieldId_TextValue",
                table: "Dynamic_RecordIndex",
                columns: new[] { "FieldId", "TextValue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_RecordIndex");
        }
    }
}
