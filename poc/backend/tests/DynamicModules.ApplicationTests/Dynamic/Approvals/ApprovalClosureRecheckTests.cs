using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>改公式、彙總、帶值後重檢閉包交集（設計 §9.2）：可編輯欄位不能落進流程條件的上游閉包；鎖定帶值不能指向可編輯欄位。</summary>
public class ApprovalClosureRecheckTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _approved, _remark, _ref2;

    private long BlockOf(long fieldId) => Db.Context.Set<Field>().AsNoTracking().Single(f => f.Id == fieldId).BlockId;

    /// <summary>草稿 → 已確認 需簽核，流程條件引用 conditionOf；一關，可編輯欄位為核可金額、核簽備註、備用供應商。</summary>
    private async Task Rig(Func<PurchaseRig, long>? conditionOf = null)
    {
        _r = await BuildPurchase();
        _approved = (await MustAdd(_r.Module, FieldType.Decimal, new FieldConfig { Scale = 0 }, "核可金額")).Id;
        _remark = (await MustAdd(_r.Module, FieldType.Text, new FieldConfig { ReadOnly = true }, "核簽備註")).Id;   // 鎖定帶值的目的欄位必須唯讀
        _ref2 = (await MustAdd(_r.Module, FieldType.Reference, RefTo(_r.Vendor), "備用供應商")).Id;
        var cond = Cond(conditionOf?.Invoke(_r) ?? _r.NetTotal, "gte", 0);
        await SeedFlow(_r, "po-a", _r.Confirmed, 1,
            Def(cond, Stage("lead", Group(PurchaseLeadGroup), editable: [_approved, _remark, _ref2])));
        var wf = await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Draft)]);
        wf.IsSuccess.Should().BeTrue(Dump(wf));
        await GrantEveryone(_r.Module, _r.Extra);
    }

    private Task<ServiceResult> SetFormula(long fieldId, string formula) =>
        Send(new UpdateFieldRequest(fieldId, FieldType.Formula, Text("公式"),
            new FieldConfig { Formula = formula, ResultType = "number", Scale = 0 }, BlockOf(fieldId)));

    private static void Rejected(ServiceResult res, params string[] contains)
    {
        res.IsInvalid.Should().BeTrue(Dump(res));
        foreach (var c in contains) Dump(res).Should().Contain(c);
    }

    private Dictionary<long, string> RowVersions() =>
        Db.Context.Set<Record>().AsNoTracking().ToDictionary(x => x.Id, x => x.RowVersion);

    [Fact]
    public async Task UpdateFormula_PullsEditableFieldIntoClosure_Returns400()
    {
        await Rig();
        Rejected(await SetFormula(_r.NetTotal, $"{{{_r.Total}}} - {{{_r.Discount}}} + {{{_approved}}}"), "flows[po-a]", "核可金額");
    }

    [Fact]
    public async Task UpdateFill_MapsIntoConditionField_FromEditableReference_Returns400()
    {
        await Rig(r => r.SupplierName);
        var res = await Send(new UpdateFieldRequest(_ref2, FieldType.Reference, Text("備用供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Default, [new FillMapping(_r.VendorName, _r.SupplierName)]) }, BlockOf(_ref2)));
        Rejected(res, "flows[po-a]", "備用供應商");
    }

    [Fact]
    public async Task ReactivateFormulaField_CreatingIntersection_Returns400()
    {
        await Rig();
        var extra = await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_approved}}} * 2", ResultType = "number", Scale = 0 }, "加倍");
        (await Deactivate(extra.Id)).IsSuccess.Should().BeTrue();
        // 停用期間讓淨額引用它（直接寫資料，繞過「不能引用停用欄位」的檢查）
        var net = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _r.NetTotal);
        net.Config = net.Config with { Formula = $"{{{_r.Total}}} - {{{_r.Discount}}} + {{{extra.Id}}}" };
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        Rejected(await Send(new SetFieldStatusRequest(extra.Id, FieldStatus.Active)), "flows[po-a]", "核可金額");
    }

    [Fact]
    public async Task UpdateFormula_NoIntersection_Succeeds()
    {
        await Rig();
        var res = await SetFormula(_r.NetTotal, $"{{{_r.Total}}} - {{{_r.Discount}}} * 2");
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task UpdateFormula_ModuleWithoutApproval_SkipsCheck()
    {
        var plain = await BuildPurchase("pl");   // 沒有狀態機
        var extra = await MustAdd(plain.Module, FieldType.Decimal, new FieldConfig { Scale = 0 }, "數字");
        var res = await Send(new UpdateFieldRequest(plain.NetTotal, FieldType.Formula, Text("公式"),
            new FieldConfig { Formula = $"{{{plain.Total}}} + {{{extra.Id}}}", ResultType = "number", Scale = 0 }, BlockOf(plain.NetTotal)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    /// <summary>既有的違規（舊資料或繞過 API 寫入）不算這次的錯：只報這次改動新增的。</summary>
    [Fact]
    public async Task UpdateFormula_PreexistingViolation_NotReported()
    {
        await Rig();
        var version = await Db.Context.Set<ApprovalFlowVersion>().SingleAsync();
        version.Definition = version.Definition with
        {
            Stages = [version.Definition.Stages![0] with { EditableFields = [_r.Discount] }]   // 折扣本來就在條件閉包內
        };
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var res = await SetFormula(_r.NetTotal, $"{{{_r.Total}}} - {{{_r.Discount}}} * 2");
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task UpdateFormula_Intersection_NoRecalcPerformed()
    {
        await Rig();
        As("buyer1");
        await NewPo(_r, extra: V((_r.Discount, 100)));
        As("admin");
        var before = RowVersions();
        Rejected(await SetFormula(_r.NetTotal, $"{{{_r.Total}}} + {{{_approved}}}"));
        RowVersions().Should().BeEquivalentTo(before);
        Db.Context.Set<Field>().AsNoTracking().Single(f => f.Id == _r.NetTotal).Config.Formula.Should().NotContain($"{_approved}");
    }

    /// <summary>換上去比對的設定在回傳前還原，不留在追蹤中的欄位實體上。</summary>
    [Fact]
    public async Task RejectNew_RestoresFieldConfigAndStatus()
    {
        await Rig();
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Module, true, CancellationToken.None))!;
        var net = module.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == _r.NetTotal);
        var original = net.Config;
        var res = await sp.GetRequiredService<ApprovalClosureRecheck>().RejectNewAsync(
            module, net, original with { Formula = $"{{{_r.Total}}} + {{{_approved}}}" }, FieldStatus.Active, CancellationToken.None);
        res.Should().NotBeNull();
        net.Config.Should().BeSameAs(original);
        net.Status.Should().Be(FieldStatus.Active);
    }

    // ───────── 鎖定帶值指向可編輯欄位 ─────────

    [Fact]
    public async Task AddRefField_LockFillIntoEditableField_Returns400()
    {
        await Rig();
        var res = await Send(new AddFieldRequest(_r.Module, BlockOf(_r.Memo), FieldType.Reference, Text("第三供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(_r.VendorName, _remark)]) }));
        Rejected(res, "Config.Fill", "核簽備註");
    }

    [Fact]
    public async Task UpdateFill_LockModeIntoEditableField_Returns400()
    {
        await Rig();
        var res = await Send(new UpdateFieldRequest(_ref2, FieldType.Reference, Text("備用供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(_r.VendorName, _remark)]) }, BlockOf(_ref2)));
        Rejected(res, "Config.Fill", "核簽備註");
    }

    /// <summary>可編輯欄位只在舊版本的流程定義裡：實例可能還綁著舊版，所以任何版本都算。</summary>
    [Fact]
    public async Task UpdateFill_LockModeIntoFieldEditableOnlyInOldVersion_Returns400()
    {
        await Rig();
        var flow = await Db.Context.Set<ApprovalFlow>().SingleAsync(f => f.Key == "po-a");
        var v2 = new ApprovalFlowVersion
        {
            FlowId = flow.Id, Version = 2, Definition = Def(Cond(_r.NetTotal, "gte", 0), Stage("lead", Group(PurchaseLeadGroup))),
            CreatedBy = "admin", CreatedTime = SeedTime
        };
        Db.Context.Add(v2);
        await Db.Context.SaveChangesAsync();
        flow.CurrentVersionId = v2.Id;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var res = await Send(new UpdateFieldRequest(_ref2, FieldType.Reference, Text("備用供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(_r.VendorName, _remark)]) }, BlockOf(_ref2)));
        Rejected(res, "Config.Fill", "核簽備註");
    }

    /// <summary>預設帶值（default）不鎖欄位，指向可編輯欄位沒有衝突。</summary>
    [Fact]
    public async Task UpdateFill_DefaultModeIntoEditableField_Succeeds()
    {
        await Rig();
        var res = await Send(new UpdateFieldRequest(_ref2, FieldType.Reference, Text("備用供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Default, [new FillMapping(_r.VendorName, _remark)]) }, BlockOf(_ref2)));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task AddRefField_LockFillIntoNonEditableField_Succeeds()
    {
        await Rig();
        var memo = (await MustAdd(_r.Module, FieldType.Text, new FieldConfig { ReadOnly = true }, "唯讀備註")).Id;   // 不在任何一關的可編輯欄位
        var res = await Send(new AddFieldRequest(_r.Module, BlockOf(_r.Memo), FieldType.Reference, Text("第三供應商"),
            RefTo(_r.Vendor) with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(_r.VendorName, memo)]) }));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }
}
