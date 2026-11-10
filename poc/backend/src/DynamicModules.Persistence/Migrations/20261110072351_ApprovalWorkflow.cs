using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalDelegate",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DelegateUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalDelegate", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalFlow",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToOptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CurrentVersionId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalFlow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalFlow_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalInstance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    FlowId = table.Column<long>(type: "INTEGER", nullable: true),
                    FlowVersionId = table.Column<long>(type: "INTEGER", nullable: true),
                    FlowSetSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    RouteKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FromOptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToOptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    InitiatorUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RestartedFromInstanceId = table.Column<long>(type: "INTEGER", nullable: true),
                    CurrentStageRunId = table.Column<long>(type: "INTEGER", nullable: true),
                    TitleSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Picks = table.Column<string>(type: "TEXT", nullable: false),
                    SubmittedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalInstance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalFlowVersion",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FlowId = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Definition = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalFlowVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalFlowVersion_Dynamic_ApprovalFlow_FlowId",
                        column: x => x.FlowId,
                        principalTable: "Dynamic_ApprovalFlow",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalAction",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<long>(type: "INTEGER", nullable: false),
                    StageRunId = table.Column<long>(type: "INTEGER", nullable: true),
                    TaskId = table.Column<long>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    OnBehalfOfUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TargetUserIds = table.Column<string>(type: "TEXT", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DataJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalAction", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalAction_Dynamic_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Dynamic_ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalInstanceFlow",
                columns: table => new
                {
                    InstanceId = table.Column<long>(type: "INTEGER", nullable: false),
                    FlowId = table.Column<long>(type: "INTEGER", nullable: false),
                    FlowVersionId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalInstanceFlow", x => new { x.InstanceId, x.FlowId });
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalInstanceFlow_Dynamic_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Dynamic_ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalStageRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    StageCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    NameSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SkipReason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    AddedByUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    AddSignUserIds = table.Column<string>(type: "TEXT", nullable: true),
                    ActivatedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    BranchKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalStageRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalStageRun_Dynamic_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Dynamic_ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalTask",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StageRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    AssigneeUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    OriginalUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SeatKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRemindedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalTask_Dynamic_ApprovalInstance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Dynamic_ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalAction_InstanceId",
                table: "Dynamic_ApprovalAction",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalFlow_Key",
                table: "Dynamic_ApprovalFlow",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalFlow_ModuleId_ToOptionId_Priority",
                table: "Dynamic_ApprovalFlow",
                columns: new[] { "ModuleId", "ToOptionId", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalFlowVersion_FlowId_Version",
                table: "Dynamic_ApprovalFlowVersion",
                columns: new[] { "FlowId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalInstance_InitiatorUserId",
                table: "Dynamic_ApprovalInstance",
                column: "InitiatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalInstance_ModuleId_Status",
                table: "Dynamic_ApprovalInstance",
                columns: new[] { "ModuleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalInstance_RecordId",
                table: "Dynamic_ApprovalInstance",
                column: "RecordId",
                unique: true,
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalInstanceFlow_FlowVersionId",
                table: "Dynamic_ApprovalInstanceFlow",
                column: "FlowVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalStageRun_InstanceId_Seq",
                table: "Dynamic_ApprovalStageRun",
                columns: new[] { "InstanceId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalTask_AssigneeUserId_Status",
                table: "Dynamic_ApprovalTask",
                columns: new[] { "AssigneeUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalTask_InstanceId",
                table: "Dynamic_ApprovalTask",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalTask_OriginalUserId_Reason_Status",
                table: "Dynamic_ApprovalTask",
                columns: new[] { "OriginalUserId", "Reason", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalTask_RecordId_Status",
                table: "Dynamic_ApprovalTask",
                columns: new[] { "RecordId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ApprovalTask_StageRunId",
                table: "Dynamic_ApprovalTask",
                column: "StageRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalAction");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalDelegate");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalFlowVersion");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalInstanceFlow");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalStageRun");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalTask");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalFlow");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalInstance");
        }
    }
}
