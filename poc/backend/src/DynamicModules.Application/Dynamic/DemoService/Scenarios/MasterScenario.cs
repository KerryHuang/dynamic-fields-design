using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 共用主檔的虛構資料（共用主檔設計 §11.5）：往來對象與聯絡人（含任職經歷），排在使用它們的情境之前，其他情境從
/// <see cref="DemoPlan.Records"/> 的 <see cref="PartnerModule"/>、<see cref="ContactModule"/> 引用（用 <see cref="ForApp"/> 挑自己的那幾家）。
/// 只建立當下已開啟、而且已改用主檔的應用程式需要的公司與角色（<see cref="Needs"/>）；沒有任何應用程式需要時什麼都不建。
/// 公司、人名、電話、Email 全是虛構（Email 一律 example.com），公司名稱沿用各情境原本的虛構名稱。
/// </summary>
public sealed class MasterScenario(IReadOnlyCollection<string> openApps) : IDemoScenario
{
    public const string PartnerModule = "partner", ContactModule = "contact";
    public const string PartnerSymbol = "partner/partner", ContactSymbol = "contact/contact", EmploymentSymbol = "contact/employment";

    public string Key => "master";
    public int Seed => 20261110;

    /// <summary>
    /// 已改用主檔的應用程式：要不要建聯絡人、由誰建立往來對象、負責人是誰、從今天前幾天開始建（要早於該應用程式最早引用它的單據）。
    /// </summary>
    private static readonly Dictionary<string, (bool Contacts, string[] Creators, string[] Owners, int FromDay)> Needs = new()
    {
        [DemoCatalog.CrmAppId] = (true, ["sales01", "sales02"], ["sales01", "sales02"], -80),
        [DemoCatalog.MesAppId] = (false, ["manager01"], ["sales01", "sales02"], -95)
    };

    /// <summary>
    /// 公司：每家屬於原本用它的應用程式（只有該應用程式開啟時才建）。CRM 的十家客戶（其中一家也是供應商）、MES 的八家客戶。
    /// </summary>
    private static readonly (string Name, string Role, string App)[] Companies =
    [
        ("晨曦機械", "customer", DemoCatalog.CrmAppId), ("遠見電子", "customer", DemoCatalog.CrmAppId), ("新航物流", "customer", DemoCatalog.CrmAppId),
        ("綠野食品", "customer", DemoCatalog.CrmAppId), ("恆展工業", "both", DemoCatalog.CrmAppId), ("藍海貿易", "customer", DemoCatalog.CrmAppId),
        ("巨峰營造", "customer", DemoCatalog.CrmAppId), ("明日設計", "customer", DemoCatalog.CrmAppId), ("安泰五金", "customer", DemoCatalog.CrmAppId),
        ("雲端製造", "customer", DemoCatalog.CrmAppId),
        ("嵐岳精密", "customer", DemoCatalog.MesAppId), ("恆川機械", "customer", DemoCatalog.MesAppId), ("曜陽電子", "customer", DemoCatalog.MesAppId),
        ("岳峰精機", "customer", DemoCatalog.MesAppId), ("澄川塑膠", "customer", DemoCatalog.MesAppId), ("瑞嶺零件", "customer", DemoCatalog.MesAppId),
        ("拓明模具", "customer", DemoCatalog.MesAppId), ("禾晟電機", "customer", DemoCatalog.MesAppId)
    ];

    private static readonly string[] Surnames = ["林", "陳", "王", "張", "李", "黃", "吳", "劉", "蔡", "楊"];
    private static readonly string[] GivenNames = ["怡君", "家豪", "雅婷", "志明", "淑芬", "建宏", "佩珊", "俊傑", "美玲", "宗翰"];
    private static readonly string[] Titles = ["採購經理", "工廠廠長", "資訊主管", "業務窗口", "財務主管"];

    /// <summary>換過公司的 CRM 聯絡人：聯絡人序號 → 前一家公司的序號（任職經歷兩筆以上）。第 7 位待過兩家。</summary>
    private static readonly Dictionary<int, int[]> FormerCompanies = new() { [2] = [5], [4] = [8], [7] = [0, 3] };

    /// <summary>某個應用程式的往來對象（依 <see cref="Companies"/> 的順序）；那個應用程式沒開時是空的。</summary>
    public static List<DemoRecord> ForApp(DemoPlan plan, string appId) =>
        plan.Records.GetValueOrDefault(PartnerModule, []).Where(p => (string?)p.Facts.GetValueOrDefault("app") == appId).ToList();

