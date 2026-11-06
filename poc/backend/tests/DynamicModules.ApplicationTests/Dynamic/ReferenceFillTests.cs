using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>選參照後帶值：定義驗證與後端強制（快照語意、lock／default、不洩漏）。</summary>
public class ReferenceFillTests : DesignerTestBase
{
    /// <summary>來源（被參照）模組 src：名稱（標題）、條件；目的模組 dst：參照、名稱副本、條件副本、備註。</summary>
    private sealed record World(
        long SrcName, long SrcTerms, long SrcAmount, long RefId, long DstName, long DstTerms, long DstNote, long DstAmount);

    private async Task<World> Build(string mode = FillConfig.Lock, bool readOnlyTargets = true)
    {
        await NewModule("src");
        var terms = await MustAdd("src", FieldType.Text, label: "條件");
        var amount = await MustAdd("src", FieldType.Currency, label: "單價");
        await Send(new ActivateModuleRequest("src"));
        var srcName = (await Detail("src")).TitleFieldId!.Value;

        await NewModule("dst");
        var dstName = await MustAdd("dst", FieldType.Text, new FieldConfig { ReadOnly = readOnlyTargets }, "名稱副本");
        var dstTerms = await MustAdd("dst", FieldType.Text, new FieldConfig { ReadOnly = readOnlyTargets }, "條件副本");
        var dstAmount = await MustAdd("dst", FieldType.Currency, new FieldConfig { ReadOnly = readOnlyTargets }, "單價副本");
        var note = await MustAdd("dst", FieldType.Text, label: "備註");
        var reference = await MustAdd("dst", FieldType.Reference, RefTo("src") with
        {
            Fill = new FillConfig(mode,
            [
                new FillMapping(srcName, dstName.Id), new FillMapping(terms.Id, dstTerms.Id), new FillMapping(amount.Id, dstAmount.Id)
            ])
        }, "來源");
        await Send(new ActivateModuleRequest("dst"));
        _dstTitle = (await Detail("dst")).TitleFieldId!.Value;
        return new World(srcName, terms.Id, amount.Id, reference.Id, dstName.Id, dstTerms.Id, note.Id, dstAmount.Id);
    }

    private long _dstTitle;

    private static string K(long id) => $"f_{id}";

    private async Task<long> NewSrc(World w, string name, string terms, decimal amount)
    {
        var r = await Send(new CreateRecordRequest("src", new Dictionary<string, JsonElement?>
        {
            [K(w.SrcName)] = J($"\"{name}\""), [K(w.SrcTerms)] = J($"\"{terms}\""), [K(w.SrcAmount)] = J(amount.ToString())
        }));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return ((DynamicRecordResponse)r.Data!).Id;
    }

