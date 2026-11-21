using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 清單測試的掃描器自測（權限軸設計 §7.4 清單測試 10，PAX-23j）：§7.4 列的每個違規樣本都被回報（筆數逐樣本固定）、合法樣本回報零；
/// 樣本是字串，餵給清單測試用的同一個 <see cref="AuthorizationWriteScanner.Scan"/>，登記清單是自測專用的一份（守衛型別取正式模型）。
/// 「跨檔傳遞查詢」同時違反兩條（登記檔案交出 IQueryable&lt;守衛型別&gt;、另一檔的接收者追不到 Set），兩條防線各回報一筆，所以是 2。
/// 另加幾個 §7.4 沒列、但掃描器同樣要抓的樣本（標「另加」）。
/// </summary>
public class AuthorizationWriteScannerSelfTests
{
    private const string Repo = "DynamicModules.Persistence/Repositories/Dynamic/";
    private const string AccountRepo = Repo + "AccountRepository.cs";
    private const string PermissionRepo = Repo + "PermissionRepository.cs";
    private const string SampleRepo = Repo + "SampleRepository.cs";
    private const string OpsRepo = Repo + "OpsIncidentRepository.cs";
    private const string Purger = "DynamicModules.Application/Dynamic/SampleService/GrantPurger.cs";
    private const string GenericPurge = "DynamicModules.Persistence/GenericPurge.cs";

    private static readonly ScanRegistry Registry = SampleRegistry();

    private static ScanRegistry SampleRegistry()
    {
        var model = AuthorizationWriteInventoryTests.SqliteModel();
        return new ScanRegistry
        {
            GuardTypes = AuthorizationWriteInventoryTests.GuardTypeNames(model),
            EntityTypes = model.GetEntityTypes().Select(e => e.ClrType.Name).ToHashSet(),
            PermitSites = new HashSet<PermitSite>(),
            GuardSetFiles = new HashSet<string> { AccountRepo, PermissionRepo },
            GuardBatchEntries = new Dictionary<BatchMethod, string> { [new(PermissionRepo, "ReplaceModuleRowsAsync")] = "ReplaceModuleRows" },
            NonGuardBatchMethods = new HashSet<BatchMethod>
            {
                new(AccountRepo, "PurgeMembersAsync"), new(AccountRepo, "PurgeGrantsAsync"), new(AccountRepo, "PurgeGrantsViaPropertyAsync"),
                new(SampleRepo, "HideAsync"), new(SampleRepo, "AdoptAnonymousAsync"), new(Purger, "PurgeAsync"), new(GenericPurge, "PurgeAsync"),
                new(OpsRepo, "MarkNotifiedAsync"),
            },
            RawSqlFiles = new HashSet<string>(),
            WebApiDbFiles = new HashSet<string>(),
        };
    }

    private static ScanSource Cs(string path, string body) => new(path, $$"""
        using DynamicModules.Domain.Data;
        using DynamicModules.Domain.Entities.Dynamic;
        using Microsoft.EntityFrameworkCore;

        namespace DynamicModules.Samples;

        {{body}}
        """);

