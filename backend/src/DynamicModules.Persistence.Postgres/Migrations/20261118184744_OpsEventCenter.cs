using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
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
                    Kind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Host = table.Column<string>(type: "text", maxLength: 255, nullable: true, collation: "C"),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Security = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    UserName = table.Column<string>(type: "text", maxLength: 255, nullable: true, collation: "C"),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    FromAddress = table.Column<string>(type: "text", maxLength: 255, nullable: true, collation: "C"),
                    FromName = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    ToAddresses = table.Column<string>(type: "text", maxLength: 2000, nullable: true, collation: "C"),
                    MailLocale = table.Column<string>(type: "text", maxLength: 10, nullable: true, collation: "C"),
                    LastClaimUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", maxLength: 500, nullable: true, collation: "C"),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AlertChannel", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AlertOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Channel = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    IncidentId = table.Column<long>(type: "bigint", nullable: true),
                    Transition = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    TransitionId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    Subject = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    Body = table.Column<string>(type: "text", maxLength: 4000, nullable: false, collation: "C"),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    ClaimedBy = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    ClaimedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DigestOutboxId = table.Column<long>(type: "bigint", nullable: true),
                    LastError = table.Column<string>(type: "text", maxLength: 500, nullable: true, collation: "C"),
                    SentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AlertOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ActorKind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Channel = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    TokenId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    RiskLevel = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    TargetKind = table.Column<string>(type: "text", maxLength: 30, nullable: true, collation: "C"),
                    TargetId = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    TargetLabel = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    ArgumentsHash = table.Column<string>(type: "text", maxLength: 64, nullable: true, collation: "C"),
                    ResultCount = table.Column<int>(type: "integer", nullable: true),
                    SummaryJson = table.Column<string>(type: "text", maxLength: 16000, nullable: true, collation: "C"),
                    ConfirmationId = table.Column<long>(type: "bigint", nullable: true),
                    ConfirmationOutcome = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    Result = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    ReasonCode = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    RequestId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    ClientIp = table.Column<string>(type: "text", maxLength: 45, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_HealthCheck",
                columns: table => new
                {
                    Code = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    WarnThreshold = table.Column<long>(type: "bigint", nullable: true),
                    CriticalThreshold = table.Column<long>(type: "bigint", nullable: true),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastProbeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAggregatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    LastValue = table.Column<long>(type: "bigint", nullable: true),
                    LastMessageCode = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    NextDueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_HealthCheck", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_HealthCheckResult",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CheckedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Value = table.Column<long>(type: "bigint", nullable: true),
                    MessageCode = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    InstanceId = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    TransitionId = table.Column<string>(type: "text", maxLength: 32, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_HealthCheckResult", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_OpsIncident",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Fingerprint = table.Column<string>(type: "text", maxLength: 16, nullable: false, collation: "C"),
                    FingerprintVersion = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Category = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Severity = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Code = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    TitleKey = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    TitleParamsJson = table.Column<string>(type: "text", maxLength: 2000, nullable: true, collation: "C"),
                    NormalizedMessage = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    ModuleKey = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    RuleKind = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    RuleId = table.Column<long>(type: "bigint", nullable: true),
                    Route = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    OccurrenceCount = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstVersion = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    LastVersion = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    LastRequestId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    Regression = table.Column<bool>(type: "boolean", nullable: false),
                    ReopenCount = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    AcknowledgedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ResolvedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedReason = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    LastNotifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_OpsIncident", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_RuleFailure",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleKind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    FailedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_RuleFailure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_RuleHealth",
                columns: table => new
                {
                    RuleKind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    StreakResetUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    LastRequestId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    PausedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPausedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PauseCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedWhilePaused = table.Column<int>(type: "integer", nullable: false),
                    ResumedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResumedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_RuleHealth", x => new { x.RuleKind, x.RuleId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_OpsIncidentSample",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IncidentId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestId = table.Column<string>(type: "text", maxLength: 32, nullable: true, collation: "C"),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    Version = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    Route = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    AuditLogId = table.Column<long>(type: "bigint", nullable: true),
                    RawMessage = table.Column<string>(type: "text", maxLength: 500, nullable: true, collation: "C"),
                    RawStack = table.Column<string>(type: "text", maxLength: 4000, nullable: true, collation: "C"),
                    Browser = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    Locale = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C")
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
