using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 授權寫入邊界的清單測試（權限軸設計 §7.4 清單測試 1–9，PAX-23）。原始碼掃描照 <see cref="NoBusinessWordsTests"/> 的寫法讀
/// <c>backend/src/**/*.cs</c>（排除 bin、obj），掃描器在 <see cref="AuthorizationWriteScanner"/>；反射用正式組件，模型掃描用
/// <see cref="DynamicDbContext"/> 在 SQLite 與 PostgreSQL 兩個 provider 下的模型。
/// 清單依 2026-11-20 的掃描結果登記；新增項目要改這裡的清單，並在該項後面寫一行理由。
/// </summary>
public class AuthorizationWriteInventoryTests
{
    // ── 登記清單 ──────────────────────────────────────────────────────────────

    private const string App = "DynamicModules.Application/";
    private const string Repo = "DynamicModules.Persistence/Repositories/Dynamic/";
    private const string Seed = "DynamicModules.Persistence/Seed/";

    /// <summary>清單測試 1：宣告 IPermissionAdminRequest 的正式請求（§7.2 表中「再加」與「新」的 13 列）。</summary>
    private static readonly string[] PermissionAdminRequests =
    [
        "SaveModulePermissionsRequest",          // 模組權限存檔：授權修改本身
        "StartDemoDataRequest",                  // 產生 Demo 資料：等於批次授權（M4）
        "GenerateDemoDataRequest",               // 背景的 Demo 產生，自己再過一次 gate（SuppressFlow 帶不到前台許可）
        "OpenAppRequest", "ReopenAppRequest", "CloseAppRequest", "DeleteAppRequest", // 開關刪應用程式：寫群組權限與欄位可見列
        "SetGroupMembersRequest", "CreateUserRequest", "UpdateUserRequest",          // 成員表
        "DeleteGroupRequest",                    // 刪群組：成員與五種授權列
        "UpdateGroupSystemPrivilegesRequest", "UpdateUserSystemPrivilegesRequest",   // 系統權限代碼列
    ];

    /// <summary>清單測試 2：AuthorizationWriteScope.RunAsync 的呼叫，恰為這四處。</summary>
    private static readonly HashSet<PermitSite> PermitSites =
    [
        new($"{App}Common/Security/PermissionAdminGate.cs", "RunAsync", "Authorized"),            // behavior 與條件式檢查都經 gate（T6）
        new($"{App}Dynamic/AppService/SharedMasters/MasterDesigner.cs", "BindFieldAsync", "Derived"), // 貢獻者區塊新增欄位時複製可見列
        new($"{Repo}DeletionRepository.cs", "RunCleanupAsync", "Cleanup"),                       // 刪模組、刪欄位的清除，結束時驗目標已刪
        new($"{Seed}SeedData.cs", "EnsureSeededAsync", "Seed"),                                 // 啟動種子與升級帶入（PermissionAxesImport）
    ];

    /// <summary>清單測試 3：可以取 Set&lt;守衛型別&gt; 的檔案。</summary>
    private static readonly HashSet<string> GuardSetFiles =
    [
        $"{Repo}PermissionRepository.cs",   // 模組權限、系統權限、成員的讀寫
        $"{Repo}SharedMasterRepository.cs", // 應用程式授權、欄位可見列、動作範圍
        $"{Repo}AccountRepository.cs",      // 帳號的成員與個人權限、刪群組
        $"{Repo}DeletionRepository.cs",     // 刪除清理的兩個批次入口
        $"{Seed}DemoPeople.cs",             // Demo 示範帳號與成員（沿用背景 GenerateDemoDataRequest 的許可）
        $"{Seed}PermissionAxesImport.cs",   // 升級帶入（Seed 許可內）
    ];

    /// <summary>清單測試 5：四個守衛批次入口與它們該 Demand 的入口值。</summary>
    private static readonly Dictionary<BatchMethod, string> GuardBatchEntries = new()
    {
        [new($"{Repo}PermissionRepository.cs", "ReplaceModuleRowsAsync")] = "ReplaceModuleRows",
        [new($"{Repo}SharedMasterRepository.cs", "ReplaceActionScopesAsync")] = "ReplaceActionScopes",
        [new($"{Repo}DeletionRepository.cs", "PurgeModuleAsync")] = "PurgeModule",
        [new($"{Repo}DeletionRepository.cs", "PurgeFieldsAsync")] = "PurgeFields",
    };

