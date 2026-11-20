using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>
/// 彙總欄位的條件比較日期時間時依營運時區（RollupCalculator 用 DI 的 FieldTypeRegistry）：
/// 測試時鐘的今天是 2026-11-04；UTC 16:30 在 +08:00 是隔天、在 +07:00 是今天。SQLite 與 PostgreSQL 雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class TimeZoneRollupTests : DesignerTestBase
{
    protected override bool ScopePerRequest => true;

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private async Task<long> New(string module, params (string Key, object? Value)[] values)
    {
        var r = await Send(new CreateRecordRequest(module, Vals(values)));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return ((DynamicRecordResponse)r.Data!).Id;
    }

    private async Task<decimal?> CountOf(long id, string key)
    {
        var rec = await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().SingleAsync(r => r.Id == id);
        var v = JsonDocument.Parse(rec.ValuesJson).RootElement;
        return v.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDecimal() : null;
    }

    private async Task<decimal?> Run(string? zone)
    {
        if (zone is not null)
            (await Send(new UpdateSystemPolicyRequest(PolicyCatalog.LocaleTimeZone.Code, JsonSerializer.SerializeToElement(zone)))).IsSuccess.Should().BeTrue();
        var req = await NewModule("req");
        var rec = await NewModule("rec");
        var source = await MustAdd(rec, FieldType.Reference, RefTo(req), "申請");
        var at = await MustAdd(rec, FieldType.DateTime, label: "時間");
        var count = await MustAdd(req, FieldType.Rollup, new FieldConfig
        {
            Rollup = new RollupConfig(source.Id, RollupFunction.Count, null, J($$"""{"field":{{at.Id}},"op":"withinNextDays","value":0}"""))
        }, "今天筆數");
        (await Send(new ActivateModuleRequest(req))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(rec))).IsSuccess.Should().BeTrue();
        var reqTitle = $"f_{(await Detail(req)).TitleFieldId}";
        var recTitle = $"f_{(await Detail(rec)).TitleFieldId}";
        var parent = await New(req, (reqTitle, "申請"));
        await New(rec, (recTitle, "紀錄"), (source.Key, parent), (at.Key, "2026-11-04T16:30:00Z"));
        return await CountOf(parent, count.Key);
    }

    [Fact]
    public async Task Rollup_DateTimeCondition_FactoryZone() => (await Run(null)).Should().Be(0m);

    [Fact]
    public async Task Rollup_DateTimeCondition_FollowsBusinessZone() => (await Run("+07:00")).Should().Be(1m);
}