    public static TheoryData<string, int, string> Violations() => new()
    {
        { "UserGroupMapping 的 ExecuteDeleteAsync", 1, "追到守衛型別 Set<UserGroupMapping>" },
        { "經 Set<Module> 的導覽屬性改 VisibilityRestricted", 1, "守衛欄 VisibilityRestricted" },
        { "Set<Field> 批次改 ExcludeFromAi", 1, "守衛欄 ExcludeFromAi" },
        { "DELETE FROM public.\"Dynamic_FieldPermission\"", 1, "守衛表名 Dynamic_FieldPermission" },
        { "WITH x AS (…) DELETE FROM \"Dynamic_FieldGrant\"", 1, "守衛表名 dynamic_fieldgrant" },
        { "兩個陳述式串在一起", 1, "守衛表名 Dynamic_UserGroupMapping" },
        { "未登記檔案用 Dapper ExecuteAsync", 1, "未登記的原生 SQL 檔案" },
        { "登記檔案內經變數取得 Set<FieldGrant> 再批次刪", 1, "追到守衛型別 Set<FieldGrant>" },
        { "登記檔案內經屬性取得 Set<FieldGrant> 再批次刪", 1, "追到守衛型別 Set<FieldGrant>" },
        { "跨檔傳遞查詢", 2, "IQueryable<FieldGrant>" },
        { "接收者是方法參數 IQueryable<T>", 1, "（q）追不到" },
        { "未登記的方法對非守衛型別批次刪", 1, "未登記的批次方法 WipeAsync" },
        { "migration 的 UpdateData", 1, "UpdateData 寫 Dynamic_Field" },
        { "migration 的 DeleteData 表名是變數", 1, "DeleteData 的表名要是字串常值" },
        { "migration 的 InsertData 位置參數表名是變數", 1, "InsertData 的表名要是字串常值" },
        { "migration 的 Sql 是插值字串", 1, "Sql 要是不含插值的字串常值" },
        { "migration 的 Sql 串接變數", 1, "Sql 要是不含插值的字串常值" },
        { "migration 的 Sql 串接常值拆開表名", 1, "Sql 寫 Dynamic_FieldPermission" },
        { "migration 直接加 SqlOperation", 1, "SqlOperation" },
        { "migration 的 DeleteData 表名用 \\u 跳脫", 1, "反斜線跳脫" },
        { "migration 的 Sql 用 \\u、\\x 跳脫", 2, "反斜線跳脫" },
        { "另加：未登記的開許可位置", 1, "未登記的開許可位置" },
        { "另加：src 使用測試許可", 1, "不得使用測試許可" },
        { "另加：型別參數不具體的 Set<T>()", 1, "Set<T> 的型別參數不是具體" },
        { "另加：守衛批次入口沒先 Demand", 1, "Demand(AuthorizationBatchEntry.ReplaceModuleRows)" },
        { "另加：批次指令在成員段落之外", 1, "成員段落之外" },
        { "另加：Application 引用 DbContext", 1, "Application 不得引用 DynamicDbContext" },
    };