    /// <summary>清單測試 5：其餘非守衛的批次方法（每個呼叫的接收者都要追到 Set&lt;具體的非守衛型別&gt;()）。</summary>
    private static readonly HashSet<BatchMethod> NonGuardBatchMethods =
    [
        new($"{Repo}AlertRepositories.cs", "LockAsync"),              // 告警通道（AlertChannel）搶鎖
        new($"{Repo}LocalePackRepository.cs", "DeleteForLocaleAsync"), // 語系覆寫層與預覽（LocalePack／LocalePackPreview）移除語系時清除
        new($"{Repo}LocalePackRepository.cs", "PrunePreviewsAsync"),  // 語系包預覽暫存逾期與超量清除
        new($"{Repo}AlertRepositories.cs", "RecordSuccessAsync"),     // 告警通道送出成功的狀態
        new($"{Repo}AlertRepositories.cs", "RecordFailureAsync"),     // 告警通道送出失敗的狀態
        new($"{Repo}AlertRepositories.cs", "TryClaimAsync"),          // 告警外送佇列（AlertOutbox）認領
        new($"{Repo}AlertRepositories.cs", "DigestPendingAsync"),     // 告警外送佇列併成摘要
        new($"{Repo}AlertRepositories.cs", "AttachToDigestAsync"),    // 告警外送佇列掛到摘要
        new($"{Repo}AlertRepositories.cs", "TryCompleteAsync"),       // 告警外送佇列完成
        new($"{Repo}AlertRepositories.cs", "MarkSentDirectAsync"),    // 告警直送標已送
        new($"{Repo}AlertRepositories.cs", "ClaimForDirectAsync"),    // 告警直送認領
        new($"{Repo}AlertRepositories.cs", "ReleaseDirectAsync"),     // 告警直送釋放
        new($"{Repo}AlertRepositories.cs", "SkipPendingAsync"),       // 告警外送佇列略過
        new($"{Repo}AlertRepositories.cs", "PurgeBatchAsync"),        // 告警外送佇列分批清除
        new($"{Repo}ApprovalRepositories.cs", "TryReplaceDraftAsync"), // 簽核流程（ApprovalFlow）草稿樂觀鎖
        new($"{Repo}ApprovalRepositories.cs", "RemoveAsync"),         // 刪簽核流程的版本（ApprovalFlowVersion）
        new($"{Repo}ApprovalRepositories.cs", "TryBumpRowVersionAsync"), // 簽核實例（ApprovalInstance）列版本
        new($"{Repo}ApprovalRepositories.cs", "TryClaimReminderAsync"), // 簽核任務（ApprovalTask）提醒認領
        new($"{Repo}AuditLogRepository.cs", "PurgeBatchAsync"),       // 稽核紀錄（AuditLogEntry）分批清除
        new($"{Repo}EffectsRepositories.cs", "PurgeOlderThanAsync"),  // 操作紀錄（OperationRecord）清除
        new($"{Repo}HealthCheckRepository.cs", "TouchProbeAsync"),    // 健康檢查（HealthCheck）探測時間
        new($"{Repo}HealthCheckRepository.cs", "LockAsync"),          // 健康檢查彙整搶鎖
        new($"{Repo}HealthCheckRepository.cs", "UpdateProjectionAsync"), // 健康檢查狀態投影
        new($"{Repo}HealthCheckRepository.cs", "AdvanceNextDueAsync"), // 健康檢查下次時間
        new($"{Repo}HealthCheckRepository.cs", "PurgeResultsBatchAsync"), // 健康檢查結果（HealthCheckResult）分批清除
        new($"{Repo}OpsIncidentRepository.cs", "TryBumpAsync"),       // 維運事件（OpsIncident）計數
        new($"{Repo}OpsIncidentRepository.cs", "TryRegressAsync"),    // 維運事件退回開啟
        new($"{Repo}OpsIncidentRepository.cs", "TryResolveAsync"),    // 維運事件解決
        new($"{Repo}OpsIncidentRepository.cs", "TryAcknowledgeAsync"), // 維運事件確認
        new($"{Repo}OpsIncidentRepository.cs", "MarkNotifiedAsync"),  // 維運事件通知時間
        new($"{Repo}OpsIncidentRepository.cs", "TryClaimRenotifyAsync"), // 維運事件重新通知認領
        new($"{Repo}OpsIncidentRepository.cs", "ExpireBatchAsync"),   // 維運事件逾期
        new($"{Repo}OpsIncidentRepository.cs", "TrimSamplesAsync"),   // 維運事件樣本（OpsIncidentSample）修剪
        new($"{Repo}OpsIncidentRepository.cs", "PurgeResolvedBatchAsync"), // 已解決的維運事件分批清除
        new($"{Repo}OpsIncidentRepository.cs", "PurgeSamplesBatchAsync"), // 維運事件樣本分批清除
        new($"{Repo}RecordRepository.cs", "RemoveFieldIndexAsync"),   // 記錄索引（RecordIndex）
        new($"{Repo}RecordRepository.cs", "ReplaceIndexAsync"),       // 記錄索引重建
        new($"{Repo}RecordRepository.cs", "ReplaceFieldIndexAsync"),  // 單一欄位的記錄索引重建
        new($"{Repo}RecordRepository.cs", "ClearFieldIndexAsync"),    // 單一欄位的記錄索引清除
        new($"{Repo}RecordRepository.cs", "ShiftDateTimeIndexAsync"), // 改營運時區時平移日期時間索引
        new($"{Repo}RuleHealthRepository.cs", "BumpFailureAsync"),    // 規則健康（RuleHealth）失敗計數
        new($"{Repo}RuleHealthRepository.cs", "ResetStreakAsync"),    // 規則健康連續失敗歸零
        new($"{Repo}RuleHealthRepository.cs", "PruneFailuresAsync"),  // 規則失敗列（RuleFailure）修剪
        new($"{Repo}RuleHealthRepository.cs", "TryPauseAsync"),       // 規則自動暫停
        new($"{Repo}RuleHealthRepository.cs", "AddSkippedAsync"),     // 暫停中略過次數
        new($"{Repo}RuleHealthRepository.cs", "TryResumeAsync"),      // 規則恢復
        new($"{Repo}RuleHealthRepository.cs", "DeleteAsync"),         // 刪規則時的健康與失敗列
        new($"{Repo}RuleHealthRepository.cs", "PurgeFailuresBatchAsync"), // 規則失敗列分批清除
        new($"{Repo}ScheduledJobRepository.cs", "TryAcquireAsync"),   // 排程工作（ScheduledJob）租約
        new($"{Repo}ScheduledJobRepository.cs", "HoldsLeaseAsync"),   // 排程工作租約確認
        new($"{Repo}ScheduledJobRepository.cs", "RenewAsync"),        // 排程工作租約續約
        new($"{Repo}ScheduledJobRepository.cs", "FinishAsync"),       // 排程工作完成
        new($"{Repo}TaskRepositories.cs", "TryClaimVersionAsync"),    // 任務（TaskItem）版本認領
        new($"{Repo}TaskRepositories.cs", "TryMarkDueSoonNotifiedAsync"), // 任務即將到期通知
        new($"{Repo}TaskRepositories.cs", "TryMarkOverdueNotifiedAsync"), // 任務逾期通知
        new($"{Repo}TaskRepositories.cs", "LockAsync"),               // 任務規則（TaskRule）搶鎖
        new($"{Repo}TaskRepositories.cs", "MarkAllReadAsync"),        // 通知（Notification）全部已讀
        new($"{Repo}TaskRepositories.cs", "PurgeOlderThanAsync"),     // 通知清除
        new($"{Repo}TaskRepositories.cs", "RemoveBySourceAsync"),     // 依來源刪通知
        new($"{Seed}SeedData.cs", "EnsureSeededAsync"),               // 匿名記錄與個人檢視的 OwnerUserId 改歸 admin（Record、ListView）
    ];

