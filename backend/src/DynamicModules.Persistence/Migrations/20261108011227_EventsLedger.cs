using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EventsLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ScaledValue",
                table: "Dynamic_RecordIndex",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Dynamic_Check",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Definition = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Check", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Check_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    SourceModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Definition = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Event_Dynamic_Module_SourceModuleId",
                        column: x => x.SourceModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_EventFact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EffectCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemKey = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_EventFact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Ledger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Definition = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Ledger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Ledger_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_LedgerReversal",
                columns: table => new
                {
                    OriginalRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReversalRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_LedgerReversal", x => x.OriginalRecordId);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Operation",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResultKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Operation", x => new { x.UserId, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Check_Key",
                table: "Dynamic_Check",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Check_ModuleId",
                table: "Dynamic_Check",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Event_Key",
                table: "Dynamic_Event",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Event_SourceModuleId",
                table: "Dynamic_Event",
                column: "SourceModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_EventFact_EventKey_EffectCode_SourceRecordId_ItemKey",
                table: "Dynamic_EventFact",
                columns: new[] { "EventKey", "EffectCode", "SourceRecordId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_EventFact_SourceRecordId",
                table: "Dynamic_EventFact",
                column: "SourceRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_EventFact_TargetRecordId",
                table: "Dynamic_EventFact",
                column: "TargetRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Ledger_ModuleId",
                table: "Dynamic_Ledger",
                column: "ModuleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_LedgerReversal_ReversalRecordId",
                table: "Dynamic_LedgerReversal",
                column: "ReversalRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Operation_CreatedTime",
                table: "Dynamic_Operation",
                column: "CreatedTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_Check");

            migrationBuilder.DropTable(
                name: "Dynamic_Event");

            migrationBuilder.DropTable(
                name: "Dynamic_EventFact");

            migrationBuilder.DropTable(
                name: "Dynamic_Ledger");

            migrationBuilder.DropTable(
                name: "Dynamic_LedgerReversal");

            migrationBuilder.DropTable(
                name: "Dynamic_Operation");

            migrationBuilder.DropColumn(
                name: "ScaledValue",
                table: "Dynamic_RecordIndex");
        }
    }
}
