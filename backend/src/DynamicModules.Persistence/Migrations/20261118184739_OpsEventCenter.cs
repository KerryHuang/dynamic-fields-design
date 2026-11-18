using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DynamicModules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpsEventCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_AlertChannel",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Security = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: true),
                    FromAddress = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    FromName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ToAddresses = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    MailLocale = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    LastClaimUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AlertChannel", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AlertOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IncidentId = table.Column<long>(type: "INTEGER", nullable: true),
                    Transition = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ClaimedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ClaimedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DigestOutboxId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AlertOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ActorKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TokenId = table.Column<long>(type: "INTEGER", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RiskLevel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TargetLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ArgumentsHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResultCount = table.Column<int>(type: "INTEGER", nullable: true),
                    SummaryJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    ConfirmationId = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmationOutcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_HealthCheck",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    WarnThreshold = table.Column<long>(type: "INTEGER", nullable: true),
                    CriticalThreshold = table.Column<long>(type: "INTEGER", nullable: true),
                    ModifiedUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastProbeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAggregatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    LastValue = table.Column<long>(type: "INTEGER", nullable: true),
                    LastMessageCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NextDueUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_HealthCheck", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_HealthCheckResult",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Value = table.Column<long>(type: "INTEGER", nullable: true),
                    MessageCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TransitionId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_HealthCheckResult", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_OpsIncident",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FingerprintVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TitleKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TitleParamsJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    NormalizedMessage = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ModuleKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RuleKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    RuleId = table.Column<long>(type: "INTEGER", nullable: true),
                    Route = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OccurrenceCount = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LastVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LastRequestId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Regression = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReopenCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    AcknowledgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedReason = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    LastNotifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_OpsIncident", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_RuleFailure",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    FailedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_RuleFailure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_RuleHealth",
                columns: table => new
                {
                    RuleKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RuleId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    StreakResetUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastRequestId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PausedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPausedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PauseCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SkippedWhilePaused = table.Column<int>(type: "INTEGER", nullable: false),
                    ResumedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResumedBy = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_RuleHealth", x => new { x.RuleKind, x.RuleId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_OpsIncidentSample",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IncidentId = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Route = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AuditLogId = table.Column<long>(type: "INTEGER", nullable: true),
                    RawMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RawStack = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Browser = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Locale = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_OpsIncidentSample", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_OpsIncidentSample_Dynamic_OpsIncident_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Dynamic_OpsIncident",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AlertOutbox_CreatedUtc",
                table: "Dynamic_AlertOutbox",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AlertOutbox_DigestOutboxId",
                table: "Dynamic_AlertOutbox",
                column: "DigestOutboxId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AlertOutbox_SentUtc",
                table: "Dynamic_AlertOutbox",
                column: "SentUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AlertOutbox_Status_NextAttemptUtc",
                table: "Dynamic_AlertOutbox",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AuditLog_Action_OccurredUtc",
                table: "Dynamic_AuditLog",
                columns: new[] { "Action", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AuditLog_ActorUserId_OccurredUtc",
                table: "Dynamic_AuditLog",
                columns: new[] { "ActorUserId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AuditLog_OccurredUtc",
                table: "Dynamic_AuditLog",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AuditLog_TargetKind_TargetId_OccurredUtc",
                table: "Dynamic_AuditLog",
                columns: new[] { "TargetKind", "TargetId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AuditLog_TokenId_OccurredUtc",
                table: "Dynamic_AuditLog",
                columns: new[] { "TokenId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_HealthCheckResult_CheckedUtc",
                table: "Dynamic_HealthCheckResult",
                column: "CheckedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_HealthCheckResult_Code_CheckedUtc",
                table: "Dynamic_HealthCheckResult",
                columns: new[] { "Code", "CheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_HealthCheckResult_Code_InstanceId_CheckedUtc",
                table: "Dynamic_HealthCheckResult",
                columns: new[] { "Code", "InstanceId", "CheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_HealthCheckResult_TransitionId",
                table: "Dynamic_HealthCheckResult",
                column: "TransitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_OpsIncident_Fingerprint",
                table: "Dynamic_OpsIncident",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_OpsIncident_ResolvedUtc",
                table: "Dynamic_OpsIncident",
                column: "ResolvedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_OpsIncident_Status_LastSeenUtc",
                table: "Dynamic_OpsIncident",
                columns: new[] { "Status", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_OpsIncident_Status_Severity_LastSeenUtc",
                table: "Dynamic_OpsIncident",
                columns: new[] { "Status", "Severity", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_OpsIncidentSample_IncidentId_OccurredUtc",
                table: "Dynamic_OpsIncidentSample",
                columns: new[] { "IncidentId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_OpsIncidentSample_OccurredUtc",
                table: "Dynamic_OpsIncidentSample",
                column: "OccurredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RuleFailure_FailedUtc",
                table: "Dynamic_RuleFailure",
                column: "FailedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RuleFailure_RuleKind_RuleId_FailedUtc",
                table: "Dynamic_RuleFailure",
                columns: new[] { "RuleKind", "RuleId", "FailedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AlertChannel");

            migrationBuilder.DropTable(
                name: "Dynamic_AlertOutbox");

            migrationBuilder.DropTable(
                name: "Dynamic_AuditLog");

            migrationBuilder.DropTable(
                name: "Dynamic_HealthCheck");

            migrationBuilder.DropTable(
                name: "Dynamic_HealthCheckResult");

            migrationBuilder.DropTable(
                name: "Dynamic_OpsIncidentSample");

            migrationBuilder.DropTable(
                name: "Dynamic_RuleFailure");

            migrationBuilder.DropTable(
                name: "Dynamic_RuleHealth");

            migrationBuilder.DropTable(
                name: "Dynamic_OpsIncident");
        }
    }
}
