using System.Text.Json.Nodes;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 四個舊範本（tests/Fixtures/templates）併入「MES 模具廠」套件（apps/mes-mold.app.json）時沒有丟東西：
/// 每個模組、區塊、欄位（型別與設定）、選項、規則、檢視、權限逐項等價，只允許設計文件 §4.2 列出的改動（白名單）。
/// </summary>
public class MesMigrationEquivalenceTests
{
    private static JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    // 白名單：舊範本只有 zh-TW、en-US，套件另外補了 th、vi（機器翻譯），這裡去掉再比
    private static readonly JsonNode Mes = StripAddedLocales(Load(Path.Combine(AppContext.BaseDirectory, "apps", "mes-mold.app.json")));

    private static JsonNode StripAddedLocales(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.ContainsKey("zh-TW")) { o.Remove("th"); o.Remove("vi"); }
                foreach (var child in o.Select(kv => kv.Value).ToList()) if (child is not null) StripAddedLocales(child);
                break;
            case JsonArray a:
                foreach (var child in a) if (child is not null) StripAddedLocales(child);
                break;
        }
        return node;
    }

    public static IEnumerable<object[]> Templates => new[] { "order", "repair", "mold-care", "quality" }.Select(t => new object[] { t });

    private static JsonObject MesModule(string code) =>
        Mes["modules"]!.AsArray().Select(m => m!.AsObject()).Single(m => (string)m["code"]! == code);

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static JsonNode? WithoutRefTarget(JsonNode? config)
    {
        var copy = config?.DeepClone().AsObject();
        copy?.Remove("refTarget");
        return copy;
    }

    // 白名單（設計 §4.2）：這些欄位的型別／設定允許不同，或套件多出來的欄位／規則／權限
    private static readonly HashSet<string> ChangedFields =
        ["line.unit"];                                                                            // 下拉改文字：轉換不能複製下拉
    // 只改參照目標（s2d-0 模具參照改指 MES 自己的模組 mold）：型別、其他設定、選項照比，refTarget 斷言等於預期值
    private static readonly Dictionary<string, (string Kind, string Key)> RetargetedRefs =
        new() { ["repair.mold"] = ("module", "mold"), ["care.mold"] = ("module", "mold") };
    // 模具從核心實體的擴充模組改為 MES 自己的模組（s2d-0）：代碼、型態、宿主、標題欄位與區塊都不同，欄位逐一比對
    private static readonly HashSet<string> RehomedModules = ["mold"];
    // 舊範本沒有分區、選單歸到「自訂功能」的模組，套件補上分區：只比對期望的新值
    private static readonly Dictionary<string, string> AddedSections = new() { ["part"] = "設備" };
    private static readonly HashSet<string> AddedFields =
        ["order.fromQuote", "order.qualityCount", "line.sourceLine", "quality.workOrder",      // 追溯、彙總、掛工令
         "mold.no", "mold.name"];                                                                // 模具自己的編號與名稱（s2d-0）
    private static readonly Dictionary<string, string[]> AddedPermissionGroups =
        new() { ["order"] = ["planner", "qa"], ["quality"] = ["planner"] };                      // 原本 Demo 用指令補的權限、生管可讀
    private static readonly HashSet<string> DroppedRules = ["order:readOnly:*"];                 // 整單唯讀規則改由狀態機的唯讀狀態達成

    private static string RuleId(string module, JsonNode r) =>
        $"{module}:{(string)r["kind"]!}:{string.Join(',', r["targets"]!.AsArray().Select(t => (string)t!))}";

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryModuleOfTheTemplate_IsInTheMesPackage_Equivalently(string template)
    {
        // 白名單：套件群組代號不收連字號，sales-manager 在套件裡叫 salesManager（開啟時對應到同一個群組）
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", $"{template}.json"))
            .Replace("\"sales-manager\"", "\"salesManager\""))!;
        foreach (var old in fixture["modules"]!.AsArray().Select(m => m!.AsObject()))
        {
            var code = (string)old["code"]!;
            var now = MesModule(code);

            var rehomed = RehomedModules.Contains(code);
            if (!rehomed) ((string)now["key"]!).Should().Be("mes-" + (string)old["key"]!, code);
            foreach (var prop in rehomed ? new[] { "name", "icon", "section" } : new[] { "name", "icon", "section", "kind", "hostEntityKey", "titleField" })
                if (prop == "section" && AddedSections.TryGetValue(code, out var section))
                {
                    old[prop].Should().BeNull($"{code} 舊範本沒有分區");
                    ((string?)now[prop]).Should().Be(section, $"{code}.{prop}");
                }
                else
                    Same(old[prop], now[prop]).Should().BeTrue($"{code}.{prop}");

            // 區塊與欄位
            var nowBlocks = now["blocks"]!.AsArray().ToDictionary(b => (string)b!["code"]!);
            foreach (var oldBlock in old["blocks"]!.AsArray().Select(b => b!))
            {
                var nowFields = rehomed
                    ? now["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray()).ToDictionary(f => (string)f!["code"]!)
                    : nowBlocks[(string)oldBlock["code"]!]!["fields"]!.AsArray().ToDictionary(f => (string)f!["code"]!);
                if (!rehomed)
                {
                    var nowBlock = nowBlocks[(string)oldBlock["code"]!];
                    Same(oldBlock["label"], nowBlock!["label"]).Should().BeTrue($"{code} 區塊 {oldBlock["code"]} 標籤");
                    ((int?)oldBlock["columns"]).Should().Be((int?)nowBlock!["columns"], $"{code} 區塊欄數");
                }
                foreach (var oldField in oldBlock["fields"]!.AsArray())
                {
                    var fieldCode = (string)oldField!["code"]!;
                    var nowField = nowFields[fieldCode]!;
                    Same(oldField!["label"], nowField["label"]).Should().BeTrue($"{code}.{fieldCode} 標籤");
                    if (ChangedFields.Contains($"{code}.{fieldCode}")) continue;
                    Same(oldField["type"], nowField["type"]).Should().BeTrue($"{code}.{fieldCode} 型別");
                    if (RetargetedRefs.TryGetValue($"{code}.{fieldCode}", out var target))
                    {
                        var nowTarget = nowField["config"]?["refTarget"];
                        ((string?)nowTarget?["kind"]).Should().Be(target.Kind, $"{code}.{fieldCode} 參照目標型態");
                        ((string?)nowTarget?["key"]).Should().Be(target.Key, $"{code}.{fieldCode} 參照目標");
                        Same(WithoutRefTarget(oldField["config"]), WithoutRefTarget(nowField["config"])).Should().BeTrue($"{code}.{fieldCode} 設定（refTarget 以外）");
                    }
                    else
                        Same(oldField["config"], nowField["config"]).Should().BeTrue($"{code}.{fieldCode} 設定");
                    Same(oldField["options"], nowField["options"]).Should().BeTrue($"{code}.{fieldCode} 選項");
                }
            }
            // 套件多出來的欄位只能是白名單
            var oldFieldCodes = old["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray()).Select(f => (string)f!["code"]!).ToHashSet();
            var extra = now["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray()).Select(f => (string)f!["code"]!)
                .Where(f => !oldFieldCodes.Contains(f)).Select(f => $"{code}.{f}");
            extra.Should().BeSubsetOf(AddedFields, $"{code} 多出的欄位");

            // 規則：除了白名單拿掉的，順序與內容都一樣
            var expectedRules = old["rules"]!.AsArray().Where(r => !DroppedRules.Contains(RuleId(code, r!))).ToList();
            var nowRules = now["rules"]!.AsArray().ToList();
            nowRules.Should().HaveCount(expectedRules.Count, $"{code} 規則數");
            for (var i = 0; i < expectedRules.Count; i++) Same(expectedRules[i], nowRules[i]).Should().BeTrue($"{code} 規則 {i}");

            // 檢視
            Same(old["views"], now["views"]).Should().BeTrue($"{code} 檢視");

            // 權限：舊的每一筆都在，多出來的只能是白名單的群組
            var nowPerms = now["permissions"]!.AsArray();
            foreach (var p in old["permissions"]!.AsArray())
                nowPerms.Any(x => Same(p, x)).Should().BeTrue($"{code} 權限 {p!["group"]}");
            var oldGroups = old["permissions"]!.AsArray().Select(p => (string)p!["group"]!).ToHashSet();
            nowPerms.Select(p => (string)p!["group"]!).Where(g => !oldGroups.Contains(g))
                .Should().BeSubsetOf(AddedPermissionGroups.GetValueOrDefault(code, []), $"{code} 多出的權限群組");
        }
    }

    [Fact]
    public void LineUnit_IsNowFreeText_BecauseConversionsCannotCopyDropdowns()
    {
        var unit = MesModule("line")["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray()).Single(f => (string)f!["code"]! == "unit")!;
        ((string)unit["type"]!).Should().Be("text");
    }

    [Fact]
    public void QualityOnlyRules_AndFieldAccess_FromTheOldDemoSetup_AreNowPartOfThePackage()
    {
        // 原本 Demo 在套用範本後用指令補的：責任單位對業務唯讀、不良率對現場隱藏、重大嚴重度選項只開給品保
        Mes["fieldAccess"]!.AsArray().Any(a => (string)a!["field"]! == "quality.responsibleUnit" && (string)a["group"]! == "sales" && (string)a["access"]! == "readOnly")
            .Should().BeTrue();
        Mes["fieldAccess"]!.AsArray().Any(a => (string)a!["field"]! == "quality.defectRate" && (string)a["group"]! == "floor" && (string)a["access"]! == "hidden")
            .Should().BeTrue();
        Mes["optionScopes"]!.AsArray().Any(o => (string)o!["option"]! == "quality.critical" && o["groups"]!.AsArray().Select(g => (string)g!).SequenceEqual(["qa"]))
            .Should().BeTrue();
    }
}
