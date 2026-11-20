using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetPagedRecords;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecordHistory;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Reports;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>
/// 改營運時區（A17、p3 計畫 Task 4）：同一交易內平移日期時間欄位的索引，日期欄位與原值字串不動；
/// 平移會超出 DateTime 範圍時整次拒絕。AddMinutes 的翻譯與上下限在 SQLite、PostgreSQL 兩邊都要驗。
/// </summary>
[Trait("Category", "Postgres")]
public class TimeZoneChangeTests : DesignerTestBase
{
    private sealed class FailingHook : IPolicyChangeHook
    {
        public bool Reject;
        public Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct) =>
            Task.FromResult(Reject ? ServiceResult.Invalid(new Dictionary<string, string[]> { ["value"] = ["test"] }) : null);
    }

    private readonly FailingHook _hook = new();
    private static readonly PolicyDefinition Tz = PolicyCatalog.LocaleTimeZone;
    private FieldResponse _at = null!, _day = null!;

    protected override bool ScopePerRequest => true;
    protected override bool FieldsFilterable => true;

    // 排在 TimeZoneChangeHook（AddApplication 先註冊）之後
    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IPolicyChangeHook>(_hook);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await NewModule("tz", activate: true);
        _at = await MustAdd("tz", FieldType.DateTime, label: "時間");
        _day = await MustAdd("tz", FieldType.Date, label: "日期");
    }

    private Task<ServiceResult> Put(string value) =>
        Send(new UpdateSystemPolicyRequest(Tz.Code, JsonSerializer.SerializeToElement(value)));

    private async Task<long> Create(string at, string? day = null)
    {
        var m = await Detail("tz");
        var values = new Dictionary<string, JsonElement?>
        {
            [$"f_{m.TitleFieldId}"] = J("\"x\""),
            [_at.Key] = J($"\"{at}\""),
        };
        if (day is not null) values[_day.Key] = J($"\"{day}\"");
        var r = await Send(new CreateRecordRequest("tz", values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return ((DynamicRecordResponse)r.Data!).Id;
    }

    private async Task<DateTime?> IndexOf(long recordId, long fieldId) =>
        (await Db.Context.Set<RecordIndex>().AsNoTracking().SingleAsync(i => i.RecordId == recordId && i.FieldId == fieldId)).DateValue;

    private async Task<string?> StoredAsync() =>
        (await Db.Context.Set<SystemSetting>().AsNoTracking().SingleOrDefaultAsync(s => s.Key == Tz.SettingKey))?.Value;

    private async Task<string> RawValue(long recordId) =>
        JsonDocument.Parse((await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().SingleAsync(r => r.Id == recordId)).ValuesJson)
            .RootElement.GetProperty(_at.Key).GetString()!;

    [Fact]
    public async Task TimeZoneChange_ShiftsDateTimeIndexOnly()
    {
        var id = await Create("2026-11-11T10:00:00+08:00", "2026-11-11");
        var raw = await RawValue(id);

        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        var at = await IndexOf(id, _at.Id);
        at.Should().Be(new DateTime(2026, 11, 11, 9, 0, 0));
        at!.Value.Kind.Should().Be(DateTimeKind.Unspecified);
        (await IndexOf(id, _day.Id)).Should().Be(new DateTime(2026, 11, 11));
        (await RawValue(id)).Should().Be(raw);

        (await Put("+08:00")).IsSuccess.Should().BeTrue();
        (await IndexOf(id, _at.Id)).Should().Be(new DateTime(2026, 11, 11, 10, 0, 0));
    }

    [Fact]
    public async Task TimeZoneChange_FractionalOffset()
    {
        var id = await Create("2026-11-11T10:00:00+08:00");
        (await Put("+05:45")).IsSuccess.Should().BeTrue();
        (await IndexOf(id, _at.Id)).Should().Be(new DateTime(2026, 11, 11, 7, 45, 0));
    }

    private async Task<IReadOnlyList<string>> DayKeys(ReportEngineMode engine)
    {
        using var scope = NewScope();
        scope.ServiceProvider.GetRequiredService<ReportOptions>().Engine = engine;
        var main = new ReportFieldRef("main", _at.Id, null);
        var def = new ReportDefinition(1, "tz", null, [], [new ReportGroup(main, "day")], [new ReportMeasure("n", "countMain", null, null)],
            [], null, null, [], "table");
        var r = await scope.ServiceProvider.GetRequiredService<ReportRunner>().RunAsync(def, new ReportRunOptions(null, null), CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<ReportOptions>().Engine = ReportEngineMode.Auto;
        r.Engine.Should().Be(engine == ReportEngineMode.MemoryOnly ? "memory" : "sql");
        return r.Groups!.Select(g => g.GroupKeys[0]).ToList();
    }

    [Fact]
    public async Task TimeZoneChange_ReportDayBucketFollowsNewZone()
    {
        await Create("2026-11-11T00:30:00+08:00");
        (await DayKeys(ReportEngineMode.MemoryOnly)).Should().ContainSingle().Which.Should().StartWith("2026-11-11");
        (await DayKeys(ReportEngineMode.SqlOnly)).Should().ContainSingle().Which.Should().StartWith("2026-11-11");

        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        (await DayKeys(ReportEngineMode.MemoryOnly)).Should().ContainSingle().Which.Should().StartWith("2026-11-10");
        (await DayKeys(ReportEngineMode.SqlOnly)).Should().ContainSingle().Which.Should().StartWith("2026-11-10");
    }

    [Fact]
    public async Task TimeZoneChange_ListFilterBetweenFollowsNewZone()
    {
        var id = await Create("2026-11-11T10:00:00+08:00");
        // 篩選值必須帶偏移（DateTimeHandler.Normalize 不收無偏移的值），解析時換成營運時區的牆上時間與索引比較：
        // 同一個絕對時間在改時區前後都要找得到；索引沒平移時改成 +07:00 後就找不到（篩選 09:00、索引仍 10:00）
        var filter = $$"""{"all":[{"field":{{_at.Id}},"op":"between","value":["2026-11-11T09:00:00+07:00","2026-11-11T09:30:00+07:00"]}]}""";
        async Task<IReadOnlyList<long>> Match()
        {
            var r = await Send(new GetPagedRecordsRequest("tz", 1, 50, filter, [], [], null));
            r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
            return ((PagedResponse<DynamicRecordResponse>)r.Data!).Items.Select(x => x.Id).ToList();
        }
        (await Match()).Should().Equal(id);
        (await Put("+07:00")).IsSuccess.Should().BeTrue();
        (await Match()).Should().Equal(id);
    }

    [Fact]
    public async Task SameValue_NoShift()
    {
        var id = await Create("2026-11-11T10:00:00+08:00");
        (await Put("+08:00")).IsSuccess.Should().BeTrue();
        (await IndexOf(id, _at.Id)).Should().Be(new DateTime(2026, 11, 11, 10, 0, 0));
    }

    [Fact]
    public async Task HookRejects_IndexAndSettingRolledBack()
    {
        var id = await Create("2026-11-11T10:00:00+08:00");
        _hook.Reject = true;
        var r = await Put("+07:00");
        r.IsSuccess.Should().BeFalse();
        (await IndexOf(id, _at.Id)).Should().Be(new DateTime(2026, 11, 11, 10, 0, 0));
        (await StoredAsync()).Should().BeNull();
        Svc<CachedSystemPolicy>().BusinessOffset.Should().Be(TimeSpan.FromHours(8));
    }

    private async Task AssertRejectedUnchanged(ServiceResult r, long id, DateTime index, long otherId, string message)
    {
        r.IsSuccess.Should().BeFalse();
        r.Errors["value"][0].Should().Be(message);
        (await StoredAsync()).Should().BeNull();
        (await IndexOf(id, _at.Id)).Should().Be(index);
        (await IndexOf(otherId, _at.Id)).Should().Be(new DateTime(2026, 11, 11, 10, 0, 0));
        Svc<CachedSystemPolicy>().BusinessOffset.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task ShiftOverflowsUpper_Rejected()
    {
        var other = await Create("2026-11-11T10:00:00+08:00");
        var id = await Create("9999-12-31T23:30:00+08:00");
        (await IndexOf(id, _at.Id)).Should().Be(new DateTime(9999, 12, 31, 23, 30, 0));
        await AssertRejectedUnchanged(await Put("+09:00"), id, new DateTime(9999, 12, 31, 23, 30, 0), other,
            "改成這個時區會讓 1 筆日期時間超出可儲存的範圍（9999-12-31 23:30 ～ 9999-12-31 23:30），請先修改這些記錄再改時區");
    }

    [Fact]
    public async Task ShiftOverflowsLower_Rejected()
    {
        var other = await Create("2026-11-11T10:00:00+08:00");
        var id = await Create("0001-01-01T08:30:00+08:00");
        (await IndexOf(id, _at.Id)).Should().Be(new DateTime(1, 1, 1, 8, 30, 0));
        await AssertRejectedUnchanged(await Put("-12:00"), id, new DateTime(1, 1, 1, 8, 30, 0), other,
            "改成這個時區會讓 1 筆日期時間超出可儲存的範圍（0001-01-01 08:30 ～ 0001-01-01 08:30），請先修改這些記錄再改時區");
    }

    [Fact]
    public async Task ShiftAtBoundary_Allowed()
    {
        var id = await Create("9999-12-31T22:30:00+08:00");
        (await Put("+09:00")).IsSuccess.Should().BeTrue();
        var at = await IndexOf(id, _at.Id);
        at.Should().Be(new DateTime(9999, 12, 31, 23, 30, 0));
        at!.Value.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    /// <summary>極值只留在歷程（記錄現值已改成一般時間）：改時區後索引可以平移，歷程的舊值換算會超出範圍，顯示要退回原本的偏移、不能讓整頁失敗。</summary>
    [Fact]
    public async Task ExtremeValueOnlyInHistory_HistoryStillLoads()
    {
        var id = await Create("9999-12-31T23:30:00+08:00");
        var rowVersion = (await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().SingleAsync(r => r.Id == id)).RowVersion;
        var upd = await Send(new UpdateRecordRequest(id, new Dictionary<string, JsonElement?> { [_at.Key] = J("\"2026-11-11T10:00:00+08:00\"") }, rowVersion));
        upd.IsSuccess.Should().BeTrue(upd.Message);

        (await Put("+09:00")).IsSuccess.Should().BeTrue();
        var r = await Send(new GetRecordHistoryRequest(id, 1, 50, "zh-TW"));
        r.IsSuccess.Should().BeTrue(r.Message);
        var items = ((PagedResponse<RecordHistoryResponse>)r.Data!).Items.Where(h => h.FieldKey == _at.Key).ToList();
        items.Should().Contain(h => h.OldText == "9999-12-31 23:30 +08:00" && h.NewText == "2026-11-11 11:00");
    }
}
