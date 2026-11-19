using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic.PermissionAxes;

/// <summary>
/// 權限軸驗收的共用 fixture（權限軸設計 §18 的 persona 與模組表，測試資料全部虛構）：
/// <list type="bullet">
/// <item>admin：系統管理員、沒有任何系統權限列。</item>
/// <item>wang（小王）：G-design（DYN00000 讀新改刪；A、T、H、D 讀新改、沒有金額；M 沒有列）。</item>
/// <item>li（小李）：G-perm（SYS00001；沒有 DYN00000、A 沒有列）。</item>
/// <item>chen（小陳）：G-both（DYN00000 讀改、SYS00001；A、T 讀改）。</item>
/// <item>zhang（小張）：個人 DYN00000 讀改。</item>
/// <item>zhou（小周）：已停用、在 G-both。lin（老林）：已停用的系統管理員、在 G-both。</item>
/// </list>
/// 模組 A（單價 Currency、薪資 Decimal 對 G-design 隱藏、F1 = {薪資} * 1、F2 = {F1} + 0）、M、T（N 小數、P 金額）、
/// H 表頭（薪資對 G-design 隱藏、單價、F1）與 D 明細（X、Y 帶表頭的薪資、單價；§18 的 Z 帶 F1 建不出來）；S 用內建共用主檔往來對象。
/// 系統權限列與成員直接在測試許可內寫（不經 API，不產生稽核與安全事件）；模組與模組權限由 admin 經 API 建。
/// </summary>
internal sealed class PaxWorld
{
    public const string Admin = "admin", Wang = "wang", Li = "li", Chen = "chen", Zhang = "zhang", Zhou = "zhou", Lin = "lin";
    public const string A = "pax-a", M = "pax-m", T = "pax-t", H = "pax-h", D = "pax-d", S = BuiltInMasterTests.Partner;
    public const string GDesignKey = "pax-g-design", GPermKey = "pax-g-perm", GBothKey = "pax-g-both";

    public required TestApp App { get; init; }
    public long GDesign { get; private set; }
    public long GPerm { get; private set; }
    public long GBoth { get; private set; }

    /// <summary>欄位 id：A.單價、A.薪資、A.F1、A.F2、T.N、T.P、H.薪資、H.單價、H.F1、D.X、D.Y、D.表頭參照（D.Z 建不出來，見 SeedModulesAsync）。</summary>
    public long APrice, ASalary, AF1, AF2, TN, TP, HSalary, HPrice, HF1, DX, DY, DRef;

    public HttpClient As(string user) => App.AsUser(user);

    public static string F(long id) => $"f_{id}";

    public static async Task<PaxWorld> CreateAsync(TestApp app, bool modules = true)
    {
        var w = new PaxWorld { App = app };
        w.SeedPeople();
        if (modules) await w.SeedModulesAsync();
        return w;
    }

    private void SeedPeople()
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<AppUser>().AddRange(
            new AppUser { UserId = Wang, DisplayName = "小王" },
            new AppUser { UserId = Li, DisplayName = "小李" },
            new AppUser { UserId = Chen, DisplayName = "小陳" },
            new AppUser { UserId = Zhang, DisplayName = "小張" },
            new AppUser { UserId = Zhou, DisplayName = "小周", IsActive = false },
            new AppUser { UserId = Lin, DisplayName = "老林", IsSystemAdmin = true, IsActive = false });
        var design = Group(GDesignKey, "設計組");
        var perm = Group(GPermKey, "授權組");
        var both = Group(GBothKey, "設計授權組");
        db.Set<PermissionGroup>().AddRange(design, perm, both);
        db.SaveChanges();
        (GDesign, GPerm, GBoth) = (design.Id, perm.Id, both.Id);