    private static ScanSource[] Sample(string name) => name switch
    {
        "UserGroupMapping 的 ExecuteDeleteAsync" =>
        [
            Cs(AccountRepo, """"
                public class AccountRepository(DynamicDbContext db)
                {
                    public Task<int> PurgeMembersAsync(long groupId, CancellationToken ct) =>
                        db.Set<UserGroupMapping>().Where(m => m.GroupId == groupId).ExecuteDeleteAsync(ct);
                }
                """"),
        ],
        "Set<Field> 批次改 ExcludeFromAi" =>
        [
            Cs(SampleRepo, """"
                public class SampleRepository(DynamicDbContext db)
                {
                    public Task<int> HideAsync(long moduleId, CancellationToken ct) =>
                        db.Set<Field>().Where(f => f.ModuleId == moduleId).ExecuteUpdateAsync(s => s.SetProperty(f => f.ExcludeFromAi, false), ct);
                }
                """"),
        ],
        "經 Set<Module> 的導覽屬性改 VisibilityRestricted" =>
        [
            Cs(SampleRepo, """"
                public class SampleRepository(DynamicDbContext db)
                {
                    public Task<int> HideAsync(long moduleId, CancellationToken ct) =>
                        db.Set<Module>().Where(m => m.Id == moduleId).SelectMany(m => m.Blocks).SelectMany(b => b.Fields)
                            .ExecuteUpdateAsync(s => s.SetProperty(f => f.VisibilityRestricted, false), ct);
                }
                """"),
        ],
        "DELETE FROM public.\"Dynamic_FieldPermission\"" =>
        [
            Cs("DynamicModules.Persistence/Maintenance.cs", """"
                public static class Maintenance
                {
                    public const string Wipe = "DELETE FROM public.\"Dynamic_FieldPermission\"";
                }
                """"),
        ],
        "WITH x AS (…) DELETE FROM \"Dynamic_FieldGrant\"" =>
        [
            Cs("DynamicModules.Persistence/Maintenance.cs", """"
                public static class Maintenance
                {
                    public static string Wipe(long id) => @"WITH x AS (SELECT ""Id"" FROM ""Dynamic_Record"") DELETE FROM ""dynamic_fieldgrant""";
                }
                """"),
        ],
        "兩個陳述式串在一起" =>
        [
            Cs("DynamicModules.Persistence/Maintenance.cs", """"
                public static class Maintenance
                {
                    public static string Wipe(string table) => $"UPDATE {table} SET \"OwnerUserId\" = 'admin'; DELETE FROM \"Dynamic_UserGroupMapping\"";
                }
                """"),
        ],
        "未登記檔案用 Dapper ExecuteAsync" =>
        [
            Cs("DynamicModules.Persistence/OwnerFixer.cs", """"
                using Dapper;

                public class OwnerFixer(DynamicDbContext db)
                {
                    public Task<int> FixAsync(string user) =>
                        db.Database.GetDbConnection().ExecuteAsync("UPDATE \"Dynamic_Record\" SET \"OwnerUserId\" = @user", new { user });
                }
                """"),
        ],
        "登記檔案內經變數取得 Set<FieldGrant> 再批次刪" =>
        [
            Cs(AccountRepo, """"
                public class AccountRepository(DynamicDbContext db)
                {
                    public async Task PurgeGrantsAsync(long fieldId, CancellationToken ct)
                    {
                        var grants = db.Set<FieldGrant>();
                        await grants.Where(g => g.FieldId == fieldId).ExecuteDeleteAsync(ct);
                    }
                }
                """"),
        ],
        "登記檔案內經屬性取得 Set<FieldGrant> 再批次刪" =>
        [
            Cs(AccountRepo, """"
                public class AccountRepository(DynamicDbContext db)
                {
                    private IQueryable Grants => db.Set<FieldGrant>();

                    public Task<int> PurgeGrantsViaPropertyAsync(CancellationToken ct) => Grants.Cast<FieldGrant>().ExecuteDeleteAsync(ct);
                }
                """"),
        ],
        "跨檔傳遞查詢" =>
        [
            Cs(PermissionRepo, """"
                public class PermissionRepository(DynamicDbContext db)
                {
                    public IQueryable<FieldGrant> GrantsOf(long fieldId) => db.Set<FieldGrant>().Where(g => g.FieldId == fieldId);
                }
                """"),
            Cs(Purger, """"
                public class GrantPurger(IPermissionRepository repo)
                {
                    public Task<int> PurgeAsync(long fieldId, CancellationToken ct) => repo.GrantsOf(fieldId).ExecuteDeleteAsync(ct);
                }
                """"),
        ],
        "接收者是方法參數 IQueryable<T>" =>
        [
            Cs(GenericPurge, """"
                public static class GenericPurge
                {
                    public static Task<int> PurgeAsync<T>(IQueryable<T> q, CancellationToken ct) where T : class => q.ExecuteDeleteAsync(ct);
                }
                """"),
        ],
        "未登記的方法對非守衛型別批次刪" =>
        [
            Cs(SampleRepo, """"
                public class SampleRepository(DynamicDbContext db)
                {
                    public Task<int> WipeAsync(long moduleId, CancellationToken ct) =>
                        db.Set<Record>().Where(r => r.ModuleId == moduleId).ExecuteDeleteAsync(ct);
                }
                """"),
        ],
        "migration 的 UpdateData" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_RelaxVisibility.cs", """"
                public partial class RelaxVisibility : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.UpdateData(table: "Dynamic_Field", keyColumn: "Id", keyValue: 1L, column: "VisibilityRestricted", value: false);
                    }
                }
                """"),
        ],
        "migration 的 DeleteData 表名是變數" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    private const string table = "Dynamic_FieldPermission";

                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.DeleteData(table: table, keyColumn: "FieldId", keyValue: 1L);
                    }
                }
                """"),
        ],
        "migration 的 InsertData 位置參數表名是變數" =>
        [
            Cs("DynamicModules.Persistence.Postgres/Migrations/20261120000000_AddRows.cs", """"
                public partial class AddRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        var t = Tables.Grant;
                        migrationBuilder.InsertData(t, new[] { "FieldId", "GroupId" }, new object[] { 1L, 2L });
                    }
                }
                """"),
        ],
        "migration 的 Sql 是插值字串" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        const string table = "Dynamic_FieldPermission";
                        migrationBuilder.Sql($"DELETE FROM \"{table}\"");
                    }
                }
                """"),
        ],
        "migration 的 Sql 串接變數" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        var table = Names.Permission;
                        migrationBuilder.Sql("DELETE FROM \"" + table + "\"");
                    }
                }
                """"),
        ],
        "migration 的 Sql 串接常值拆開表名" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Sql("DELETE FROM \"Dynamic_" + "FieldPermission\"");
                    }
                }
                """"),
        ],
        "migration 直接加 SqlOperation" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Operations.Add(new SqlOperation { Sql = Names.Wipe });
                    }
                }
                """"),
        ],
        "migration 的 DeleteData 表名用 \\u 跳脫" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.DeleteData(table: "Dynamic_\u0046ieldPermission", keyColumn: "FieldId", keyValue: 1L);
                    }
                }
                """"),
        ],
        "migration 的 Sql 用 \\u、\\x 跳脫" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_DropRows.cs", """"
                public partial class DropRows : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Sql("DELETE FROM \"Dynamic_\u0046ieldPermission\"");
                        migrationBuilder.Sql("DELETE FROM \"Dynamic_" + "\x46ieldGrant\"");
                    }
                }
                """"),
        ],
        "另加：未登記的開許可位置" =>
        [
            Cs("DynamicModules.Application/Dynamic/SampleService/Shortcut.cs", """"
                public class Shortcut
                {
                    public Task RunAsync(Func<Task> body) =>
                        Domain.Authorization.AuthorizationWriteScope.RunAsync(Domain.Authorization.AuthorizationPermit.Authorized(true), () => false, body);
                }
                """"),
        ],
        "另加：src 使用測試許可" =>
        [
            Cs("DynamicModules.Persistence/Seed/Shortcut.cs", """"
                public static class Shortcut
                {
                    public static Task RunAsync(Func<Task> body) => TestPermits.RunForTestsAsync(body);
                }
                """"),
        ],
        "另加：型別參數不具體的 Set<T>()" =>
        [
            Cs("DynamicModules.Persistence/Repositories/Dynamic/AnyRepository.cs", """"
                public class AnyRepository(DynamicDbContext db)
                {
                    public Task<List<T>> AllAsync<T>(CancellationToken ct) where T : class => db.Set<T>().ToListAsync(ct);
                }
                """"),
        ],
        "另加：守衛批次入口沒先 Demand" =>
        [
            Cs(PermissionRepo, """"
                public class PermissionRepository(DynamicDbContext db)
                {
                    public async Task ReplaceModuleRowsAsync(Module module, ModulePermissionRows rows, CancellationToken ct)
                    {
                        await db.Set<GroupProgramPrivilege>().Where(p => p.ProgramCode == module.ProgramCode).ExecuteDeleteAsync(ct);
                        AuthorizationWriteScope.Demand(AuthorizationBatchEntry.ReplaceModuleRows);
                    }
                }
                """"),
        ],
        "另加：批次指令在成員段落之外" =>
        [
            Cs(SampleRepo, """"
                public class SampleRepository(DynamicDbContext db)
                {
                    public Task<int> HideAsync { get; } = db.Set<Record>().ExecuteDeleteAsync();
                }
                """"),
        ],
        "另加：Application 引用 DbContext" =>
        [
            Cs("DynamicModules.Application/Dynamic/SampleService/Direct.cs", """"
                public class Direct(DynamicDbContext db)
                {
                    public int Count() => db.Set<Record>().Count();
                }
                """"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public void PAX23j_EachViolationSample_IsReported(string name, int expected, string fragment)
    {
        var sample = Sample(name);
        var violations = AuthorizationWriteScanner.Scan(sample, Registry).Violations;
        violations.Should().HaveCount(expected, string.Join("\n", violations));
        violations.Should().OnlyContain(v => sample.Any(s => v.StartsWith(s.Path + ":", StringComparison.Ordinal)));
        violations.Should().Contain(v => v.Contains(fragment), string.Join("\n", violations));
    }

    public static TheoryData<string> Legal() => new()
    {
        "SharedMasterConfigurations 的 ToTable(\"Dynamic_FieldGrant\")",
        "AuthorizationTables 的常數",
        "migration 的 CreateTable(name: \"Dynamic_FieldGrant\")",
        "migration 對非守衛表的常值 Sql（含串接、verbatim、原始字串）與 InsertData／DeleteData",
        ".Designer.cs 與 ModelSnapshot 的 b.ToTable(\"Dynamic_FieldGrant\")",
        "登記的非守衛批次方法對 Set<Record>()（含子查詢）批次更新",
        "同檔案私有屬性 Incidents => db.Set<OpsIncident>() 再 ExecuteUpdateAsync",
        "另加：註解與插值、原始字串裡的字樣不算",
    };

    private static ScanSource[] LegalSample(string name) => name switch
    {
        "SharedMasterConfigurations 的 ToTable(\"Dynamic_FieldGrant\")" =>
        [
            Cs("DynamicModules.Domain/Data/Configurations/SharedMasterConfigurations.cs", """"
                public class FieldGrantConfiguration : IEntityTypeConfiguration<FieldGrant>
                {
                    public void Configure(EntityTypeBuilder<FieldGrant> b)
                    {
                        b.ToTable("Dynamic_FieldGrant", t => t.HasCheckConstraint("CK_FieldGrant_Source", "(\"SourceKind\" = 'Manual') = (\"AppId\" IS NULL)"));
                    }
                }
                """"),
        ],
        "AuthorizationTables 的常數" =>
        [
            Cs("DynamicModules.Domain/Authorization/AuthorizationTables.cs", """"
                public static class AuthorizationTables
                {
                    public const string FieldGrant = "Dynamic_FieldGrant";
                    public const string UserGroupMapping = "Dynamic_UserGroupMapping";
                    public const string Field = "Dynamic_Field";
                }
                """"),
        ],
        "migration 的 CreateTable(name: \"Dynamic_FieldGrant\")" =>
        [
            Cs("DynamicModules.Persistence.Postgres/Migrations/20261120000000_SharedMasters.cs", """"
                public partial class SharedMasters : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.CreateTable(
                            name: "Dynamic_FieldGrant",
                            columns: table => new { Id = table.Column<long>(type: "bigint", nullable: false) },
                            constraints: table => table.PrimaryKey("PK_Dynamic_FieldGrant", x => x.Id));
                        migrationBuilder.CreateIndex(name: "IX_Dynamic_FieldGrant_FieldId", table: "Dynamic_FieldGrant", column: "FieldId");
                    }
                }
                """"),
        ],
        "migration 對非守衛表的常值 Sql（含串接、verbatim、原始字串）與 InsertData／DeleteData" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_MoveMail.cs", """"
                public partial class MoveMail : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder)
                    {
                        migrationBuilder.Sql(
                            "INSERT INTO \"Dynamic_MailSettings\" (\"Kind\", \"Host\") " +
                            "SELECT 'default', \"Host\" FROM \"Dynamic_AlertChannel\" WHERE \"Kind\" = 'smtp';");
                        migrationBuilder.Sql(@"UPDATE ""Dynamic_Module"" SET ""Version"" = 1", suppressTransaction: true);
                        migrationBuilder.Sql(sql: """
                            DELETE FROM "Dynamic_AlertChannel" WHERE "Kind" = 'old';
                            """);
                        migrationBuilder.InsertData(table: "Dynamic_MailSettings", columns: new[] { "Kind", "Port" }, values: new object[] { "default", 587 });
                        migrationBuilder.DeleteData("Dynamic_AlertChannel", "Kind", "smtp");
                        migrationBuilder.UpdateData(keyColumn: "Kind", keyValue: "default", column: "Port", value: 25, table: "Dynamic_MailSettings");
                    }
                }
                """"),
        ],
        ".Designer.cs 與 ModelSnapshot 的 b.ToTable(\"Dynamic_FieldGrant\")" =>
        [
            Cs("DynamicModules.Persistence/Migrations/20261120000000_SharedMasters.Designer.cs", """"
                partial class SharedMasters
                {
                    protected override void BuildTargetModel(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity("DynamicModules.Domain.Entities.Dynamic.FieldGrant", b => { b.ToTable("Dynamic_FieldGrant"); });
                    }
                }
                """"),
            Cs("DynamicModules.Persistence/Migrations/DynamicDbContextModelSnapshot.cs", """"
                partial class DynamicDbContextModelSnapshot : ModelSnapshot
                {
                    protected override void BuildModel(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity("DynamicModules.Domain.Entities.Dynamic.FieldGrant", b => { b.ToTable("Dynamic_FieldGrant"); });
                    }
                }
                """"),
        ],
        "登記的非守衛批次方法對 Set<Record>()（含子查詢）批次更新" =>
        [
            Cs(SampleRepo, """"
                public class SampleRepository(DynamicDbContext db)
                {
                    public Task<int> AdoptAnonymousAsync(CancellationToken ct) =>
                        db.Set<Record>().Where(r => !db.Set<AppUser>().Any(u => u.UserId == r.OwnerUserId))
                            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerUserId, "admin"), ct);
                }
                """"),
        ],
        "同檔案私有屬性 Incidents => db.Set<OpsIncident>() 再 ExecuteUpdateAsync" =>
        [
            Cs(OpsRepo, """"
                public class OpsIncidentRepository(DynamicDbContext db)
                {
                    private IQueryable<OpsIncident> Incidents => db.Set<OpsIncident>();

                    public Task MarkNotifiedAsync(long id, DateTime atUtc, CancellationToken ct) =>
                        Incidents.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastNotifiedUtc, atUtc), ct);
                }
                """"),
        ],
        "另加：註解與插值、原始字串裡的字樣不算" =>
        [
            Cs("DynamicModules.Application/Dynamic/SampleService/Notes.cs", """"
                /// <summary>說明 Dynamic_FieldGrant 與 Dynamic_UserGroupMapping；ExecuteDeleteAsync 只在註解裡；Set&lt;FieldGrant&gt;。</summary>
                public static class Notes
                {
                    // db.Set<UserGroupMapping>().ExecuteDeleteAsync(); DbContext
                    /* AuthorizationWriteScope.RunAsync(AuthorizationPermit.Seed(), ...) */
                    public static string Html(string title, int n) => $$"""
                        <style>body { margin: 0 }</style><h1>{{title}}</h1><p>{{(n > 1 ? "items" : "item")}}</p>
                        """;
                    public static string Escaped(string x) => $"{{literal}} {x} \" '{x}'" + @"C:\path""quoted""" + '"' + '\'';
                }
                """"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Legal))]
    public void PAX23j_EachLegalSample_ReportsNothing(string name)
    {
        var violations = AuthorizationWriteScanner.Scan(LegalSample(name), Registry).Violations;
        violations.Should().BeEmpty(string.Join("\n", violations));
    }
}
