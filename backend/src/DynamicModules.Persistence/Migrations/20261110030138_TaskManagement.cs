using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_Notification",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    TitleSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Link = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SourceId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReadTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Task",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AssigneeUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: true),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: true),
                    RecordTitleSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RuleId = table.Column<long>(type: "INTEGER", nullable: true),
                    RuleOccurrence = table.Column<int>(type: "INTEGER", nullable: true),
                    RuleTaskCode = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    DueOverridden = table.Column<bool>(type: "INTEGER", nullable: false),
                    AssigneeOverridden = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancelledByRule = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDueSoonNotified = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    LastOverdueNotified = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Task", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Task_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskRule",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameI18n = table.Column<string>(type: "TEXT", nullable: false),
                    ModuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    AppId = table.Column<long>(type: "INTEGER", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    Repeat = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Once"),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    WhenJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskRule_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskComment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AuthorUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Body = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    SystemEvent = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SystemData = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Withdrawn = table.Column<bool>(type: "INTEGER", nullable: false),
                    WithdrawnBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    WithdrawnTime = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskComment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskComment_Dynamic_Task_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Dynamic_Task",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskParticipant",
                columns: table => new
                {
                    TaskId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskParticipant", x => new { x.TaskId, x.UserId, x.Reason });
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskParticipant_Dynamic_Task_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Dynamic_Task",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskRuleRun",
                columns: table => new
                {
                    RuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Occurrence = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TriggeredBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskRuleRun", x => new { x.RuleId, x.RecordId, x.Occurrence });
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskRuleRun_Dynamic_TaskRule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "Dynamic_TaskRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskRuleTask",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    TitleI18n = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptionI18n = table.Column<string>(type: "TEXT", nullable: true),
                    AssigneeFieldId = table.Column<long>(type: "INTEGER", nullable: false),
                    DueFieldId = table.Column<long>(type: "INTEGER", nullable: true),
                    DueOffsetDays = table.Column<int>(type: "INTEGER", nullable: false),
                    WhenJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskRuleTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskRuleTask_Dynamic_TaskRule_RuleId",
                        column: x => x.RuleId,
                        principalTable: "Dynamic_TaskRule",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskCommentAttachment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CommentId = table.Column<long>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskCommentAttachment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskCommentAttachment_Dynamic_TaskComment_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Dynamic_TaskComment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Notification_SourceType_SourceId",
                table: "Dynamic_Notification",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Notification_UserId_CreatedTime",
                table: "Dynamic_Notification",
                columns: new[] { "UserId", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Notification_UserId_ReadTime",
                table: "Dynamic_Notification",
                columns: new[] { "UserId", "ReadTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Task_AssigneeUserId_Status",
                table: "Dynamic_Task",
                columns: new[] { "AssigneeUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Task_CreatedBy",
                table: "Dynamic_Task",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Task_ModuleId",
                table: "Dynamic_Task",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Task_RecordId",
                table: "Dynamic_Task",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Task_RuleId_RecordId_RuleOccurrence_RuleTaskCode",
                table: "Dynamic_Task",
                columns: new[] { "RuleId", "RecordId", "RuleOccurrence", "RuleTaskCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskComment_TaskId",
                table: "Dynamic_TaskComment",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskCommentAttachment_CommentId",
                table: "Dynamic_TaskCommentAttachment",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskCommentAttachment_StorageKey",
                table: "Dynamic_TaskCommentAttachment",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskParticipant_UserId",
                table: "Dynamic_TaskParticipant",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskRule_AppId",
                table: "Dynamic_TaskRule",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskRule_Key",
                table: "Dynamic_TaskRule",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskRule_ModuleId",
                table: "Dynamic_TaskRule",
                column: "ModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskRuleTask_RuleId_Code",
                table: "Dynamic_TaskRuleTask",
                columns: new[] { "RuleId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_Notification");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskCommentAttachment");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskParticipant");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskRuleRun");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskRuleTask");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskComment");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskRule");

            migrationBuilder.DropTable(
                name: "Dynamic_Task");
        }
    }
}
