using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ConversionService;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetRecordConversions;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 單據轉換的加固：目的欄位寫入權限、追溯欄位與數量欄位的完整性（定義驗證、執行時重驗、事後確認）。
/// </summary>
public class ConversionHardeningTests : ConversionTestBase
{
    private static string Why(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + ":" + e.Value[0]));

    private Task<ServiceResult> Convert(DynamicRecordResponse src, string key, long line, decimal qty) =>
        Send(new ConvertRecordRequest(src.Id, key, null, null, [new ConvertLineInput(line, qty)]));

    private async Task<DynamicRecordResponse> Reload(long id) => (DynamicRecordResponse)(await Send(new GetRecordRequest(id))).Data!;

    private async Task<int> RecordCount(string module) =>
        await Db.Context.Set<Record>().AsNoTracking().CountAsync(r => r.ModuleId == Db.Context.Set<Module>().Single(m => m.Key == module).Id);

    private async Task<ConversionAvailability> Availability(DynamicRecordResponse src, string key)
    {
        var r = await Send(new GetRecordConversionsRequest(src.Id));
        r.IsSuccess.Should().BeTrue(Why(r));
        return ((List<ConversionAvailability>)r.Data!).Single(a => a.Key == key);
    }

    private async Task<FieldResponse> FieldOf(string module, long id) =>
        (await Detail(module)).Blocks.SelectMany(b => b.Fields).Single(f => f.Id == id);

    /// <summary>sales 群組：來源與往來對象可讀，目標可讀可新增（明細權限跟著表頭）。</summary>
    private async Task GrantSales(Chain c)
    {
        var sales = Db.Context.Set<PermissionGroup>().Single(g => g.Key == "sales").Id;
        foreach (var (m, add) in new[] { (c.Src, false), (c.Party, false), (c.Dst, true) })
            Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = sales, ProgramCode = (await Detail(m)).ProgramCode, CanRead = true, CanAdd = add, CanAmt = true
            });
        await Db.Context.SaveChangesAsync();
    }

    private async Task Restrict(long fieldId, FieldAccess access)
    {
        var sales = Db.Context.Set<PermissionGroup>().Single(g => g.Key == "sales").Id;
        Db.Context.Set<FieldPermission>().Add(new FieldPermission { FieldId = fieldId, GroupId = sales, Access = access });
        await Db.Context.SaveChangesAsync();
    }

    // ───────── 1. 複製不能繞過目標欄位的寫入權限 ─────────

    [Theory]
    [InlineData(FieldAccess.Hidden)]
    [InlineData(FieldAccess.ReadOnly)]
    public async Task Convert_DoesNotCopyIntoAHeaderFieldTheUserCannotWrite(FieldAccess access)
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        await GrantSales(c);
        await Restrict(c.DstMemo, access);

        As("sales01");
        var r = await Convert(src, "a-to", lines[0], 1);
        r.IsCreated.Should().BeTrue(Why(r));
        As("admin");
        var stored = await Reload(((DynamicRecordResponse)r.Data!).Id);

        stored.Values.Should().NotContainKey(K(c.DstMemo));
        stored.Values[K(c.DstFrom)]!.Value.GetInt64().Should().Be(src.Id); // 追溯欄位仍由系統填
        stored.Values[K(c.DstParty)]!.Value.GetInt64().Should().Be(src.Values[K(c.SrcParty)]!.Value.GetInt64()); // 沒被限制的欄位照複製
    }

    [Theory]
    [InlineData(FieldAccess.Hidden)]
    [InlineData(FieldAccess.ReadOnly)]
    public async Task Convert_DoesNotCopyIntoALineFieldTheUserCannotWrite(FieldAccess access)
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        await GrantSales(c);
        await Restrict(c.DstLineItem, access);

        As("sales01");
        var r = await Convert(src, "a-to", lines[0], 3);
        r.IsCreated.Should().BeTrue(Why(r));
        As("admin");
        var row = (await Reload(((DynamicRecordResponse)r.Data!).Id)).Details![c.DstLine].Single();

        row.Values.Should().NotContainKey(K(c.DstLineItem));
        row.Values[K(c.DstLinePrice)]!.Value.GetDecimal().Should().Be(5);
        row.Values[K(c.DstLineQty)]!.Value.GetDecimal().Should().Be(3);          // 數量與追溯欄位仍由系統填
        row.Values[K(c.DstLineFrom)]!.Value.GetInt64().Should().Be(lines[0]);
    }

    [Fact]
    public async Task Convert_StillCopiesIntoFieldsThatAreOnlyReadOnlyByConfig()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        (await Update(await FieldOf(c.Dst, c.DstMemo), new FieldConfig { ReadOnly = true })).IsSuccess.Should().BeTrue();

        var r = await Convert(src, "a-to", lines[0], 1);
        r.IsCreated.Should().BeTrue(Why(r));
        ((DynamicRecordResponse)r.Data!).Values[K(c.DstMemo)]!.Value.GetString().Should().Be("來源備註");
    }

    // ───────── 2. 追溯欄位與數量欄位不能是同一欄 ─────────

    [Fact]
    public async Task Define_TraceFieldAndQuantityFieldCannotBeTheSameField()
    {
        var c = await BuildChain("a");
        var def = LinesDefinition(c);
        def = def with { Lines = def.Lines! with { Quantity = new FillMapping(c.LineQty, c.DstLineFrom) } };

        var r = await Define(c, "a-to", def);
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("definition.lines.quantity");
    }

    // ───────── 3. 執行時重驗定義：欄位事後被停用，fail-closed ─────────

    [Theory]
    [InlineData("lineTrace")]
    [InlineData("lineQuantity")]
    [InlineData("headerTrace")]
    public async Task Convert_AfterTheDefinitionBecomesInvalid_ConflictsAndChangesNothing(string disabled)
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var field = disabled switch { "lineTrace" => c.DstLineFrom, "lineQuantity" => c.DstLineQty, _ => c.DstFrom };
        (await Deactivate(field)).IsSuccess.Should().BeTrue();

        var r = await Convert(src, "a-to", lines[0], 1);
        r.IsConflict.Should().BeTrue(Why(r));
        r.Message.Should().Be(ConvertRecordHandler.DefinitionInvalid);
        (await RecordCount(c.Dst)).Should().Be(0);
        (await RecordCount(c.DstLine)).Should().Be(0);
        (await Reload(src.Id)).RowVersion.Should().Be(src.RowVersion); // 沒搶互斥權、沒動來源版本號
    }

    [Fact]
    public async Task Availability_OfAnInvalidDefinition_IsNotAllowed_WithAReason()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, _) = await NewSource(c, ("螺絲", 10, 5));
        (await Deactivate(c.DstLineFrom)).IsSuccess.Should().BeTrue();

        var a = await Availability(src, "a-to");
        a.Allowed.Should().BeFalse();
        a.Reason.Should().Be(ConvertRecordHandler.DefinitionInvalid);
    }

    // ───────── 4. 追溯欄位與數量欄位不能是 fill 的目的欄位（兩種順序） ─────────

    private sealed record RefModule(long Int, long Dec);

    private async Task<RefModule> BuildRefModule(string key)
    {
        await NewModule(key);
        var i = await MustAdd(key, FieldType.Integer, label: "整數");
        var d = await MustAdd(key, FieldType.Decimal, label: "小數");
        await Send(new ActivateModuleRequest(key));
        return new RefModule(i.Id, d.Id);
    }

    private Task<ServiceResult> AddLockFill(string module, string refKey, long from, long to, string mode = FillConfig.Lock) =>
        AddField(module, FieldType.Reference, RefTo(refKey) with { Fill = new FillConfig(mode, [new FillMapping(from, to)]) }, "參照主檔");

    [Theory]
    [InlineData("lineTrace", "definition.lines.sourceLineFieldId")]
    [InlineData("lineQuantity", "definition.lines.quantity")]
    public async Task Define_AFillTargetCannotBeATraceOrQuantityField(string target, string errorKey)
    {
        var c = await BuildChain("a");
        var rm = await BuildRefModule("ref-m");
        if (target == "lineQuantity") (await Update(await FieldOf(c.DstLine, c.DstLineQty), new FieldConfig { ReadOnly = true })).IsSuccess.Should().BeTrue();
        var fill = target switch
        {
            "lineTrace" => await AddLockFill(c.DstLine, "ref-m", rm.Int, c.DstLineFrom),
            _ => await AddLockFill(c.DstLine, "ref-m", rm.Dec, c.DstLineQty)
        };
        fill.IsCreated.Should().BeTrue(Why(fill));

        var r = await Define(c, "a-to");
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey(errorKey);
    }

    [Theory]
    [InlineData("lineTrace")]
    [InlineData("lineQuantity")]
    public async Task Convert_WhenAFillIsAddedLater_ConflictsInsteadOfLosingTheTrace(string target)
    {
        var c = await BuildChain("a");
        var rm = await BuildRefModule("ref-m");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        if (target == "lineQuantity") (await Update(await FieldOf(c.DstLine, c.DstLineQty), new FieldConfig { ReadOnly = true })).IsSuccess.Should().BeTrue();
        var fill = target == "lineTrace" ? await AddLockFill(c.DstLine, "ref-m", rm.Int, c.DstLineFrom) : await AddLockFill(c.DstLine, "ref-m", rm.Dec, c.DstLineQty);
        fill.IsCreated.Should().BeTrue(Why(fill));

        var r = await Convert(src, "a-to", lines[0], 4);
        r.IsConflict.Should().BeTrue(Why(r));
        r.Message.Should().Be(ConvertRecordHandler.DefinitionInvalid);
        (await RecordCount(c.Dst)).Should().Be(0);
        (await RecordCount(c.DstLine)).Should().Be(0); // 沒有任何已轉量被吃掉
    }

    // ───────── 最後一道防線：建立後實際存下的追溯值與數量必須等於系統給的值 ─────────

    private sealed class TamperingCreate(IRequestHandler<CreateRecordRequest, ServiceResult> inner, Func<DynamicRecordResponse, Task> tamper)
        : IRequestHandler<CreateRecordRequest, ServiceResult>
    {
        public async Task<ServiceResult> Handle(CreateRecordRequest request, CancellationToken ct)
        {
            var result = await inner.Handle(request, ct);
            if (result.IsCreated) await tamper((DynamicRecordResponse)result.Data!);
            return result;
        }
    }

    private ConvertRecordHandler HandlerWith(IRequestHandler<CreateRecordRequest, ServiceResult> create) => new(
        Svc<IConversionRepository>(), Svc<IModuleRepository>(), Svc<IRecordRepository>(), Svc<IDynamicAccessGuard>(),
        Svc<IUnitOfWork>(), Svc<ConversionLedger>(), Svc<ConversionValidator>(), create);

    [Theory]
    [InlineData("headerTrace")]
    [InlineData("lineTrace")]
    [InlineData("lineQuantity")]
    public async Task Convert_WhenTheStoredTraceOrQuantityDiffers_FailsAndRollsBack(string tampered)
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));

        async Task Tamper(DynamicRecordResponse created)
        {
            var (id, key) = tampered == "headerTrace" ? (created.Id, K(c.DstFrom))
                : (created.Details![c.DstLine].Single().Id, tampered == "lineTrace" ? K(c.DstLineFrom) : K(c.DstLineQty));
            var record = await Db.Context.Set<Record>().SingleAsync(r => r.Id == id);
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(record.ValuesJson)!;
            values[key] = J("999");
            record.ValuesJson = JsonSerializer.Serialize(values);
        }

        var handler = HandlerWith(new TamperingCreate(Svc<IRequestHandler<CreateRecordRequest, ServiceResult>>(), Tamper));
        await using var tx = await Db.Context.Database.BeginTransactionAsync();
        var r = await handler.Handle(new ConvertRecordRequest(src.Id, "a-to", null, null, [new ConvertLineInput(lines[0], 4)]), default);
        r.IsSuccess.Should().BeFalse();
        r.IsConflict.Should().BeTrue(Why(r));
        r.Message.Should().Be(ConvertRecordHandler.StoredMismatch);
        await tx.RollbackAsync(); // 交易行為對失敗結果的做法
        (await RecordCount(c.Dst)).Should().Be(0);
    }

    [Fact]
    public async Task Convert_WhenNothingIsTampered_PassesTheFinalCheck()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var handler = HandlerWith(Svc<IRequestHandler<CreateRecordRequest, ServiceResult>>());
        var r = await handler.Handle(new ConvertRecordRequest(src.Id, "a-to", null, null, [new ConvertLineInput(lines[0], 4)]), default);
        r.IsCreated.Should().BeTrue(Why(r));
    }
}
