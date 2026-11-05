using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 8 階段決策 2：欄位預設今天、明細欄位帶表頭的值。</summary>
public class DynamicDefaultTests : DesignerTestBase
{
    private const string Today = "2026-11-04";

    private static Dictionary<string, JsonElement?> Vals(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));

    private static string? Str(DynamicRecordResponse r, FieldResponse f) =>
        r.Values.TryGetValue(f.Key, out var v) && v is { ValueKind: JsonValueKind.String } t ? t.GetString() : null;

    private static DynamicRecordResponse Ok(ServiceResult r)
    {
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<string> TitleKey(string module) => $"f_{(await Detail(module)).TitleFieldId}";

    // ---- 預設今天 ----

    private async Task<(string Module, FieldResponse Date)> TodayModule(FieldConfig? config = null)
    {
        var module = await NewModule("task");
        var date = await MustAdd(module, FieldType.Date, config ?? new FieldConfig { DefaultToday = true }, "日期");
        (await Send(new ActivateModuleRequest(module))).IsSuccess.Should().BeTrue();
        return (module, date);
    }

    private async Task<DynamicRecordResponse> CreateTask(string module, params (string Key, object? Value)[] extra) =>
        Ok(await Send(new CreateRecordRequest(module, Vals([(await TitleKey(module), "x"), .. extra]))));

    [Fact]
    public async Task DefaultToday_FillsBusinessToday_OnCreate()
    {
        var (module, date) = await TodayModule();
        Str(await CreateTask(module), date).Should().Be(Today);
    }

    [Fact]
    public async Task DefaultToday_ProvidedValueWins()
    {
        var (module, date) = await TodayModule();
        Str(await CreateTask(module, (date.Key, "2026-01-02")), date).Should().Be("2026-01-02");
    }

    [Fact]
    public async Task DefaultToday_NotOnUpdate()
    {
        var (module, date) = await TodayModule();
        var created = await CreateTask(module);

        var updated = Ok(await Send(new UpdateRecordRequest(created.Id, new Dictionary<string, JsonElement?> { [date.Key] = null }, created.RowVersion, null)));

        Str(updated, date).Should().BeNull();
    }

    [Fact]
    public async Task DefaultToday_OnlyDate()
    {
        var module = await NewModule("task");
        var r = await AddField(module, FieldType.Text, new FieldConfig { DefaultToday = true });
        r.IsSuccess.Should().BeFalse();
        Err(r, "Config.DefaultToday").Should().Be("只有日期欄位可以預設今天");
    }

    [Fact]
    public async Task DefaultToday_WithDefaultValue_Rejected()
    {
        var module = await NewModule("task");
        var r = await AddField(module, FieldType.Date, new FieldConfig { DefaultToday = true, DefaultValue = J("\"2026-01-01\"") });
        r.IsSuccess.Should().BeFalse();
        Err(r, "Config.DefaultToday").Should().Be("預設今天與預設值只能擇一");
    }

    [Fact]
    public async Task DefaultToday_SwitchedOnLater_DoesNotBackfill()
    {
        var (module, date) = await TodayModule(new FieldConfig());
        var created = await CreateTask(module);
        Str(created, date).Should().BeNull();

        (await Update(date, new FieldConfig { DefaultToday = true })).IsSuccess.Should().BeTrue();

        Str(Ok(await Send(new GetRecordRequest(created.Id))), date).Should().BeNull("改成預設今天只影響之後新增的記錄");
    }

    // ---- 帶表頭的值 ----

    private sealed record Order(string Parent, string Child, FieldResponse HeaderDate, FieldResponse LineDate, string ParentTitle, string ChildTitle);

    private async Task<Order> ArrangeOrder(FieldType headerType = FieldType.Date, FieldType lineType = FieldType.Date, bool link = true)
    {
        var parent = await NewModule("order");
        var child = await NewModule("line");
        await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail), "表頭");
        var headerDate = await MustAdd(parent, headerType, label: "交期");
        var lineDate = await MustAdd(child, lineType,
            link ? new FieldConfig { ParentDefaultFieldId = headerDate.Id } : new FieldConfig(), "交期");
        (await Send(new ActivateModuleRequest(parent))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(child))).IsSuccess.Should().BeTrue();
        return new Order(parent, child, headerDate, lineDate, await TitleKey(parent), await TitleKey(child));
    }

    private static DetailRowInput Row(Order o, params (string Key, object? Value)[] extra) =>
        new(null, Vals([(o.ChildTitle, "品項"), .. extra]), null, false);

    private async Task<DynamicRecordResponse> CreateOrder(Order o, string? due, params DetailRowInput[] rows) =>
        Ok(await Send(new CreateRecordRequest(o.Parent,
            Vals([(o.ParentTitle, "訂單"), .. due is null ? [] : new[] { (o.HeaderDate.Key, (object?)due) }]), null,
            new Dictionary<string, List<DetailRowInput>> { [o.Child] = [.. rows] })));

    [Fact]
    public async Task ParentDefault_NewDetailRow_CopiesHeaderValue()
    {
        var o = await ArrangeOrder();
        var header = await CreateOrder(o, "2026-12-01", Row(o));
        Str(header.Details![o.Child][0], o.LineDate).Should().Be("2026-12-01");
    }

    [Fact]
    public async Task ParentDefault_UsesHeaderValueOfThisSave()
    {
        var o = await ArrangeOrder();
        var header = await CreateOrder(o, "2026-12-01");

        var updated = Ok(await Send(new UpdateRecordRequest(header.Id, Vals((o.HeaderDate.Key, "2026-12-10")), header.RowVersion,
            new Dictionary<string, List<DetailRowInput>> { [o.Child] = [Row(o)] })));

        Str(updated.Details![o.Child][0], o.LineDate).Should().Be("2026-12-10");
    }

    [Fact]
    public async Task ParentDefault_ExistingRowUntouched()
    {
        var o = await ArrangeOrder();
        var header = await CreateOrder(o, "2026-12-01", Row(o));

        var updated = Ok(await Send(new UpdateRecordRequest(header.Id, Vals((o.HeaderDate.Key, "2026-12-10")), header.RowVersion, null)));

        Str(updated.Details![o.Child][0], o.LineDate).Should().Be("2026-12-01");
    }

    [Fact]
    public async Task ParentDefault_NotDetailModule_Rejected()
    {
        var module = await NewModule("plain");
        var other = await MustAdd(module, FieldType.Date, label: "A");
        var r = await AddField(module, FieldType.Date, new FieldConfig { ParentDefaultFieldId = other.Id });
        r.IsSuccess.Should().BeFalse();
        Err(r, "Config.ParentDefaultFieldId").Should().Be("只有明細模組的欄位可以帶表頭的值");

        var existing = await MustAdd(module, FieldType.Date, label: "B");
        var u = await Update(existing, new FieldConfig { ParentDefaultFieldId = other.Id });
        Err(u, "Config.ParentDefaultFieldId").Should().Be("只有明細模組的欄位可以帶表頭的值");
    }

    [Fact]
    public async Task ParentDefault_TypeMismatch_Rejected()
    {
        var parent = await NewModule("order");
        var child = await NewModule("line");
        await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail), "表頭");
        var headerText = await MustAdd(parent, FieldType.Text, label: "備註");

        var r = await AddField(child, FieldType.Date, new FieldConfig { ParentDefaultFieldId = headerText.Id });

        Err(r, "Config.ParentDefaultFieldId").Should().Be("表頭欄位不存在、未啟用或型別不同");
    }

    [Fact]
    public async Task ParentDefault_EnumNotAllowed()
    {
        var parent = await NewModule("order");
        var child = await NewModule("line");
        await MustAdd(child, FieldType.Reference, RefTo(parent, ReferenceRelation.Detail), "表頭");
        var headerEnum = await MustAdd(parent, FieldType.Enum, label: "狀態");

        var r = await AddField(child, FieldType.Enum, new FieldConfig { ParentDefaultFieldId = headerEnum.Id });

        Err(r, "Config.ParentDefaultFieldId").Should().Be("表頭欄位不存在、未啟用或型別不同");
    }

    [Fact]
    public async Task ParentDefault_HeaderFieldInactive_Ignored()
    {
        var o = await ArrangeOrder();
        var header = await CreateOrder(o, "2026-12-01");
        (await Deactivate(o.HeaderDate.Id)).IsSuccess.Should().BeTrue();

        var updated = Ok(await Send(new UpdateRecordRequest(header.Id, [], header.RowVersion,
            new Dictionary<string, List<DetailRowInput>> { [o.Child] = [Row(o)] })));

        Str(updated.Details![o.Child][0], o.LineDate).Should().BeNull();
    }

    [Fact]
    public async Task ParentDefault_WithDefaultValueOrToday_Rejected()
    {
        var o = await ArrangeOrder(link: false);
        var today = await AddField(o.Child, FieldType.Date, new FieldConfig { ParentDefaultFieldId = o.HeaderDate.Id, DefaultToday = true });
        Err(today, "Config.ParentDefaultFieldId").Should().Be("帶表頭的值不能同時設定預設值或預設今天");

        var value = await Update(o.LineDate, new FieldConfig { ParentDefaultFieldId = o.HeaderDate.Id, DefaultValue = J("\"2026-01-01\"") });
        Err(value, "Config.ParentDefaultFieldId").Should().Be("帶表頭的值不能同時設定預設值或預設今天");
    }

    [Fact]
    public async Task ParentDefault_TextField_CopiesHeaderValue()
    {
        var o = await ArrangeOrder(FieldType.Text, FieldType.Text);
        var header = await CreateOrder(o, "北區倉", Row(o));
        Str(header.Details![o.Child][0], o.LineDate).Should().Be("北區倉");
    }
}
