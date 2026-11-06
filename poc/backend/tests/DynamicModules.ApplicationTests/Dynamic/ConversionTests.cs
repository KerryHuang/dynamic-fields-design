using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ConversionService.Commands.ConvertRecord;
using DynamicModules.Application.Dynamic.ConversionService.Commands.DeleteConversion;
using DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.ConversionService.Queries.GetRecordConversions;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>單據轉換：定義驗證、部分轉換與剩餘量、追溯、來源狀態、權限。銷售與採購兩條單據鏈共用同一批斷言。</summary>
public class ConversionTests : ConversionTestBase
{
    private async Task<ServiceResult> Convert(Chain c, DynamicRecordResponse src, string key, params (long Line, decimal Qty)[] lines) =>
        await Send(new ConvertRecordRequest(src.Id, key, null, null,
            lines.Select(l => new ConvertLineInput(l.Line, l.Qty)).ToList()));

    private static string Why(ServiceResult r) => r.Message + string.Join(";", r.Errors.Select(e => e.Key + ":" + e.Value[0]));

    private async Task<ConversionAvailability> Availability(DynamicRecordResponse src, string key)
    {
        var r = await Send(new GetRecordConversionsRequest(src.Id));
        r.IsSuccess.Should().BeTrue(Why(r));
        return ((List<ConversionAvailability>)r.Data!).Single(a => a.Key == key);
    }

    // ───────── 定義驗證 ─────────

