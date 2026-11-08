using System.Net.Http.Json;
using System.Text.Json;

namespace DynamicModules.WebAPITests.Dynamic;

internal static class ApiTestKit
{
    public const string Base = "/api/v3/dynamic";

    public static string NewKey() => "m-" + Guid.NewGuid().ToString("N")[..10];

    public static async Task<JsonElement> JsonOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>());

    public static async Task<string> CreateModule(HttpClient c, string? key = null)
    {
        key ??= NewKey();
        var res = await c.PostAsJsonAsync($"{Base}/modules",
            new { key, name = new Dictionary<string, string> { ["zh-TW"] = "測試模組" } });
        res.EnsureSuccessStatusCode();
        return key;
    }

    /// <summary>回傳新欄位的 id 與 key；區塊用模組建立時的預設區塊。</summary>
    public static async Task<(long Id, string Key, long BlockId)> AddField(
        HttpClient c, string moduleKey, string type, object? config = null)
    {
        var module = await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"));
        var blockId = module.GetProperty("blocks")[0].GetProperty("id").GetInt64();
        var res = await c.PostAsJsonAsync($"{Base}/modules/{moduleKey}/fields", new
        {
            blockId,
            type,
            label = new Dictionary<string, string> { ["zh-TW"] = "欄位" },
            config = config ?? new { }
        });
        if (!res.IsSuccessStatusCode)   // 帶回應內容，偶發的 5xx 才查得到原因
            throw new HttpRequestException($"AddField {type} 失敗 {(int)res.StatusCode}：{await res.Content.ReadAsStringAsync()}");
        var f = await JsonOf(res);
        return (f.GetProperty("id").GetInt64(), f.GetProperty("key").GetString()!, blockId);
    }

    /// <summary>新模組預設的必填標題欄位 key。</summary>
    public static async Task<string> TitleKey(HttpClient c, string moduleKey) =>
        "f_" + (await JsonOf(await c.GetAsync($"{Base}/modules/{moduleKey}"))).GetProperty("titleFieldId").GetInt64();

    public static async Task Activate(HttpClient c, string moduleKey) =>
        (await c.PostAsync($"{Base}/modules/{moduleKey}/activate", null)).EnsureSuccessStatusCode();
}