    public void Plan(DemoPlan plan)
    {
        var apps = openApps.Where(Needs.ContainsKey).ToHashSet();
        if (apps.Count == 0) return;
        var today = plan.Today;

        // ---- 往來對象 ----
        var partners = new Dictionary<int, DemoRecord>();
        var perApp = new Dictionary<string, int>();
        for (var i = 0; i < Companies.Length; i++)
        {
            var (name, role, app) = Companies[i];
            if (!apps.Contains(app)) continue;
            var k = perApp[app] = perApp.GetValueOrDefault(app) + 1;
            var (_, creators, owners, fromDay) = Needs[app];
            var n = i;
            var owner = owners[(k - 1) % owners.Length];
            var record = plan.Create(PartnerModule, plan.At(today.AddDays(fromDay + k - 1)), creators[(k - 1) % creators.Length], $"往來對象 {name} 建立", ctx =>
            {
                var m = ctx.Masters!;
                var values = m.Values(PartnerSymbol,
                    ("name", name), ("role", m.Opt(PartnerSymbol, role)), ("taxId", $"{90000000 + n * 1371:00000000}"),
                    ("phone", $"02-5550-{1000 + n * 37:0000}"), ("email", $"service{n + 1}@example.com"),
                    ("address", $"示範市中山路 {100 + n * 8} 號"), ("owner", owner), ("active", true));
                if (role != "supplier") values[m.Key($"{PartnerSymbol}.salesPaymentTerms")] = Json(n % 2 == 0 ? "月結 30 天" : "月結 60 天");
                if (role != "customer") values[m.Key($"{PartnerSymbol}.purchasePaymentTerms")] = Json(n % 2 == 0 ? "月結 30 天" : "月結 60 天");
                return new CreateRecordRequest(m.Ids.ModuleKeys[PartnerSymbol], values);
            });
            record.Facts["name"] = name;
            record.Facts["role"] = role;
            record.Facts["app"] = app;
            partners[i] = record;
        }

        // ---- CRM 的聯絡人：掛在 CRM 的客戶公司下；幾位換過公司，任職經歷有兩筆以上 ----
        if (!apps.Contains(DemoCatalog.CrmAppId) || !Needs[DemoCatalog.CrmAppId].Contacts) return;
        var users = Needs[DemoCatalog.CrmAppId].Creators;
        var companies = partners.Keys.Where(k => Companies[k].App == DemoCatalog.CrmAppId).Take(Surnames.Length).ToList();
        for (var c = 0; c < companies.Count; c++)
        {
            var n = c;
            var current = partners[companies[c]];
            var former = (FormerCompanies.GetValueOrDefault(c) ?? []).Where(k => partners.ContainsKey(k)).Select(k => partners[k]).ToList();
            var at = plan.At(today.AddDays(-66 + c));
            var name = $"{Surnames[c]}{GivenNames[c]}";
            var record = plan.Create(ContactModule, at, users[c % users.Length], $"聯絡人 {name} 建立", ctx =>
            {
                var m = ctx.Masters!;
                var rows = new List<DetailRowInput>();
                var start = today.AddYears(-2 - former.Count);
                foreach (var previous in former)
                {
                    rows.Add(new DetailRowInput(null, m.Values(EmploymentSymbol, ("partner", previous.Id), ("title", "業務專員"),
                        ("startDate", Iso(start)), ("endDate", Iso(start.AddYears(1).AddDays(-1)))), null));
                    start = start.AddYears(1);
                }
                rows.Add(new DetailRowInput(null, m.Values(EmploymentSymbol, ("partner", current.Id), ("title", Titles[n % Titles.Length]),
                    ("startDate", Iso(start))), null));
                return new CreateRecordRequest(m.Ids.ModuleKeys[ContactSymbol], m.Values(ContactSymbol,
                        ("name", name), ("partner", current.Id), ("title", Titles[n % Titles.Length]),
                        ("phone", $"0912-555-{100 + n * 7:000}"), ("email", $"contact{n + 1}@example.com")),
                    Details: new Dictionary<string, List<DetailRowInput>> { [m.Ids.ModuleKeys[EmploymentSymbol]] = rows });
            });
            record.Facts["name"] = name;
            record.Facts["partner"] = current;
            record.Facts["employment"] = former.Count + 1;
        }
    }

    private static System.Text.Json.JsonElement? Json(string value) => System.Text.Json.JsonSerializer.SerializeToElement(value);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
