using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>第 7 階段決策 5：匯出 CSV／xlsx。</summary>
public class ExportTests : DesignerTestBase
{
    [Fact]
    public void Csv_HasBomHeaderAndRows()
    {
        var bytes = CsvWriter.Write(["標題", "數量"], [["a", "1"], ["b", "2"]]);

        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be("標題,數量\r\na,1\r\nb,2\r\n");
    }

    [Fact]
    public void Csv_QuotesCommaQuoteNewline()
    {
        var bytes = CsvWriter.Write(["h"], [["a,\"b\"\nc"]]);

        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().Be("h\r\n\"a,\"\"b\"\"\nc\"\r\n");
    }

    [Fact]
    public void Csv_EscapesFormulaInjection()
    {
        CsvWriter.EscapeFormula("=1+1").Should().Be("'=1+1");
        CsvWriter.EscapeFormula("@x").Should().Be("'@x");
        CsvWriter.EscapeFormula("-5").Should().Be("'-5");
        CsvWriter.EscapeFormula("abc").Should().Be("abc");
    }

    private async Task<string> Create(string module, params (string Key, object? Value)[] pairs)
    {
        var values = pairs.ToDictionary(p => p.Key, p => (JsonElement?)JsonSerializer.SerializeToElement(p.Value));
        var r = await Send(new CreateRecordRequest(module, values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return module;
    }

    private async Task<long> SalesGroupId() =>
        (await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales")).Id;

    private async Task Grant(string module, bool print = true, bool pricing = true, DataScope? scope = null)
    {
        var m = await Detail(module);
        var sales = await SalesGroupId();
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales, ProgramCode = m.ProgramCode,
            CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true, CanPrint = print, CanAmt = pricing
        });
        if (scope is { } s) Db.Context.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = sales, Scope = s });
        await Db.Context.SaveChangesAsync();
    }

    private async Task<ExportFile> Export(string module, string format = "csv", string? filter = null,
        string[]? sortBy = null, string[]? sortOrder = null, string? q = null)
    {
        var r = await Send(new ExportRecordsRequest(module, format, filter, sortBy ?? [], sortOrder ?? [], q, "zh-TW"));
        r.IsSuccess.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (ExportFile)r.Data!;
    }

    private static string[] Lines(ExportFile f) =>
        Encoding.UTF8.GetString(f.Content, 3, f.Content.Length - 3).TrimEnd('\r', '\n').Split("\r\n");

    [Fact]
    public async Task Csv_EscapesFormulaForAllButNumericCells()
    {
        await NewModule("memo", activate: true);
        var qty = await MustAdd("memo", FieldType.Decimal, new FieldConfig { IsSortable = true, IsFilterable = true }, "數量");
        var rate = await MustAdd("memo", FieldType.Percent, label: "比率");
        var level = await MustAdd("memo", FieldType.Enum, label: "等級");
        var low = (OptionResponse)(await Send(new AddOptionRequest(level.Id, Text("-低"), null))).Data!;
        var title = await Detail("memo");
        await Create("memo", ($"f_{title.TitleFieldId}", "=1+1"), (qty.Key, -5), (rate.Key, -5), (level.Key, $"opt_{low.Id}"));
        await Create("memo", ($"f_{title.TitleFieldId}", "ok"), (qty.Key, 3));

        var file = await Export("memo", sortBy: [qty.Key], sortOrder: ["asc"]);

        file.ContentType.Should().Be("text/csv; charset=utf-8");
        file.FileName.Should().Be("memo-20261104-0900.csv");
        // 數字與百分比的負號不加 '；文字、選項標籤等其餘欄位都加
        Lines(file)[1].Should().Be("'=1+1,-5.00,-5.00%,'-低");
    }

    [Fact]
    public async Task Csv_EscapesFormulaInReferenceTitle()
    {
        var tgt = await NewModule("tgt", activate: true);
        var target = (DynamicModules.Application.Dynamic.RecordService.Data.DynamicRecordResponse)(await Send(new CreateRecordRequest(
            tgt, new Dictionary<string, JsonElement?> { [$"f_{(await Detail(tgt)).TitleFieldId}"] = JsonSerializer.SerializeToElement("=1+1") }))).Data!;
        await NewModule("src");
        var refF = await MustAdd("src", FieldType.Reference, RefTo(tgt), "目標");
        (await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("src")))
            .IsSuccess.Should().BeTrue();
        await Create("src", ($"f_{(await Detail("src")).TitleFieldId}", "a"), (refF.Key, target.Id));

        Lines(await Export("src"))[1].Should().Be("a,'=1+1");
    }

    [Fact]
    public async Task Export_RespectsFilterSortAndScope()
    {
        await NewModule("memo", activate: true);
        var qty = await MustAdd("memo", FieldType.Integer, new FieldConfig { IsSortable = true, IsFilterable = true }, "數量");
        var t = $"f_{(await Detail("memo")).TitleFieldId}";
        await Create("memo", (t, "a"), (qty.Key, 1));
        await Create("memo", (t, "b"), (qty.Key, 5));
        await Create("memo", (t, "c"), (qty.Key, 9));
        await Create("memo", (t, "d"), (qty.Key, 7));
        await Grant("memo", scope: DataScope.Own);
        As("sales01");
        await Create("memo", (t, "mine"), (qty.Key, 8));

        As("admin");
        var filter = $"{{\"all\":[{{\"field\":{qty.Id},\"op\":\"gt\",\"value\":5}}]}}";
        var file = await Export("memo", filter: filter, sortBy: [qty.Key], sortOrder: ["desc"]);
        Lines(file).Skip(1).Should().Equal("c,9", "mine,8", "d,7");

        As("sales01");
        Lines(await Export("memo")).Skip(1).Should().Equal("mine,8");
    }

    [Fact]
    public async Task Export_BadFilter_400_LikePaged()
    {
        await NewModule("memo", activate: true);
        var r = await Send(new ExportRecordsRequest("memo", "csv", "{oops", [], [], null, "zh-TW"));
        r.Errors["filter"][0].Should().Be("篩選條件格式錯誤");
    }

    [Fact]
    public async Task Export_BadFormat_400()
    {
        await NewModule("memo", activate: true);
        var r = await Send(new ExportRecordsRequest("memo", "pdf", null, [], [], null, "zh-TW"));
        r.Errors["format"][0].Should().Be("格式只能是 csv 或 xlsx");
    }

    [Fact]
    public async Task Export_WithoutPricing_NoCurrencyColumn()
    {
        await NewModule("memo", activate: true);
        var qty = await MustAdd("memo", FieldType.Integer, label: "數量");
        var price = await MustAdd("memo", FieldType.Currency, label: "單價");
        await MustAdd("memo", FieldType.Formula,
            new FieldConfig { Formula = $"{{{qty.Id}}}*{{{price.Id}}}", ResultType = "currency" }, "金額");
        await Grant("memo", pricing: false);

        As("sales01");
        var header = Lines(await Export("memo"))[0];

        header.Should().Contain("數量").And.NotContain("單價").And.NotContain("金額");
        As("admin");
        Lines(await Export("memo"))[0].Should().Contain("單價").And.Contain("金額");
    }

    [Fact]
    public async Task Export_HiddenField_NoColumn()
    {
        await NewModule("memo", activate: true);
        var secret = await MustAdd("memo", FieldType.Text, label: "機密");
        await MustAdd("memo", FieldType.Text, label: "公開");
        (await Send(new SaveModulePermissionsRequest("memo", new ModulePermissionsDto(
            [], [], [new FieldPermissionDto(secret.Id, await SalesGroupId(), FieldAccess.Hidden)], [])))).IsSuccess.Should().BeTrue();
        await Grant("memo");

        As("sales01");

        Lines(await Export("memo"))[0].Should().Contain("公開").And.NotContain("機密");
    }

    [Fact]
    public async Task Export_WithoutPrintExport_403()
    {
        await NewModule("memo", activate: true);
        await Grant("memo", print: false);
        As("sales01");

        (await Send(new ExportRecordsRequest("memo", "csv", null, [], [], null, "zh-TW"))).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Export_DetailModule_404()
    {
        await NewModule("order", activate: true);
        await NewModule("line");
        await MustAdd("line", FieldType.Reference, RefTo("order", ReferenceRelation.Detail), "表頭");
        (await Send(new DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule.ActivateModuleRequest("line")))
            .IsSuccess.Should().BeTrue();

        var r = await Send(new ExportRecordsRequest("line", "csv", null, [], [], null, "zh-TW"));

        r.IsNotFound.Should().BeTrue();
        r.Message.Should().Be("模組不存在或未啟用");
    }

    [Fact]
    public async Task Export_OverLimit_400()
    {
        await NewModule("memo", activate: true);
        await CreateRecord("memo");
        await CreateRecord("memo");
        await CreateRecord("memo");
        Svc<ExportOptions>().MaxRecords = 2;

        var r = await Send(new ExportRecordsRequest("memo", "csv", null, [], [], null, "zh-TW"));

        r.IsSuccess.Should().BeFalse();
        r.Errors["filter"][0].Should().Be("匯出最多 2 筆，請加篩選條件");
    }

    [Fact]
    public async Task Xlsx_NumericCellsAreNumbers()
    {
        await NewModule("memo", activate: true);
        var amount = await MustAdd("memo", FieldType.Decimal, label: "數量");
        var status = await MustAdd("memo", FieldType.Enum, label: "狀態");
        var opt = (OptionResponse)(await Send(new AddOptionRequest(status.Id, Text("送審"), null))).Data!;
        await Create("memo", ($"f_{(await Detail("memo")).TitleFieldId}", "x"), (amount.Key, 12.5), (status.Key, $"opt_{opt.Id}"));

        var file = await Export("memo", "xlsx");

        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileName.Should().Be("memo-20261104-0900.xlsx");
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        var ws = wb.Worksheets.Single();
        ws.Name.Should().Be("memo");
        var cols = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
        ws.Cell(2, cols["數量"]).DataType.Should().Be(XLDataType.Number);
        ws.Cell(2, cols["數量"]).GetDouble().Should().Be(12.5);
        ws.Cell(2, cols["狀態"]).DataType.Should().Be(XLDataType.Text);
        ws.Cell(2, cols["狀態"]).GetString().Should().Be("送審");
    }
}
