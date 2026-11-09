using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 共用主檔的虛構資料（共用主檔設計 §11.5）：往來對象與聯絡人（含任職經歷），排在使用它們的情境之前，其他情境從
/// <see cref="DemoPlan.Records"/> 的 <see cref="PartnerModule"/>、<see cref="ContactModule"/> 引用。
/// 只建立當下已開啟、而且已改用主檔的應用程式需要的角色（<see cref="Needs"/>）；沒有任何應用程式需要時什麼都不建。
/// 公司、人名、電話、Email 全是虛構（Email 一律 example.com），公司名稱沿用各情境原本的虛構名稱。
/// </summary>
public sealed class MasterScenario(IReadOnlyCollection<string> openApps) : IDemoScenario
{
    public const string PartnerModule = "partner", ContactModule = "contact";
    public const string PartnerSymbol = "partner/partner", ContactSymbol = "contact/contact", EmploymentSymbol = "contact/employment";

    public string Key => "master";
    public int Seed => 20261110;

    /// <summary>已改用主檔的應用程式需要的往來對象角色、要不要聯絡人，以及由誰建立。MES、進銷存改用主檔後再加進來。</summary>
    private static readonly Dictionary<string, (string[] Roles, bool Contacts, string[] Users)> Needs = new()
    {
        [DemoCatalog.CrmAppId] = (["customer"], true, ["sales01", "sales02"])
    };

    /// <summary>公司：CRM 的十家客戶（其中一家也是供應商），加上進銷存的三家供應商。</summary>
    private static readonly (string Name, string Role)[] Companies =
    [
        ("晨曦機械", "customer"), ("遠見電子", "customer"), ("新航物流", "customer"), ("綠野食品", "customer"), ("恆展工業", "both"),
        ("藍海貿易", "customer"), ("巨峰營造", "customer"), ("明日設計", "customer"), ("安泰五金", "customer"), ("雲端製造", "customer"),
        ("昌盛鋼鐵", "supplier"), ("宏達五金", "supplier"), ("精工模料", "supplier")
    ];

    private static readonly string[] Surnames = ["林", "陳", "王", "張", "李", "黃", "吳", "劉", "蔡", "楊"];
    private static readonly string[] GivenNames = ["怡君", "家豪", "雅婷", "志明", "淑芬", "建宏", "佩珊", "俊傑", "美玲", "宗翰"];
    private static readonly string[] Titles = ["採購經理", "工廠廠長", "資訊主管", "業務窗口", "財務主管"];

    /// <summary>換過公司的聯絡人：聯絡人序號 → 前一家公司的序號（任職經歷兩筆以上）。第 7 位待過兩家。</summary>
    private static readonly Dictionary<int, int[]> FormerCompanies = new() { [2] = [5], [4] = [8], [7] = [0, 3] };

    /// <summary>某個角色的往來對象是否會建立：角色是「兩者」時，客戶或供應商任一方需要就建。</summary>
    public static bool Wanted(string role, IReadOnlySet<string> roles) =>
        role == "both" ? roles.Contains("customer") || roles.Contains("supplier") : roles.Contains(role);

    public void Plan(DemoPlan plan)
    {
        var needs = openApps.Where(Needs.ContainsKey).Select(id => Needs[id]).ToList();
        if (needs.Count == 0) return;
        var roles = needs.SelectMany(n => n.Roles).ToHashSet();
        var users = needs.SelectMany(n => n.Users).Distinct().ToArray();
        string User(int i) => users[i % users.Length];
        var today = plan.Today;

        // ---- 往來對象 ----
        var partners = new Dictionary<int, DemoRecord>();
        for (var i = 0; i < Companies.Length; i++)
        {
            var (name, role) = Companies[i];
            if (!Wanted(role, roles)) continue;
            var n = i;
            var record = plan.Create(PartnerModule, plan.At(today.AddDays(-80 + i)), User(i), $"往來對象 {name} 建立", ctx => new CreateRecordRequest(
                ctx.Masters!.Ids.ModuleKeys[PartnerSymbol], ctx.Masters.Values(PartnerSymbol,
                    ("name", name), ("role", ctx.Masters.Opt(PartnerSymbol, role)), ("taxId", $"{90000000 + n * 1371:00000000}"),
                    ("phone", $"02-5550-{1000 + n * 37:0000}"), ("email", $"service{n + 1}@example.com"),
                    ("address", $"示範市中山路 {100 + n * 8} 號"), ("owner", User(n)),
                    ("salesPaymentTerms", n % 2 == 0 ? "月結 30 天" : "月結 60 天"), ("active", true))));
            record.Facts["name"] = name;
            record.Facts["role"] = role;
            partners[i] = record;
        }

        // ---- 聯絡人：掛在客戶公司下；幾位換過公司，任職經歷有兩筆以上 ----
        if (!needs.Any(n => n.Contacts)) return;
        var companies = partners.Keys.Where(k => Companies[k].Role != "supplier").Take(Surnames.Length).ToList();
        for (var c = 0; c < companies.Count; c++)
        {
            var n = c;
            var current = partners[companies[c]];
            var former = (FormerCompanies.GetValueOrDefault(c) ?? []).Where(k => partners.ContainsKey(k)).Select(k => partners[k]).ToList();
            var at = plan.At(today.AddDays(-66 + c));
            var name = $"{Surnames[c]}{GivenNames[c]}";
            var record = plan.Create(ContactModule, at, User(c), $"聯絡人 {name} 建立", ctx =>
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

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
