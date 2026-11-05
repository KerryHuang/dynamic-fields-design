using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7History : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_RecordHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<long>(type: "INTEGER", nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    ChangedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ChangedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_RecordHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_RecordHistory_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dynamic_RecordHistory_Dynamic_Record_RecordId",
                        column: x => x.RecordId,
                        principalTable: "Dynamic_Record",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordHistory_FieldId",
                table: "Dynamic_RecordHistory",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordHistory_RecordId_ChangedTime",
                table: "Dynamic_RecordHistory",
                columns: new[] { "RecordId", "ChangedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_RecordHistory");
        }
    }
}