    [Fact]
    public async Task Define_Valid_IsSaved()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
    }

    [Fact]
    public async Task Define_TraceFieldMustBeReadOnly()
    {
        var c = await BuildChain("a");
        var def = LinesDefinition(c) with { Lines = LinesDefinition(c).Lines! with { SourceLineFieldId = c.DstLineQty } };
        var r = await Define(c, "a-to", def);
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("definition.lines.sourceLineFieldId");
    }

    [Fact]
    public async Task Define_HeaderSourceFieldMustPointAtSourceModule()
    {
        var c = await BuildChain("a");
        var r = await Define(c, "a-to", LinesDefinition(c) with { Header = new ConversionHeader(c.DstParty, []) });
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("definition.header.sourceFieldId");
    }

    [Fact]
    public async Task Define_SourceAndTargetMustDiffer()
    {
        var c = await BuildChain("a");
        var r = await Send(new CreateConversionRequest(new ConversionInput("x", Text("x"), c.Src, c.Src, true, LinesDefinition(c))));
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Define_CopyWithIncompatibleTypes_Rejected()
    {
        var c = await BuildChain("a");
        var def = LinesDefinition(c) with { Header = new ConversionHeader(c.DstFrom, [new FillMapping(c.SrcMemo, c.DstParty)]) };
        (await Define(c, "a-to", def)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Define_CopyCannotWriteTheTraceFields()
    {
        var c = await BuildChain("a");
        var def = LinesDefinition(c) with
        {
            Lines = LinesDefinition(c).Lines! with { Copy = [new FillMapping(c.LineQty, c.DstLineFrom)] }
        };
        (await Define(c, "a-to", def)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Define_QuantityMustBeNumeric()
    {
        var c = await BuildChain("a");
        var def = LinesDefinition(c) with { Lines = LinesDefinition(c).Lines! with { Quantity = new FillMapping(c.LineItem, c.DstLineItem) } };
        (await Define(c, "a-to", def)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Define_SourceStatesNeedAWorkflowOnTheSource()
    {
        var c = await BuildChain("a");
        // 目標沒有狀態機，把它當來源反向定義，sourceStates 無效
        var r = await Send(new CreateConversionRequest(new ConversionInput("x", Text("x"), c.Dst, c.Src, true,
            new ConversionDefinition([c.SrcDraft], new ConversionHeader(c.DstFrom, []), null))));
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Define_DuplicateKey_Conflict()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        (await Define(c, "a-to")).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Define_RequiresDesignerPermission()
    {
        var c = await BuildChain("a");
        As("sales01");
        (await Define(c, "a-to")).IsForbidden.Should().BeTrue();
    }

    // ───────── 轉換 ─────────

    [Fact]
    public async Task Convert_Full_CopiesValuesAndTracesTheSource()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5), ("螺帽", 4, 3));

        var r = await Convert(c, src, "a-to", (lines[0], 10), (lines[1], 4));
        r.IsCreated.Should().BeTrue(Why(r));
        var dst = (DynamicRecordResponse)r.Data!;
        dst.ModuleKey.Should().Be(c.Dst);
        dst.Values[K(c.DstMemo)]!.Value.GetString().Should().Be("來源備註");
        dst.Values[K(c.DstFrom)]!.Value.GetInt64().Should().Be(src.Id);
        dst.Values[K(c.DstParty)]!.Value.GetInt64().Should().Be(src.Values[K(c.SrcParty)]!.Value.GetInt64());
        var rows = dst.Details![c.DstLine];
        rows.Should().HaveCount(2);
        rows.Select(x => x.Values[K(c.DstLineFrom)]!.Value.GetInt64()).Should().BeEquivalentTo(lines);
        rows.Single(x => x.Values[K(c.DstLineFrom)]!.Value.GetInt64() == lines[0]).Values[K(c.DstLineQty)]!.Value.GetDecimal().Should().Be(10);
        rows.Single(x => x.Values[K(c.DstLineFrom)]!.Value.GetInt64() == lines[0]).Values[K(c.DstLinePrice)]!.Value.GetDecimal().Should().Be(5);
    }

    [Fact]
    public async Task Convert_Partial_ThenTheRest_ShowsRemaining()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));

        (await Convert(c, src, "a-to", (lines[0], 4))).IsCreated.Should().BeTrue();
        var mid = await Availability(src, "a-to");
        mid.Lines!.Single().Should().Match<ConversionLineStatus>(l => l.Quantity == 10 && l.Converted == 4 && l.Remaining == 6);
        mid.Targets.Should().HaveCount(1);

        (await Convert(c, (await Reload(src.Id)), "a-to", (lines[0], 6))).IsCreated.Should().BeTrue();
        var done = await Availability(src, "a-to");
        done.Lines!.Single().Remaining.Should().Be(0);
        done.Targets.Should().HaveCount(2);
    }

    [Fact]
    public async Task Convert_MoreThanRemaining_Rejected_AndNothingIsCreated()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        (await Convert(c, src, "a-to", (lines[0], 7))).IsCreated.Should().BeTrue();

        var r = await Convert(c, await Reload(src.Id), "a-to", (lines[0], 4)); // 剩 3
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("lines[0].quantity");
        (await Availability(src, "a-to")).Targets.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Convert_NonPositiveQuantity_Rejected(int qty)
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        (await Convert(c, src, "a-to", (lines[0], qty))).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Convert_NoLines_Rejected()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, _) = await NewSource(c, ("螺絲", 10, 5));
        (await Convert(c, src, "a-to")).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Convert_LineOfAnotherSource_Rejected()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, _) = await NewSource(c, ("螺絲", 10, 5));
        var (_, otherLines) = await NewSource(c, ("別張", 10, 5));
        var r = await Convert(c, src, "a-to", (otherLines[0], 1));
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("lines[0].sourceLineId");
    }

    [Fact]
    public async Task Convert_SameLineTwiceInOneRequest_CountsTogether()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var r = await Convert(c, src, "a-to", (lines[0], 6), (lines[0], 6));
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Convert_DeletingTheTarget_ReleasesTheQuantity()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var dst = (DynamicRecordResponse)(await Convert(c, src, "a-to", (lines[0], 10))).Data!;
        (await Availability(src, "a-to")).Lines!.Single().Remaining.Should().Be(0);

        (await Send(new DeleteRecordRequest(dst.Id, dst.RowVersion))).IsSuccess.Should().BeTrue();
        var after = await Availability(src, "a-to");
        after.Lines!.Single().Remaining.Should().Be(10);
        after.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task Convert_StaleSourceRowVersion_Conflict()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var r = await Send(new ConvertRecordRequest(src.Id, "a-to", "not-the-current-version", null, [new ConvertLineInput(lines[0], 1)]));
        r.IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Convert_BumpsTheSourceRowVersion()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        (await Convert(c, src, "a-to", (lines[0], 1))).IsCreated.Should().BeTrue();
        (await Reload(src.Id)).RowVersion.Should().NotBe(src.RowVersion);
        // 再用舊版轉一次：被擋
        (await Send(new ConvertRecordRequest(src.Id, "a-to", src.RowVersion, null, [new ConvertLineInput(lines[0], 1)]))).IsConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Convert_HeaderValueOverride_IsHonoured()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var r = await Send(new ConvertRecordRequest(src.Id, "a-to", null,
            new Dictionary<string, JsonElement?> { [K(c.DstMemo)] = J("\"改過的備註\"") }, [new ConvertLineInput(lines[0], 1)]));
        r.IsCreated.Should().BeTrue(Why(r));
        ((DynamicRecordResponse)r.Data!).Values[K(c.DstMemo)]!.Value.GetString().Should().Be("改過的備註");
    }

    [Fact]
    public async Task Convert_OverridingTraceFields_IsIgnored()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var (other, _) = await NewSource(c, ("別張", 1, 1));
        var r = await Send(new ConvertRecordRequest(src.Id, "a-to", null,
            new Dictionary<string, JsonElement?> { [K(c.DstFrom)] = J(other.Id.ToString()) }, [new ConvertLineInput(lines[0], 1)]));
        r.IsCreated.Should().BeTrue(Why(r));
        ((DynamicRecordResponse)r.Data!).Values[K(c.DstFrom)]!.Value.GetInt64().Should().Be(src.Id);
    }

    // ───────── 來源狀態 ─────────

    [Fact]
    public async Task Convert_SourceStates_BlockUntilApproved()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to", LinesDefinition(c, [c.SrcApproved]));
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));

        var blocked = await Convert(c, src, "a-to", (lines[0], 1));
        blocked.IsSuccess.Should().BeFalse();
        blocked.Errors.Should().ContainKey("_record");
        var info = await Availability(src, "a-to");
        info.Allowed.Should().BeFalse();

        var approved = await Approve(c, src);
        (await Availability(approved, "a-to")).Allowed.Should().BeTrue();
        (await Convert(c, approved, "a-to", (lines[0], 1))).IsCreated.Should().BeTrue();
    }

    // ───────── 表頭轉換 ─────────

    [Fact]
    public async Task HeaderOnly_CanBeConvertedOnlyOnce()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-hdr", new ConversionDefinition([],
            new ConversionHeader(c.DstFrom, [new FillMapping(c.SrcTitle, c.DstTitle), new FillMapping(c.SrcMemo, c.DstMemo)]), null));
        var (src, _) = await NewSource(c, ("螺絲", 10, 5));
        var first = await Send(new ConvertRecordRequest(src.Id, "a-hdr", null, null, null));
        first.IsCreated.Should().BeTrue(Why(first));
        var second = await Send(new ConvertRecordRequest((await Reload(src.Id)).Id, "a-hdr", null, null, null));
        second.IsConflict.Should().BeTrue();
    }

    // ───────── 權限 ─────────

    [Fact]
    public async Task Convert_WithoutReadOnTheSource_NotFound()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        As("sales01"); // 沒有任何模組權限
        var r = await Convert(c, src, "a-to", (lines[0], 1));
        r.IsSuccess.Should().BeFalse();
        (r.IsNotFound || r.IsForbidden).Should().BeTrue();
    }

    [Fact]
    public async Task Convert_WithoutAddOnTheTarget_Forbidden()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var sales = Db.Context.Set<PermissionGroup>().Single(g => g.Key == "sales").Id;
        foreach (var m in new[] { c.Src, c.Party })
            Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = sales, ProgramCode = (await Detail(m)).ProgramCode, CanRead = true
            });
        await Db.Context.SaveChangesAsync();
        As("sales01");
        var r = await Convert(c, src, "a-to", (lines[0], 1));
        r.IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Convert_InactiveDefinition_NotFound()
    {
        var c = await BuildChain("a");
        (await Define(c, "a-to", null, active: false)).IsSuccess.Should().BeTrue();
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        (await Convert(c, src, "a-to", (lines[0], 1))).IsNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Definition_KeepsExistingTargets()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        var dst = (DynamicRecordResponse)(await Convert(c, src, "a-to", (lines[0], 1))).Data!;
        (await Send(new DeleteConversionRequest("a-to"))).IsSuccess.Should().BeTrue();
        dst.Values[K(c.DstFrom)]!.Value.GetInt64().Should().Be(src.Id);
        (await Convert(c, src, "a-to", (lines[0], 1))).IsNotFound.Should().BeTrue();
    }

    // ───────── 追溯欄位不能被一般存檔改動 ─────────

    [Fact]
    public async Task TraceFields_CannotBeForgedByOrdinaryRecordWrites()
    {
        var c = await BuildChain("a");
        await DefineOk(c, "a-to");
        var (src, lines) = await NewSource(c, ("螺絲", 10, 5));
        // 直接新增目標單與明細，企圖把明細掛到來源明細上
        var row = new DetailRowInput(null, new Dictionary<string, JsonElement?>
        {
            [K(c.DstLineTitle)] = J("\"偷渡\""), [K(c.DstLineQty)] = J("9"), [K(c.DstLineFrom)] = J(lines[0].ToString())
        }, null);
        var r = await Send(new CreateRecordRequest(c.Dst,
            new Dictionary<string, JsonElement?> { [K(c.DstTitle)] = J("\"D\""), [K(c.DstFrom)] = J(src.Id.ToString()) }, null,
            new Dictionary<string, List<DetailRowInput>> { [c.DstLine] = [row] }));
        r.IsCreated.Should().BeTrue(Why(r));
        var created = (DynamicRecordResponse)r.Data!;
        created.Values.Should().NotContainKey(K(c.DstFrom)); // 唯讀欄位的輸入被忽略
        created.Details![c.DstLine].Single().Values.Should().NotContainKey(K(c.DstLineFrom));
        (await Availability(src, "a-to")).Lines!.Single().Remaining.Should().Be(10);
    }

    private async Task<DynamicRecordResponse> Reload(long id) =>
        (DynamicRecordResponse)(await Send(new DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord.GetRecordRequest(id))).Data!;
}