    /// <summary>清單測試 6：用原生 SQL（EF 的 ExecuteSql／FromSql／SqlQuery、Dapper、CreateCommand…）的檔案；逐檔確認不寫守衛表。</summary>
    private static readonly HashSet<string> RawSqlFiles =
    [
        "DynamicModules.Persistence/Dynamic/Query/DynamicQueryBuilder.cs",  // 記錄清單查詢（Record、RecordIndex、FieldOption、簽核狀態），只讀
        "DynamicModules.Persistence/Dynamic/Query/ReportQueryBuilder.cs",   // 報表聚合查詢（Record、RecordIndex），只讀
        "DynamicModules.Persistence/Dynamic/Query/ReportSqlSnapshot.cs",    // 報表讀取快照的探測查詢（SQLite），只讀
        "DynamicModules.Persistence/Repositories/Dynamic/RecordRepository.cs", // 以 Dapper 執行記錄清單查詢，只讀
        "DynamicModules.Persistence/Repositories/Dynamic/ReportQueryRepository.cs", // 以 Dapper 執行報表聚合，只讀
        "DynamicModules.Persistence/SqliteDatabaseHealthProbe.cs",          // PRAGMA quick_check／foreign_key_check
        "DynamicModules.Persistence/SqliteMmapInterceptor.cs",              // PRAGMA mmap_size
        "DynamicModules.Persistence.Postgres/PostgresReportSqlSnapshot.cs", // SET TRANSACTION READ ONLY 與 statement_timeout
        "DynamicModules.Persistence.Postgres/PostgresStartupLock.cs",       // pg_advisory_lock／unlock（啟動鎖）
        "DynamicModules.Persistence.Postgres/PostgresWriteLockInterceptor.cs", // pg_advisory_xact_lock（全庫寫入鎖）
    ];