    private async Task<DynamicRecordResponse> NewDst(World w, long? refId, Dictionary<string, JsonElement?>? extra = null)
    {
        var values = extra ?? [];
        values[$"f_{_dstTitle}"] = J("\"單據\"");
        if (refId is { } id) values[K(w.RefId)] = J(id.ToString());
        var r = await Send(new CreateRecordRequest("dst", values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private async Task<ServiceResult> Edit(DynamicRecordResponse rec, Dictionary<string, JsonElement?> values) =>
        await Send(new UpdateRecordRequest(rec.Id, values, rec.RowVersion));

    private static string? Str(DynamicRecordResponse r, long fieldId) =>
        r.Values.TryGetValue(K(fieldId), out var v) && v is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    // ───────── 定義驗證 ─────────

    [Fact]
    public async Task Define_FillOnNonReferenceField_Rejected()
    {
        var w = await Build();
        var f = await MustAdd("dst", FieldType.Text, label: "一般");
        var r = await Update(f, new FieldConfig { Fill = new FillConfig(FillConfig.Default, [new FillMapping(w.SrcTerms, w.DstNote)]) });
        r.IsSuccess.Should().BeFalse();
        r.Errors.Should().ContainKey("Config.Fill");
    }

    [Fact]
    public async Task Define_LockTargetMustBeReadOnly()
    {
        await NewModule("src");
        var t = await MustAdd("src", FieldType.Text, label: "條件");
        await NewModule("dst");
        var open = await MustAdd("dst", FieldType.Text, label: "可編輯");
        var r = await AddField("dst", FieldType.Reference, RefTo("src") with
        {
            Fill = new FillConfig(FillConfig.Lock, [new FillMapping(t.Id, open.Id)])
        });
        r.IsCreated.Should().BeFalse();
        Err(r, "Config.Fill").Should().Contain("唯讀");
    }

    [Fact]
    public async Task Define_IncompatibleTypes_Rejected()
    {
        await NewModule("src");
        var t = await MustAdd("src", FieldType.Text, label: "文字");
        await NewModule("dst");
        var num = await MustAdd("dst", FieldType.Decimal, new FieldConfig { ReadOnly = true }, "數字");
        var r = await AddField("dst", FieldType.Reference, RefTo("src") with
        {
            Fill = new FillConfig(FillConfig.Lock, [new FillMapping(t.Id, num.Id)])
        });
        r.IsCreated.Should().BeFalse();
        Err(r, "Config.Fill").Should().Contain("型別");
    }

    [Fact]
    public async Task Define_SourceFieldMustBelongToTargetModule()
    {
        await NewModule("src");
        await NewModule("other");
        var foreign = await MustAdd("other", FieldType.Text, label: "別處");
        await NewModule("dst");
        var copy = await MustAdd("dst", FieldType.Text, new FieldConfig { ReadOnly = true }, "副本");
        var r = await AddField("dst", FieldType.Reference, RefTo("src") with
        {
            Fill = new FillConfig(FillConfig.Lock, [new FillMapping(foreign.Id, copy.Id)])
        });
        r.IsCreated.Should().BeFalse();
        r.Errors.Should().ContainKey("Config.Fill");
    }

    [Fact]
    public async Task Define_EnumCannotBeFilled()
    {
        await NewModule("src");
        var e1 = await MustAdd("src", FieldType.Enum, label: "類別");
        await NewModule("dst");
        var e2 = await MustAdd("dst", FieldType.Enum, new FieldConfig { ReadOnly = true }, "類別副本");
        var r = await AddField("dst", FieldType.Reference, RefTo("src") with
        {
            Fill = new FillConfig(FillConfig.Lock, [new FillMapping(e1.Id, e2.Id)])
        });
        r.IsCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Define_DuplicateTargetField_Rejected()
    {
        await NewModule("src");
        var a = await MustAdd("src", FieldType.Text, label: "A");
        var b = await MustAdd("src", FieldType.Text, label: "B");
        await NewModule("dst");
        var copy = await MustAdd("dst", FieldType.Text, new FieldConfig { ReadOnly = true }, "副本");
        var r = await AddField("dst", FieldType.Reference, RefTo("src") with
        {
            Fill = new FillConfig(FillConfig.Lock, [new FillMapping(a.Id, copy.Id), new FillMapping(b.Id, copy.Id)])
        });
        r.IsCreated.Should().BeFalse();
    }

    // ───────── 後端強制 ─────────

    [Fact]
    public async Task Create_WithReference_FillsFromTarget()
    {
        var w = await Build();
        var src = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var rec = await NewDst(w, src);
        Str(rec, w.DstName).Should().Be("甲公司");
        Str(rec, w.DstTerms).Should().Be("月結 30 天");
        rec.Values[K(w.DstAmount)]!.Value.GetDecimal().Should().Be(120);
    }

    [Fact]
    public async Task Lock_ClientSuppliedValueIsNotTrusted()
    {
        var w = await Build();
        var src = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var rec = await NewDst(w, src, new Dictionary<string, JsonElement?> { [K(w.DstName)] = J("\"偽造\"") });
        Str(rec, w.DstName).Should().Be("甲公司");
    }

    [Fact]
    public async Task Lock_WithoutReference_ClientCannotWriteTheCopyEither()
    {
        var w = await Build();
        var rec = await NewDst(w, null, new Dictionary<string, JsonElement?> { [K(w.DstName)] = J("\"偽造\"") });
        Str(rec, w.DstName).Should().BeNull();
    }

    [Fact]
    public async Task Snapshot_TargetChangeDoesNotFlowIntoExistingRecord()
    {
        var w = await Build();
        var src = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var rec = await NewDst(w, src);
        // 目標記錄日後被改名
        var srcRec = await Send(new Application.Dynamic.RecordService.Queries.GetRecord.GetRecordRequest(src));
        var s = (DynamicRecordResponse)srcRec.Data!;
        (await Send(new UpdateRecordRequest(src, new Dictionary<string, JsonElement?> { [K(w.SrcName)] = J("\"甲公司（改名）\"") }, s.RowVersion)))
            .IsSuccess.Should().BeTrue();

        // 再存一次本筆（參照沒變）：副本維持建立當下的值
        var saved = await Edit(rec, new Dictionary<string, JsonElement?> { [K(w.DstNote)] = J("\"備註\""), [K(w.RefId)] = J(src.ToString()) });
        saved.IsSuccess.Should().BeTrue(saved.Message);
        Str((DynamicRecordResponse)saved.Data!, w.DstName).Should().Be("甲公司");
    }

    [Fact]
    public async Task Update_ReferenceChanged_RefillsFromNewTarget()
    {
        var w = await Build();
        var a = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var b = await NewSrc(w, "乙公司", "現金", 80);
        var rec = await NewDst(w, a);
        var saved = await Edit(rec, new Dictionary<string, JsonElement?> { [K(w.RefId)] = J(b.ToString()) });
        saved.IsSuccess.Should().BeTrue(saved.Message);
        var after = (DynamicRecordResponse)saved.Data!;
        Str(after, w.DstName).Should().Be("乙公司");
        Str(after, w.DstTerms).Should().Be("現金");
    }

    [Fact]
    public async Task Update_ReferenceCleared_ClearsLockedCopies()
    {
        var w = await Build();
        var a = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var rec = await NewDst(w, a);
        var saved = await Edit(rec, new Dictionary<string, JsonElement?> { [K(w.RefId)] = null });
        saved.IsSuccess.Should().BeTrue(saved.Message);
        Str((DynamicRecordResponse)saved.Data!, w.DstName).Should().BeNull();
    }

    [Fact]
    public async Task Lock_SourceValueEmpty_ClearsTheCopy()
    {
        var w = await Build();
        var a = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var b = (await Send(new CreateRecordRequest("src", new Dictionary<string, JsonElement?> { [K(w.SrcName)] = J("\"乙公司\"") }))).Data as DynamicRecordResponse;
        var rec = await NewDst(w, a);
        var saved = await Edit(rec, new Dictionary<string, JsonElement?> { [K(w.RefId)] = J(b!.Id.ToString()) });
        saved.IsSuccess.Should().BeTrue(saved.Message);
        Str((DynamicRecordResponse)saved.Data!, w.DstTerms).Should().BeNull();
    }

    [Fact]
    public async Task Default_OnlyFillsEmptyFields_UserValueWins()
    {
        var w = await Build(FillConfig.Default, readOnlyTargets: false);
        var src = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var rec = await NewDst(w, src, new Dictionary<string, JsonElement?> { [K(w.DstTerms)] = J("\"自訂條件\"") });
        Str(rec, w.DstName).Should().Be("甲公司");
        Str(rec, w.DstTerms).Should().Be("自訂條件");
    }

    [Fact]
    public async Task Default_ReferenceChanged_RefillsUntouchedFieldsOnly()
    {
        var w = await Build(FillConfig.Default, readOnlyTargets: false);
        var a = await NewSrc(w, "甲公司", "月結 30 天", 120);
        var b = await NewSrc(w, "乙公司", "現金", 80);
        var rec = await NewDst(w, a);
        // 使用者自己改過條件，名稱沒動（送回與修改前相同的值）
        var saved = await Edit(rec, new Dictionary<string, JsonElement?>
        {
            [K(w.RefId)] = J(b.ToString()), [K(w.DstName)] = J("\"甲公司\""), [K(w.DstTerms)] = J("\"改過的條件\"")
        });
        saved.IsSuccess.Should().BeTrue(saved.Message);
        var after = (DynamicRecordResponse)saved.Data!;
        Str(after, w.DstName).Should().Be("乙公司");
        Str(after, w.DstTerms).Should().Be("改過的條件");
    }

    [Fact]
    public async Task Fill_DoesNotCopyFieldsTheUserCannotSee()
    {
        var w = await Build();
        var src = await NewSrc(w, "甲公司", "月結 30 天", 120);
        // 業務群組對 src 有 Read 但沒有金額權限 → 單價是隱藏欄位，不能被帶進副本
        var db = Db.Context;
        var sales = db.Set<PermissionGroup>().Single(g => g.Key == "sales");
        foreach (var key in new[] { "src", "dst" })
        {
            var code = (await Detail(key)).ProgramCode;
            db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = sales.Id, ProgramCode = code, CanRead = true, CanAdd = true, CanEdit = true, CanAmt = false
            });
        }
        await db.SaveChangesAsync();

        As("sales01");
        var rec = await NewDst(w, src);
        Str(rec, w.DstName).Should().Be("甲公司");
        // 回應已把金額欄位遮掉；改以 admin 讀回確認資料庫裡也沒有
        As("admin");
        var read = await Send(new Application.Dynamic.RecordService.Queries.GetRecord.GetRecordRequest(rec.Id));
        ((DynamicRecordResponse)read.Data!).Values.Should().NotContainKey(K(w.DstAmount));
    }

    [Fact]
    public async Task Fill_TargetRecordOutOfDataScope_NotCopied()
    {
        var w = await Build();
        var src = await NewSrc(w, "甲公司", "月結 30 天", 120); // 擁有者 admin
        var db = Db.Context;
        var sales = db.Set<PermissionGroup>().Single(g => g.Key == "sales");
        foreach (var key in new[] { "src", "dst" })
        {
            var m = await Detail(key);
            db.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
            {
                GroupId = sales.Id, ProgramCode = m.ProgramCode, CanRead = true, CanAdd = true, CanEdit = true, CanAmt = true
            });
            db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = sales.Id, Scope = DataScope.Own });
        }
        await db.SaveChangesAsync();

        As("sales01");
        var r = await Send(new CreateRecordRequest("dst", new Dictionary<string, JsonElement?> { [K(w.RefId)] = J(src.ToString()) }));
        // 參照驗證已擋下範圍外的目標，不會走到帶值
        r.IsSuccess.Should().BeFalse();
    }
}
