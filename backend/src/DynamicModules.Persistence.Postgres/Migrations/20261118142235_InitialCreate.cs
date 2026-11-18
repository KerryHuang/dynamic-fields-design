using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DynamicModules.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dynamic_App",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "text", maxLength: 10, nullable: false, defaultValue: "App", collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Icon = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    PackageVersion = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    FormatVersion = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Requires = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    RowVersion = table.Column<string>(type: "text", maxLength: 36, nullable: false, collation: "C"),
                    IntegrationsJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_App", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppPackage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    Version = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Json = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppPackage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalDelegate",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    DelegateUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalDelegate", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalInstance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    FlowId = table.Column<long>(type: "bigint", nullable: true),
                    FlowVersionId = table.Column<long>(type: "bigint", nullable: true),
                    FlowSetSnapshot = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    RouteKind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    FromOptionId = table.Column<long>(type: "bigint", nullable: false),
                    ToOptionId = table.Column<long>(type: "bigint", nullable: false),
                    InitiatorUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    RestartedFromInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    CurrentStageRunId = table.Column<long>(type: "bigint", nullable: true),
                    TitleSnapshot = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    Picks = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SubmittedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RowVersion = table.Column<string>(type: "text", maxLength: 36, nullable: false, collation: "C"),
                    OperationId = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalInstance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppUser",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    DisplayName = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    IsSystemAdmin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    PasswordHash = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDemoAccount = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", maxLength: 50, nullable: false, defaultValue: "initial", collation: "C"),
                    ThemeMode = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    ThemeStyle = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppUser", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Dashboard",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    Name = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    LayoutJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Dashboard", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Department",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    ManagerUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Department", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_EventFact",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventKey = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    EffectCode = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    SourceRecordId = table.Column<long>(type: "bigint", nullable: false),
                    ItemKey = table.Column<long>(type: "bigint", nullable: false),
                    TargetRecordId = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_EventFact", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldOptionScope",
                columns: table => new
                {
                    OptionId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldOptionScope", x => new { x.OptionId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldPermission",
                columns: table => new
                {
                    FieldId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    Access = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldPermission", x => new { x.FieldId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_GroupProgramPrivilege",
                columns: table => new
                {
                    ProgramCode = table.Column<string>(type: "text", maxLength: 8, nullable: false, collation: "C"),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    CanRead = table.Column<bool>(type: "boolean", nullable: false),
                    CanAdd = table.Column<bool>(type: "boolean", nullable: false),
                    CanEdit = table.Column<bool>(type: "boolean", nullable: false),
                    CanDelete = table.Column<bool>(type: "boolean", nullable: false),
                    CanPrint = table.Column<bool>(type: "boolean", nullable: false),
                    CanAmt = table.Column<bool>(type: "boolean", nullable: false),
                    CanApprove = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_GroupProgramPrivilege", x => new { x.GroupId, x.ProgramCode });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_LedgerReversal",
                columns: table => new
                {
                    OriginalRecordId = table.Column<long>(type: "bigint", nullable: false),
                    ReversalRecordId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_LedgerReversal", x => x.OriginalRecordId);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Module",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    ProgramCode = table.Column<string>(type: "text", maxLength: 8, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Icon = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    Section = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    SectionI18n = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    TitleFieldId = table.Column<long>(type: "bigint", nullable: true),
                    TitleTemplate = table.Column<string>(type: "text", maxLength: 8400, nullable: true, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "text", maxLength: 20, nullable: false, defaultValue: "Standalone", collation: "C"),
                    HostEntityKey = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    TasksEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Module", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ModuleDataScope",
                columns: table => new
                {
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ModuleDataScope", x => new { x.ModuleId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ModuleTemplate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    Json = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ModuleTemplate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Notification",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    TitleSnapshot = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    Link = table.Column<string>(type: "text", maxLength: 400, nullable: false, collation: "C"),
                    SourceType = table.Column<string>(type: "text", maxLength: 40, nullable: true, collation: "C"),
                    SourceId = table.Column<long>(type: "bigint", nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ReadTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Notification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_NumberSequence",
                columns: table => new
                {
                    FieldId = table.Column<long>(type: "bigint", nullable: false),
                    Period = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_NumberSequence", x => new { x.FieldId, x.Period });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Operation",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Key = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    RequestHash = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    ResultKind = table.Column<int>(type: "integer", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Operation", x => new { x.UserId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_PermissionGroup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_PermissionGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Program",
                columns: table => new
                {
                    ProgramCode = table.Column<string>(type: "text", maxLength: 8, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    IsBeta = table.Column<bool>(type: "boolean", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Program", x => x.ProgramCode);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Report",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    RowVersion = table.Column<string>(type: "text", maxLength: 36, nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Report", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ScheduledJob",
                columns: table => new
                {
                    JobKey = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    LeaseOwner = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    LastError = table.Column<string>(type: "text", maxLength: 1000, nullable: true, collation: "C"),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    NextDueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ScheduledJob", x => x.JobKey);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_SystemSetting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    Value = table.Column<string>(type: "text", maxLength: 500, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_SystemSetting", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskView",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    OwnerUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    ConfigJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    RowVersion = table.Column<string>(type: "text", maxLength: 36, nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskView", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserGroupMapping",
                columns: table => new
                {
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserGroupMapping", x => new { x.GroupId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserProgramPrivilege",
                columns: table => new
                {
                    ProgramCode = table.Column<string>(type: "text", maxLength: 8, nullable: false, collation: "C"),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CanRead = table.Column<bool>(type: "boolean", nullable: false),
                    CanAdd = table.Column<bool>(type: "boolean", nullable: false),
                    CanEdit = table.Column<bool>(type: "boolean", nullable: false),
                    CanDelete = table.Column<bool>(type: "boolean", nullable: false),
                    CanPrint = table.Column<bool>(type: "boolean", nullable: false),
                    CanAmt = table.Column<bool>(type: "boolean", nullable: false),
                    CanApprove = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserProgramPrivilege", x => new { x.UserId, x.ProgramCode });
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppIntegration",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Installed = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Reason = table.Column<string>(type: "text", maxLength: 2000, nullable: true, collation: "C"),
                    ItemsJson = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppIntegration", x => new { x.AppId, x.Code });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppIntegration_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppMasterUse",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    MasterAppId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppMasterUse", x => new { x.AppId, x.MasterAppId });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppMasterUse_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppMasterUse_Dynamic_App_MasterAppId",
                        column: x => x.MasterAppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalAction",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    StageRunId = table.Column<long>(type: "bigint", nullable: true),
                    TaskId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    ActorUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    OnBehalfOfUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    TargetUserIds = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Comment = table.Column<string>(type: "text", maxLength: 1000, nullable: true, collation: "C"),
                    DataJson = table.Column<string>(type: "text", maxLength: 4000, nullable: true, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    OperationId = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C")
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
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    FlowId = table.Column<long>(type: "bigint", nullable: false),
                    FlowVersionId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ApprovalInstanceFlow", x => new { x.InstanceId, x.FlowId });
                    table.ForeignKey(
                        name: "FK_Dynamic_ApprovalInstanceFlow_Dynamic_ApprovalInstance_Insta~",
                        column: x => x.InstanceId,
                        principalTable: "Dynamic_ApprovalInstance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalStageRun",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    StageCode = table.Column<string>(type: "text", maxLength: 40, nullable: true, collation: "C"),
                    NameSnapshot = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Mode = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    SkipReason = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    AddedByUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    AddSignUserIds = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ActivatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DueTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    NodeId = table.Column<string>(type: "text", maxLength: 40, nullable: true, collation: "C"),
                    BranchKey = table.Column<string>(type: "text", maxLength: 256, nullable: true, collation: "C"),
                    NodeKind = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    InEdgeId = table.Column<string>(type: "text", maxLength: 40, nullable: true, collation: "C"),
                    CloseReason = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C")
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StageRunId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    AssigneeUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    OriginalUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    SeatKey = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    Reason = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ActedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DueTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastRemindedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "Dynamic_DashboardAssignment",
                columns: table => new
                {
                    DashboardId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_DashboardAssignment", x => new { x.DashboardId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_Dynamic_DashboardAssignment_Dynamic_Dashboard_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dynamic_Dashboard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_DashboardPersonalVersion",
                columns: table => new
                {
                    DashboardId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    LayoutJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    BaseVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_DashboardPersonalVersion", x => new { x.DashboardId, x.UserId });
                    table.ForeignKey(
                        name: "FK_Dynamic_DashboardPersonalVersion_Dynamic_Dashboard_Dashboar~",
                        column: x => x.DashboardId,
                        principalTable: "Dynamic_Dashboard",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_UserDepartment",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    DepartmentId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_UserDepartment", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_Dynamic_UserDepartment_Dynamic_Department_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Dynamic_Department",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppModule",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppModule", x => new { x.AppId, x.Code });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppModule_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppModule_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalFlow",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    ToOptionId = table.Column<long>(type: "bigint", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentVersionId = table.Column<long>(type: "bigint", nullable: true),
                    DraftJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    DraftUpdatedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    DraftUpdatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                name: "Dynamic_Block",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    LabelI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Collapsed = table.Column<bool>(type: "boolean", nullable: false),
                    Columns = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Block", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Block_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Check",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Definition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                name: "Dynamic_Conversion",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SourceModuleId = table.Column<long>(type: "bigint", nullable: false),
                    TargetModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Definition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Conversion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Conversion_Dynamic_Module_SourceModuleId",
                        column: x => x.SourceModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Dynamic_Conversion_Dynamic_Module_TargetModuleId",
                        column: x => x.TargetModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SourceModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Definition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                name: "Dynamic_Ledger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Definition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                name: "Dynamic_ListView",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Columns = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Filter = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Sort = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ListView", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_ListView_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ModulePolicy",
                columns: table => new
                {
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    Value = table.Column<string>(type: "text", maxLength: 500, nullable: false, collation: "C"),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ModulePolicy", x => new { x.ModuleId, x.Code });
                    table.ForeignKey(
                        name: "FK_Dynamic_ModulePolicy_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_PrintTemplate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Body = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_PrintTemplate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_PrintTemplate_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Record",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    HostKey = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    OwnerUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    ValuesJson = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    RowVersion = table.Column<string>(type: "text", maxLength: 36, nullable: false, collation: "C"),
                    DeleteFlag = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Record", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Record_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Rule",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Condition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Targets = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    OptionIds = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    MessageI18n = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Rule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Rule_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Task",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", maxLength: 200, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "text", maxLength: 4000, nullable: true, collation: "C"),
                    AssigneeUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    ModuleId = table.Column<long>(type: "bigint", nullable: true),
                    RecordId = table.Column<long>(type: "bigint", nullable: true),
                    RecordTitleSnapshot = table.Column<string>(type: "text", maxLength: 200, nullable: true, collation: "C"),
                    RuleId = table.Column<long>(type: "bigint", nullable: true),
                    RuleOccurrence = table.Column<int>(type: "integer", nullable: true),
                    RuleTaskCode = table.Column<string>(type: "text", maxLength: 40, nullable: true, collation: "C"),
                    DueOverridden = table.Column<bool>(type: "boolean", nullable: false),
                    AssigneeOverridden = table.Column<bool>(type: "boolean", nullable: false),
                    CancelledByRule = table.Column<bool>(type: "boolean", nullable: false),
                    ParentTaskId = table.Column<long>(type: "bigint", nullable: true),
                    CancelledByParent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastDueSoonNotified = table.Column<DateOnly>(type: "date", nullable: true),
                    LastOverdueNotified = table.Column<DateOnly>(type: "date", nullable: true)
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    NameI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Repeat = table.Column<string>(type: "text", maxLength: 20, nullable: false, defaultValue: "Once", collation: "C"),
                    Trigger = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    WhenJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                name: "Dynamic_Workflow",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    StatusFieldId = table.Column<long>(type: "bigint", nullable: false),
                    InitialOptionId = table.Column<long>(type: "bigint", nullable: false),
                    States = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Transitions = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Workflow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Workflow_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppGrant",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceKind = table.Column<string>(type: "text", maxLength: 10, nullable: false, collation: "C"),
                    AppId = table.Column<long>(type: "bigint", nullable: true),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Scope = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppGrant", x => x.Id);
                    table.CheckConstraint("CK_AppGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Dynamic_AppGrant_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppGrant_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppGrant_Dynamic_PermissionGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Dynamic_PermissionGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ModuleActionScope",
                columns: table => new
                {
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Scope = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ModuleActionScope", x => new { x.ModuleId, x.GroupId, x.Action });
                    table.ForeignKey(
                        name: "FK_Dynamic_ModuleActionScope_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_ModuleActionScope_Dynamic_PermissionGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Dynamic_PermissionGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppReport",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppReport", x => new { x.AppId, x.ReportId });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppReport_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppReport_Dynamic_Report_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Dynamic_Report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ReportShare",
                columns: table => new
                {
                    ReportId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_ReportShare", x => new { x.ReportId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_Dynamic_ReportShare_Dynamic_Report_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Dynamic_Report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskViewShare",
                columns: table => new
                {
                    ViewId = table.Column<long>(type: "bigint", nullable: false),
                    TargetKind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    TargetId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_TaskViewShare", x => new { x.ViewId, x.TargetKind, x.TargetId });
                    table.ForeignKey(
                        name: "FK_Dynamic_TaskViewShare_Dynamic_TaskView_ViewId",
                        column: x => x.ViewId,
                        principalTable: "Dynamic_TaskView",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_ApprovalFlowVersion",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FlowId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Definition = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    LayoutJson = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    CreatedBy = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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
                name: "Dynamic_AppBlock",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    BlockId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppBlock", x => new { x.AppId, x.Name });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppBlock_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppBlock_Dynamic_Block_BlockId",
                        column: x => x.BlockId,
                        principalTable: "Dynamic_Block",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_Field",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModuleId = table.Column<long>(type: "bigint", nullable: false),
                    BlockId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    LabelI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Type = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    Config = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    VisibilityRestricted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_Field", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_Field_Dynamic_Block_BlockId",
                        column: x => x.BlockId,
                        principalTable: "Dynamic_Block",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_Field_Dynamic_Module_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Dynamic_Module",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskComment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C"),
                    AuthorUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    Body = table.Column<string>(type: "text", maxLength: 4000, nullable: false, collation: "C"),
                    SystemEvent = table.Column<string>(type: "text", maxLength: 40, nullable: true, collation: "C"),
                    SystemData = table.Column<string>(type: "text", maxLength: 2000, nullable: true, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Withdrawn = table.Column<bool>(type: "boolean", nullable: false),
                    WithdrawnBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    WithdrawnTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    Reason = table.Column<string>(type: "text", maxLength: 20, nullable: false, collation: "C")
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
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    Occurrence = table.Column<int>(type: "integer", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TriggeredBy = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C")
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    TitleI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    DescriptionI18n = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    AssigneeFieldId = table.Column<long>(type: "bigint", nullable: false),
                    DueFieldId = table.Column<long>(type: "bigint", nullable: true),
                    DueOffsetDays = table.Column<int>(type: "integer", nullable: false),
                    WhenJson = table.Column<string>(type: "text", nullable: true, collation: "C")
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
                name: "Dynamic_AppField",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    FieldId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppField", x => new { x.AppId, x.Name });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppField_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppField_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldGrant",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceKind = table.Column<string>(type: "text", maxLength: 10, nullable: false, collation: "C"),
                    FieldId = table.Column<long>(type: "bigint", nullable: false),
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldGrant", x => x.Id);
                    table.CheckConstraint("CK_FieldGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldGrant_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldGrant_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldGrant_Dynamic_PermissionGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Dynamic_PermissionGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_FieldOption",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FieldId = table.Column<long>(type: "bigint", nullable: false),
                    LabelI18n = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Color = table.Column<string>(type: "text", maxLength: 20, nullable: true, collation: "C"),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: true, collation: "C"),
                    ModifiedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_FieldOption", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Dynamic_FieldOption_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_RecordHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    FieldId = table.Column<long>(type: "bigint", nullable: true),
                    Operation = table.Column<string>(type: "text", maxLength: 10, nullable: false, collation: "C"),
                    OldValue = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    NewValue = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ChangedUserId = table.Column<string>(type: "text", maxLength: 50, nullable: false, collation: "C"),
                    ChangedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "Dynamic_RecordIndex",
                columns: table => new
                {
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    FieldId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    TextValue = table.Column<string>(type: "text", maxLength: 440, nullable: true, collation: "C"),
                    NumberValue = table.Column<double>(type: "double precision", nullable: true),
                    ScaledValue = table.Column<long>(type: "bigint", nullable: true),
                    DateValue = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RefKey = table.Column<string>(type: "text", maxLength: 100, nullable: true, collation: "C"),
                    TextTruncated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
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

            migrationBuilder.CreateTable(
                name: "Dynamic_TaskCommentAttachment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommentId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "text", maxLength: 150, nullable: false, collation: "C"),
                    ContentType = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "text", maxLength: 64, nullable: false, collation: "C"),
                    CreatedTime = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "Dynamic_AppOption",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    OptionId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppOption", x => new { x.AppId, x.Name });
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOption_Dynamic_App_AppId",
                        column: x => x.AppId,
                        principalTable: "Dynamic_App",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOption_Dynamic_FieldOption_OptionId",
                        column: x => x.OptionId,
                        principalTable: "Dynamic_FieldOption",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Dynamic_AppOrphan",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FormerAppKey = table.Column<string>(type: "text", maxLength: 40, nullable: false, collation: "C"),
                    Kind = table.Column<string>(type: "text", maxLength: 10, nullable: false, collation: "C"),
                    Name = table.Column<string>(type: "text", maxLength: 100, nullable: false, collation: "C"),
                    FieldId = table.Column<long>(type: "bigint", nullable: true),
                    OptionId = table.Column<long>(type: "bigint", nullable: true),
                    BlockId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dynamic_AppOrphan", x => x.Id);
                    table.CheckConstraint("CK_AppOrphan_One", "num_nonnulls(\"FieldId\", \"OptionId\", \"BlockId\") = 1");
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOrphan_Dynamic_Block_BlockId",
                        column: x => x.BlockId,
                        principalTable: "Dynamic_Block",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOrphan_Dynamic_FieldOption_OptionId",
                        column: x => x.OptionId,
                        principalTable: "Dynamic_FieldOption",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Dynamic_AppOrphan_Dynamic_Field_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Dynamic_Field",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_App_Key",
                table: "Dynamic_App",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppBlock_BlockId",
                table: "Dynamic_AppBlock",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppField_FieldId",
                table: "Dynamic_AppField",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppGrant_AppId_ModuleId_GroupId_Action",
                table: "Dynamic_AppGrant",
                columns: new[] { "AppId", "ModuleId", "GroupId", "Action" },
                unique: true,
                filter: "\"AppId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppGrant_GroupId",
                table: "Dynamic_AppGrant",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppGrant_ModuleId_GroupId_Action",
                table: "Dynamic_AppGrant",
                columns: new[] { "ModuleId", "GroupId", "Action" },
                unique: true,
                filter: "\"AppId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppMasterUse_MasterAppId",
                table: "Dynamic_AppMasterUse",
                column: "MasterAppId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppModule_ModuleId",
                table: "Dynamic_AppModule",
                column: "ModuleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOption_OptionId",
                table: "Dynamic_AppOption",
                column: "OptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_BlockId",
                table: "Dynamic_AppOrphan",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_FieldId",
                table: "Dynamic_AppOrphan",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_FormerAppKey_Kind_Name",
                table: "Dynamic_AppOrphan",
                columns: new[] { "FormerAppKey", "Kind", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppOrphan_OptionId",
                table: "Dynamic_AppOrphan",
                column: "OptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppPackage_Key",
                table: "Dynamic_AppPackage",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppReport_AppId_Code",
                table: "Dynamic_AppReport",
                columns: new[] { "AppId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_AppReport_ReportId",
                table: "Dynamic_AppReport",
                column: "ReportId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Block_ModuleId",
                table: "Dynamic_Block",
                column: "ModuleId");

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
                name: "IX_Dynamic_Conversion_Key",
                table: "Dynamic_Conversion",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Conversion_SourceModuleId",
                table: "Dynamic_Conversion",
                column: "SourceModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Conversion_TargetModuleId",
                table: "Dynamic_Conversion",
                column: "TargetModuleId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Dashboard_OwnerUserId",
                table: "Dynamic_Dashboard",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_DashboardAssignment_GroupId",
                table: "Dynamic_DashboardAssignment",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_DashboardPersonalVersion_UserId",
                table: "Dynamic_DashboardPersonalVersion",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Department_Code",
                table: "Dynamic_Department",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Department_ParentId",
                table: "Dynamic_Department",
                column: "ParentId");

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
                name: "IX_Dynamic_Field_BlockId",
                table: "Dynamic_Field",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Field_ModuleId_Key",
                table: "Dynamic_Field",
                columns: new[] { "ModuleId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_AppId",
                table: "Dynamic_FieldGrant",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_FieldId_GroupId",
                table: "Dynamic_FieldGrant",
                columns: new[] { "FieldId", "GroupId" },
                unique: true,
                filter: "\"AppId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_FieldId_GroupId_AppId",
                table: "Dynamic_FieldGrant",
                columns: new[] { "FieldId", "GroupId", "AppId" },
                unique: true,
                filter: "\"AppId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldGrant_GroupId",
                table: "Dynamic_FieldGrant",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_FieldOption_FieldId",
                table: "Dynamic_FieldOption",
                column: "FieldId");

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
                name: "IX_Dynamic_ListView_ModuleId_OwnerUserId",
                table: "Dynamic_ListView",
                columns: new[] { "ModuleId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Module_HostEntityKey",
                table: "Dynamic_Module",
                column: "HostEntityKey",
                unique: true,
                filter: "\"HostEntityKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Module_Key",
                table: "Dynamic_Module",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ModuleActionScope_GroupId",
                table: "Dynamic_ModuleActionScope",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ModuleTemplate_Key",
                table: "Dynamic_ModuleTemplate",
                column: "Key",
                unique: true);

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
                name: "IX_Dynamic_Operation_CreatedTime",
                table: "Dynamic_Operation",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_PermissionGroup_Key",
                table: "Dynamic_PermissionGroup",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_PrintTemplate_ModuleId_Key",
                table: "Dynamic_PrintTemplate",
                columns: new[] { "ModuleId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Record_ModuleId_DeleteFlag",
                table: "Dynamic_Record",
                columns: new[] { "ModuleId", "DeleteFlag" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Record_ModuleId_HostKey",
                table: "Dynamic_Record",
                columns: new[] { "ModuleId", "HostKey" },
                unique: true,
                filter: "\"HostKey\" IS NOT NULL AND NOT \"DeleteFlag\"");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordHistory_FieldId",
                table: "Dynamic_RecordHistory",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordHistory_RecordId_ChangedTime",
                table: "Dynamic_RecordHistory",
                columns: new[] { "RecordId", "ChangedTime" });

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

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_NoScaled",
                table: "Dynamic_RecordIndex",
                column: "FieldId",
                filter: "\"ScaledValue\" IS NULL AND \"NumberValue\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_RecordIndex_Truncated",
                table: "Dynamic_RecordIndex",
                columns: new[] { "FieldId", "TextTruncated" },
                filter: "\"TextTruncated\"");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Report_OwnerUserId",
                table: "Dynamic_Report",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_ReportShare_GroupId",
                table: "Dynamic_ReportShare",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Rule_ModuleId_Sequence",
                table: "Dynamic_Rule",
                columns: new[] { "ModuleId", "Sequence" });

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
                name: "IX_Dynamic_Task_ParentTaskId",
                table: "Dynamic_Task",
                column: "ParentTaskId");

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

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskView_OwnerUserId",
                table: "Dynamic_TaskView",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_TaskViewShare_TargetKind_TargetId",
                table: "Dynamic_TaskViewShare",
                columns: new[] { "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_UserDepartment_DepartmentId",
                table: "Dynamic_UserDepartment",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Dynamic_Workflow_ModuleId",
                table: "Dynamic_Workflow",
                column: "ModuleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Dynamic_AppBlock");

            migrationBuilder.DropTable(
                name: "Dynamic_AppField");

            migrationBuilder.DropTable(
                name: "Dynamic_AppGrant");

            migrationBuilder.DropTable(
                name: "Dynamic_AppIntegration");

            migrationBuilder.DropTable(
                name: "Dynamic_AppMasterUse");

            migrationBuilder.DropTable(
                name: "Dynamic_AppModule");

            migrationBuilder.DropTable(
                name: "Dynamic_AppOption");

            migrationBuilder.DropTable(
                name: "Dynamic_AppOrphan");

            migrationBuilder.DropTable(
                name: "Dynamic_AppPackage");

            migrationBuilder.DropTable(
                name: "Dynamic_AppReport");

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
                name: "Dynamic_AppUser");

            migrationBuilder.DropTable(
                name: "Dynamic_Check");

            migrationBuilder.DropTable(
                name: "Dynamic_Conversion");

            migrationBuilder.DropTable(
                name: "Dynamic_DashboardAssignment");

            migrationBuilder.DropTable(
                name: "Dynamic_DashboardPersonalVersion");

            migrationBuilder.DropTable(
                name: "Dynamic_Event");

            migrationBuilder.DropTable(
                name: "Dynamic_EventFact");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldGrant");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldOptionScope");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldPermission");

            migrationBuilder.DropTable(
                name: "Dynamic_GroupProgramPrivilege");

            migrationBuilder.DropTable(
                name: "Dynamic_Ledger");

            migrationBuilder.DropTable(
                name: "Dynamic_LedgerReversal");

            migrationBuilder.DropTable(
                name: "Dynamic_ListView");

            migrationBuilder.DropTable(
                name: "Dynamic_ModuleActionScope");

            migrationBuilder.DropTable(
                name: "Dynamic_ModuleDataScope");

            migrationBuilder.DropTable(
                name: "Dynamic_ModulePolicy");

            migrationBuilder.DropTable(
                name: "Dynamic_ModuleTemplate");

            migrationBuilder.DropTable(
                name: "Dynamic_Notification");

            migrationBuilder.DropTable(
                name: "Dynamic_NumberSequence");

            migrationBuilder.DropTable(
                name: "Dynamic_Operation");

            migrationBuilder.DropTable(
                name: "Dynamic_PrintTemplate");

            migrationBuilder.DropTable(
                name: "Dynamic_Program");

            migrationBuilder.DropTable(
                name: "Dynamic_RecordHistory");

            migrationBuilder.DropTable(
                name: "Dynamic_RecordIndex");

            migrationBuilder.DropTable(
                name: "Dynamic_ReportShare");

            migrationBuilder.DropTable(
                name: "Dynamic_Rule");

            migrationBuilder.DropTable(
                name: "Dynamic_ScheduledJob");

            migrationBuilder.DropTable(
                name: "Dynamic_SystemSetting");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskCommentAttachment");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskParticipant");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskRuleRun");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskRuleTask");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskViewShare");

            migrationBuilder.DropTable(
                name: "Dynamic_UserDepartment");

            migrationBuilder.DropTable(
                name: "Dynamic_UserGroupMapping");

            migrationBuilder.DropTable(
                name: "Dynamic_UserProgramPrivilege");

            migrationBuilder.DropTable(
                name: "Dynamic_Workflow");

            migrationBuilder.DropTable(
                name: "Dynamic_FieldOption");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalFlow");

            migrationBuilder.DropTable(
                name: "Dynamic_ApprovalInstance");

            migrationBuilder.DropTable(
                name: "Dynamic_Dashboard");

            migrationBuilder.DropTable(
                name: "Dynamic_App");

            migrationBuilder.DropTable(
                name: "Dynamic_PermissionGroup");

            migrationBuilder.DropTable(
                name: "Dynamic_Record");

            migrationBuilder.DropTable(
                name: "Dynamic_Report");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskComment");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskRule");

            migrationBuilder.DropTable(
                name: "Dynamic_TaskView");

            migrationBuilder.DropTable(
                name: "Dynamic_Department");

            migrationBuilder.DropTable(
                name: "Dynamic_Field");

            migrationBuilder.DropTable(
                name: "Dynamic_Task");

            migrationBuilder.DropTable(
                name: "Dynamic_Block");

            migrationBuilder.DropTable(
                name: "Dynamic_Module");
        }
    }
}