    /// <summary>清單測試 8：WebAPI 可以引用 DbContext 的檔案（且不得出現守衛型別）。</summary>
    private static readonly HashSet<string> WebApiDbFiles =
    [
        "DynamicModules.WebAPI/Program.cs",                    // 組 DI、啟動 migrate 與種子
        "DynamicModules.WebAPI/Security/SessionMiddleware.cs", // 每個請求讀登入工作階段
        "DynamicModules.WebAPI/Security/AdminBootstrap.cs",    // 首次啟動建立 admin 帳號
    ];

    /// <summary>不是 EF 的 Set&lt;X&gt;：ASP.NET 的 HttpContext.Features.Set（上傳大小限制）。</summary>
    private static readonly HashSet<(string, string)> NonEfSetCalls =
    [
        ("DynamicModules.WebAPI/Controllers/v3/Dynamic/UploadGuard.cs", "IFormFeature"),
    ];

    // ── 原始碼與模型 ──────────────────────────────────────────────────────────

    internal static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DynamicModules.Application"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("找不到 backend"), "src");
    }

    internal static List<ScanSource> Sources()
    {
        var src = SrcRoot();
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .Where(p => !p.Split('/').Any(part => part is "bin" or "obj"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new ScanSource(p, File.ReadAllText(Path.Combine(src, p))))
            .ToList();
    }

    internal static IModel SqliteModel()
    {
        using var db = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>().UseSqlite("DataSource=:memory:").Options);
        return db.Model;
    }

    internal static IModel PostgresModel()
    {
        var options = (DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(),
            "Host=localhost;Database=unused", new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options;
        using var db = new DynamicDbContext(options);
        return db.Model;
    }

    private static bool IsGuardEntity(IReadOnlyEntityType e) => e.GetTableName() is { } t && AuthorizationTables.Guarded.Contains(t);

    /// <summary>守衛實體的 CLR 名稱，加上只被守衛實體繼承的基底型別（例如 ProgramPrivilege）。</summary>
    internal static HashSet<string> GuardTypeNames(IModel model)
    {
        var entities = model.GetEntityTypes().ToList();
        var names = entities.Where(IsGuardEntity).Select(e => e.ClrType.Name).ToHashSet();
        var bases = entities.Where(IsGuardEntity).SelectMany(e => Bases(e.ClrType)).Distinct()
            .Where(b => entities.Where(e => b.IsAssignableFrom(e.ClrType)).All(IsGuardEntity));
        names.UnionWith(bases.Select(b => b.Name));
        return names;
    }

    private static IEnumerable<Type> Bases(Type t)
    {
        for (var b = t.BaseType; b is not null && b != typeof(object); b = b.BaseType) yield return b;
    }

    internal static ScanRegistry Registry(IModel model) => new()
    {
        GuardTypes = GuardTypeNames(model),
        EntityTypes = model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet(),
        NonEfSetCalls = NonEfSetCalls,
        PermitSites = PermitSites,
        GuardSetFiles = GuardSetFiles,
        GuardBatchEntries = GuardBatchEntries,
        NonGuardBatchMethods = NonGuardBatchMethods,
        RawSqlFiles = RawSqlFiles,
        WebApiDbFiles = WebApiDbFiles,
    };

    private static ScanResult ScanSrc() => AuthorizationWriteScanner.Scan(Sources(), Registry(SqliteModel()));

    private static IEnumerable<string> Matching(ScanResult r, params string[] keywords) =>
        r.Violations.Where(v => keywords.Any(v.Contains));

    /// <summary>違規全部列進失敗訊息（FluentAssertions 只印前幾筆）。</summary>
    private static void None(IEnumerable<string> violations)
    {
        var list = violations.ToList();
        list.Should().BeEmpty("違規共 {0} 筆：\n{1}", list.Count, string.Join("\n", list));
    }

    // ── 測試 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_is_not_vacuous()
    {
        var sources = Sources();
        sources.Count.Should().BeGreaterThan(500);
        sources.Should().Contain(s => s.Path.StartsWith("DynamicModules.Persistence/Migrations/", StringComparison.Ordinal));
        sources.Should().Contain(s => s.Path.StartsWith("DynamicModules.Persistence.Postgres/Migrations/", StringComparison.Ordinal));
        GuardTypeNames(SqliteModel()).Should().Contain(["FieldGrant", "UserGroupMapping", "GroupProgramPrivilege", "ProgramPrivilege"]);
    }

    /// <summary>清單測試 1：實作 IPermissionAdminRequest 的正式型別恰為 §7.2 的 13 個。</summary>
    [Fact]
    public void PAX23_1_PermissionAdminRequests_AreExactlyTheThirteen()
    {
        var assemblies = new[]
        {
            typeof(IPermissionAdminRequest).Assembly, typeof(AuthorizationTables).Assembly,
            typeof(DynamicModules.Persistence.DependencyInjection).Assembly, typeof(PostgresDatabase).Assembly,
        };
        var declared = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => !t.IsInterface && typeof(IPermissionAdminRequest).IsAssignableFrom(t)).Select(t => t.Name).ToList();
        declared.Should().BeEquivalentTo(PermissionAdminRequests);
        PermissionAdminRequests.Should().HaveCount(13);

        // WebAPI 組件不在測試專案的參照內，改看原始碼：除了 Application 之外沒有人宣告它
        Sources().Where(s => !s.Path.StartsWith(App, StringComparison.Ordinal) && AuthorizationWriteScanner.CodeOf(s.Text).Contains("IPermissionAdminRequest"))
            .Select(s => s.Path).Should().BeEmpty();
    }

    /// <summary>清單測試 2：開許可的位置恰為四處、種類相符；src 不得使用測試許可。</summary>
    [Fact]
    public void PAX23_2_PermitSites_AreExactlyTheRegistered()
    {
        var r = ScanSrc();
        None(Matching(r, "開許可", "測試許可", "許可種類", "AuthorizationWriteScope"));
        r.PermitSites.Should().BeEquivalentTo(PermitSites);
    }

    /// <summary>清單測試 3：Set&lt;守衛型別&gt; 只在登記檔案；沒有不具體的 Set&lt;T&gt;、Set(typeof…)；不交出守衛型別的查詢型別。</summary>
    [Fact]
    public void PAX23_3_GuardEntities_OnlyFromRegisteredFiles()
    {
        var r = ScanSrc();
        None(Matching(r, "的檔案取 Set<", "型別參數不是具體", "Set(typeof", "交出守衛型別"));
        r.GuardSetFiles.Should().BeEquivalentTo(GuardSetFiles);
    }

    /// <summary>清單測試 4：沒有任何 navigation 指向守衛型別（兩個 provider 的模型）。</summary>
    [Fact]
    public void PAX23_4_NoNavigationTargetsAGuardEntity()
    {
        foreach (var model in new[] { SqliteModel(), PostgresModel() })
        {
            var hits = model.GetEntityTypes()
                .SelectMany(e => e.GetNavigations().Cast<INavigationBase>().Concat(e.GetSkipNavigations())
                    .Where(n => IsGuardEntity(n.TargetEntityType)).Select(n => $"{e.ClrType.Name}.{n.Name} → {n.TargetEntityType.ClrType.Name}"))
                .ToList();
            hits.Should().BeEmpty();
        }
    }

    /// <summary>清單測試 5：每個呼叫批次指令的方法都登記；守衛入口先 Demand；非守衛方法的接收者追得到 Set&lt;具體非守衛型別&gt;()；ExecuteUpdate 方法不碰守衛欄。</summary>
    [Fact]
    public void PAX23_5_BatchMethods_AreRegisteredAndTraceable()
    {
        var r = ScanSrc();
        None(Matching(r, "批次"));
        r.BatchMethods.Should().BeEquivalentTo(GuardBatchEntries.Keys.Concat(NonGuardBatchMethods));
    }

    /// <summary>清單測試 6：原生 SQL 的使用檔案恰為登記清單；字串常值不含守衛表名（設定、常數檔、migration 三處例外）。</summary>
    [Fact]
    public void PAX23_6_RawSql_OnlyInRegisteredFiles_AndNoGuardTableLiterals()
    {
        var r = ScanSrc();
        None(Matching(r, "原生 SQL", "守衛表名"));
        r.RawSqlFiles.Should().BeEquivalentTo(RawSqlFiles);
    }

    /// <summary>清單測試 7：migration 不得對守衛表或 Dynamic_Field 寫資料。</summary>
    [Fact]
    public void PAX23_7_Migrations_WriteNoGuardData()
    {
        var r = ScanSrc();
        None(Matching(r, "migration"));
    }

    /// <summary>清單測試 8：Application 不引用 DbContext；WebAPI 只有登記的三個檔案引用，且不出現守衛型別。</summary>
    [Fact]
    public void PAX23_8_Layers()
    {
        var r = ScanSrc();
        None(Matching(r, "Application 不得", "WebAPI"));
        r.WebApiDbFiles.Should().BeEquivalentTo(WebApiDbFiles);
    }

    /// <summary>清單測試 9：表名像授權表的實體都在守衛表或排除清單；守衛表常數的每張表都在模型裡（兩個 provider）。</summary>
    [Fact]
    public void PAX23_9_AuthorizationLikeTables_AreGuardedOrExcluded()
    {
        string[] words = ["Privilege", "Permission", "Scope", "Grant", "GroupMapping"];
        foreach (var model in new[] { SqliteModel(), PostgresModel() })
        {
            var tables = model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().ToHashSet();
            tables.Where(t => words.Any(t.Contains) && !AuthorizationTables.Guarded.Contains(t) && t != AuthorizationTables.PermissionGroup)
                .Should().BeEmpty();
            AuthorizationTables.Guarded.Append(AuthorizationTables.Field).Append(AuthorizationTables.PermissionGroup)
                .Where(t => !tables.Contains(t)).Should().BeEmpty();
        }
    }

    /// <summary>清單沒有過期：登記了但掃不到的項目（例如方法改名）也算失敗。</summary>
    [Fact]
    public void PAX23_Registry_HasNoStaleEntries()
    {
        None(AuthorizationWriteScanner.Stale(ScanSrc(), Registry(SqliteModel())));
    }

    /// <summary>整體：所有違規一次列出（個別測試只看自己那一條的關鍵字）。</summary>
    [Fact]
    public void PAX23_AllChecks_ReportNoViolations()
    {
        None(ScanSrc().Violations);
    }
}