        db.Set<UserGroupMapping>().AddRange(
            new UserGroupMapping { GroupId = GDesign, UserId = Wang },
            new UserGroupMapping { GroupId = GPerm, UserId = Li },
            new UserGroupMapping { GroupId = GBoth, UserId = Chen },
            new UserGroupMapping { GroupId = GBoth, UserId = Zhou },
            new UserGroupMapping { GroupId = GBoth, UserId = Lin });
        db.Set<GroupProgramPrivilege>().AddRange(
            new GroupProgramPrivilege { GroupId = GDesign, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true },
            new GroupProgramPrivilege { GroupId = GPerm, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true },
            new GroupProgramPrivilege { GroupId = GBoth, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true },
            new GroupProgramPrivilege { GroupId = GBoth, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true });
        db.Set<UserProgramPrivilege>().Add(
            new UserProgramPrivilege { UserId = Zhang, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanEdit = true });
        db.SaveForTests();
    }

    public static PermissionGroup Group(string key, string name) =>
        new() { Key = key, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", name)]) };

    private async Task SeedModulesAsync()
    {
        var admin = As(Admin);
        await NewModule(admin, A);
        APrice = await Field(admin, A, "currency", "單價");
        ASalary = await Field(admin, A, "decimal", "薪資");
        AF1 = await Field(admin, A, "formula", "F1", new { formula = $"{{{ASalary}}} * 1", resultType = "number" });
        AF2 = await Field(admin, A, "formula", "F2", new { formula = $"{{{AF1}}} + 0", resultType = "number" });

        await NewModule(admin, M);

        await NewModule(admin, T);
        TN = await Field(admin, T, "decimal", "N");
        TP = await Field(admin, T, "currency", "P");

        await NewModule(admin, H);
        HSalary = await Field(admin, H, "decimal", "薪資");
        HPrice = await Field(admin, H, "currency", "單價");
        HF1 = await Field(admin, H, "formula", "F1", new { formula = $"{{{HSalary}}} * 1", resultType = "number" });

        await NewModule(admin, D);
        DRef = await Field(admin, D, "reference", "表頭", new { refTarget = new { kind = "module", key = H }, relation = "detail" });
        DX = await Field(admin, D, "decimal", "X", new { parentDefaultFieldId = HSalary });
        DY = await Field(admin, D, "currency", "Y", new { parentDefaultFieldId = HPrice });
        // §18 的 D.Z（帶表頭的 H.F1）建不出來：帶表頭的值要求來源與目的同型別、且型別在 ParentDefaultTypes 內，公式不在（FieldRules），
        // 所以不建 Z；公式被過濾的情形由 DetailWriter 單元測試（ApplicationTests PAX-14）以表頭權限的 HiddenFieldIds 涵蓋

        foreach (var key in new[] { A, M, T, H, D }) await Activate(admin, key);

        var rae = Priv(read: true, add: true, edit: true);
        var re = Priv(read: true, edit: true);
        await PutOk(admin, A, new
        {
            groups = new[] { Grp(GDesign, rae), Grp(GBoth, re) },
            fields = new[] { new { fieldId = ASalary, groupId = GDesign, access = "hidden" } },
        });
        await PutOk(admin, T, new { groups = new[] { Grp(GDesign, rae), Grp(GBoth, re) } });
        await PutOk(admin, H, new
        {
            groups = new[] { Grp(GDesign, rae) },
            fields = new[] { new { fieldId = HSalary, groupId = GDesign, access = "hidden" } },
        });
        // D 是明細模組：群組權限沿用表頭 H，不能另存（§18 表格寫的「D 讀新改」由 H 的列提供）
    }

    public static object Priv(bool read = false, bool add = false, bool edit = false, bool delete = false, bool pricing = false) =>
        new { read, add, edit, delete, printExport = false, pricing, approve = false };

    public static object Grp(long groupId, object privileges) => new { groupId, privileges, scope = "all" };

    private static async Task PutOk(HttpClient admin, string module, object body)
    {
        var res = await admin.PutAsJsonAsync($"{Base}/modules/{module}/permissions", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private static async Task NewModule(HttpClient admin, string key)
    {
        var res = await admin.PostAsJsonAsync($"{Base}/modules", new { key, name = new Dictionary<string, string> { ["zh-TW"] = key } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    public static async Task<long> Field(HttpClient c, string module, string type, string label, object? config = null)
    {
        var m = await JsonOf(await c.GetAsync($"{Base}/modules/{module}"));
        var blockId = m.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var res = await c.PostAsJsonAsync($"{Base}/modules/{module}/fields", new
        {
            blockId, type, label = new Dictionary<string, string> { ["zh-TW"] = label }, config = config ?? new { },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, $"{module}.{label}：{await res.Content.ReadAsStringAsync()}");
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    public T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = App.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    public void Write(Action<DynamicDbContext> write)
    {
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        write(db);
        db.SaveForTests();
    }

    /// <summary>全部守衛表逐列的文字快照（排序後），用來斷言「權限表逐列不變」。</summary>
    public List<string> GuardRows() => Db(PaxGuard.Rows);
}

internal static class PaxGuard
{
    public static List<string> Rows(DynamicDbContext db) =>
    [
        .. Of<GroupProgramPrivilege>(db), .. Of<UserProgramPrivilege>(db), .. Of<FieldPermission>(db), .. Of<ModuleDataScope>(db),
        .. Of<FieldOptionScope>(db), .. Of<FieldGrant>(db), .. Of<AppGrant>(db), .. Of<ModuleActionScope>(db), .. Of<UserGroupMapping>(db),
    ];

    private static IEnumerable<string> Of<T>(DynamicDbContext db) where T : class
    {
        var props = db.Model.FindEntityType(typeof(T))!.GetProperties().Where(p => p.PropertyInfo is not null).ToList();
        return db.Set<T>().AsNoTracking().ToList()
            .Select(r => typeof(T).Name + ":" + string.Join("|", props.Select(p => $"{p.Name}={p.PropertyInfo!.GetValue(r)}")))
            .Order(StringComparer.Ordinal).ToList();
    }
}

/// <summary>錯誤回應的斷言：HTTP 狀態、code（沒有 code 的斷言沒有 code 鍵）、指定語系的 message。</summary>
internal static class PaxAssert
{
    public static async Task<JsonElement> Forbidden(HttpResponseMessage res, string? code)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, text);
        var body = JsonDocument.Parse(text).RootElement.Clone();
        if (code is null) body.TryGetProperty("code", out _).Should().BeFalse($"不應有 code：{text}");
        else body.GetProperty("code").GetString().Should().Be(code, text);
        return body;
    }

    public static async Task Status(HttpResponseMessage res, HttpStatusCode expected) =>
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());

    public static HttpRequestMessage Req(HttpMethod method, string url, object? body, string locale)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Locale", locale);
        return req;
    }
}
