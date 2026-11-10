using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 簽核測試共用：設計 §17.1 的組織與群組，以及 §17.4／§17.5 的單據情境（業務名詞只出現在測試資料裡）。
/// 使用者 gm、mgrA、emp1、emp2、lead1、emp3、buyer1、buyer2、fin1、fin2；部門 hq、sales、sales-1、purchasing、finance；
/// 群組 finance、purchase-lead、board。
/// </summary>
public abstract class ApprovalTestBase : EffectsTestBase, IAsyncLifetime
{
    protected long FinanceGroup, PurchaseLeadGroup, BoardGroup;

    protected static readonly DateTime SeedTime = new(2026, 11, 4, 1, 0, 0);

    /// <summary>重新實作 IAsyncLifetime，讓 xUnit 先跑基底的初始化再建組織。</summary>
    public new async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedOrganizationAsync();
    }

    private async Task SeedOrganizationAsync()
    {
        string[] users = ["gm", "mgrA", "emp1", "emp2", "lead1", "emp3", "buyer1", "buyer2", "fin1", "fin2"];
        foreach (var u in users) Db.Context.Add(new AppUser { UserId = u, DisplayName = u });
        var groups = new Dictionary<string, string[]>
        {
            ["finance"] = ["fin1", "fin2"], ["purchase-lead"] = ["buyer2"], ["board"] = ["gm"]
        };
        var created = groups.Keys.ToDictionary(k => k, k => new PermissionGroup { Key = k, NameI18n = Text(k) });
        Db.Context.AddRange(created.Values);
        await Db.Context.SaveChangesAsync();
        foreach (var (k, members) in groups)
            foreach (var m in members) Db.Context.Add(new UserGroupMapping { GroupId = created[k].Id, UserId = m });

        Department Dept(string code, long? parent, string? manager) =>
            new() { Code = code, NameI18n = Text(code), ParentId = parent, ManagerUserId = manager, CreatedUserId = "admin", CreatedTime = SeedTime };
        var hq = Dept("hq", null, "gm");
        Db.Context.Add(hq);
        await Db.Context.SaveChangesAsync();
        var sales = Dept("sales", hq.Id, "mgrA");
        var purchasing = Dept("purchasing", hq.Id, null);
        var finance = Dept("finance", hq.Id, "fin2");
        Db.Context.AddRange(sales, purchasing, finance);
        await Db.Context.SaveChangesAsync();
        var sales1 = Dept("sales-1", sales.Id, "lead1");
        Db.Context.Add(sales1);
        await Db.Context.SaveChangesAsync();
        foreach (var (u, d) in new[]
                 {
                     ("gm", hq), ("mgrA", sales), ("emp1", sales), ("emp2", sales), ("lead1", sales1), ("emp3", sales1),
                     ("buyer1", purchasing), ("buyer2", purchasing), ("fin1", finance), ("fin2", finance)
                 })
            Db.Context.Add(new UserDepartment { UserId = u, DepartmentId = d.Id });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (FinanceGroup, PurchaseLeadGroup, BoardGroup) = (created["finance"].Id, created["purchase-lead"].Id, created["board"].Id);
    }

    // ───────── 單據情境 ─────────

    /// <summary>
    /// 單據 {p}（狀態 草稿／已確認／已取消／部分進貨／已進貨／已退回／變更中）；明細 {p}-line（數量、單價、小計 = 數量 × 單價）；
    /// 追加費用 {p}-extra（以參照指向單據，金額）；供應商主檔 {p}-vendor（名稱）。
    /// 單據的總額 = 明細小計彙總 + 追加費用彙總；折扣後總額 = 總額 − 折扣；供應商參照以 lock 帶入供應商名稱。
    /// 狀態機：草稿 → 已確認 → 部分進貨 → 已進貨、已確認 → 已取消，全部不需簽核。
    /// </summary>
    protected sealed record PurchaseRig(
        string Module, long ModuleId, long Status,
        long Draft, long Confirmed, long Cancelled, long Partial, long Received, long Returned, long Changing,
        long Total, long LineSum, long ExtraSum, long Discount, long NetTotal, long DueDate, long Memo,
        long Supplier, long SupplierName, long Owner, long No,
        string Line, long LineHead, long LineQty, long LinePrice, long LineSubtotal,
        string Extra, long ExtraRef, long ExtraAmount, string Vendor, long VendorName);

    protected async Task<PurchaseRig> BuildPurchase(string p = "po")
    {
        await NewModule($"{p}-vendor");
        var vendorName = await MustAdd($"{p}-vendor", FieldType.Text, label: "名稱");
        (await Send(new ActivateModuleRequest($"{p}-vendor"))).IsSuccess.Should().BeTrue();

        await NewModule(p);
        var status = await MustAdd(p, FieldType.Enum, label: "狀態");
        long Opt(string label) => ((OptionResponse)Send(new AddOptionRequest(status.Id, Text(label), null)).GetAwaiter().GetResult().Data!).Id;
        var draft = Opt("草稿");
        var confirmed = Opt("已確認");
        var cancelled = Opt("已取消");
        var partial = Opt("部分進貨");
        var received = Opt("已進貨");
        var returned = Opt("已退回");
        var changing = Opt("變更中");
        var memo = await MustAdd(p, FieldType.Text, label: "備註");
        var due = await MustAdd(p, FieldType.Date, label: "交期");
        var discount = await MustAdd(p, FieldType.Decimal, new FieldConfig { Scale = 0 }, "折扣");
        var supplierName = await MustAdd(p, FieldType.Text, new FieldConfig { ReadOnly = true }, "供應商名稱");
        var supplier = await MustAdd(p, FieldType.Reference,
            RefTo($"{p}-vendor") with { Fill = new FillConfig(FillConfig.Lock, [new FillMapping(vendorName.Id, supplierName.Id)]) }, "供應商");
        var owner = await MustAdd(p, FieldType.User, label: "負責人");
        var no = await MustAdd(p, FieldType.AutoNumber, new FieldConfig { Format = "P{0000}" }, "單號");

        await NewModule($"{p}-line");
        var head = await MustAdd($"{p}-line", FieldType.Reference, RefTo(p, ReferenceRelation.Detail), "表頭");
        var qty = await MustAdd($"{p}-line", FieldType.Decimal, new FieldConfig { Scale = 0 }, "數量");
        var price = await MustAdd($"{p}-line", FieldType.Decimal, new FieldConfig { Scale = 0 }, "單價");
        var subtotal = await MustAdd($"{p}-line", FieldType.Formula,
            new FieldConfig { Formula = $"{{{qty.Id}}} * {{{price.Id}}}", ResultType = "number", Scale = 0 }, "小計");
        (await Send(new ActivateModuleRequest($"{p}-line"))).IsSuccess.Should().BeTrue();

        await NewModule($"{p}-extra");
        var extraRef = await MustAdd($"{p}-extra", FieldType.Reference, RefTo(p), "單據");
        var amount = await MustAdd($"{p}-extra", FieldType.Decimal, new FieldConfig { Scale = 0 }, "金額");
        (await Send(new ActivateModuleRequest($"{p}-extra"))).IsSuccess.Should().BeTrue();

        var lineSum = await MustAdd(p, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(head.Id, RollupFunction.Sum, subtotal.Id, null) }, "明細合計");
        var extraSum = await MustAdd(p, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(extraRef.Id, RollupFunction.Sum, amount.Id, null) }, "追加費用合計");
        var total = await MustAdd(p, FieldType.Formula,
            new FieldConfig { Formula = $"{{{lineSum.Id}}} + {{{extraSum.Id}}}", ResultType = "number", Scale = 0 }, "總額");
        var net = await MustAdd(p, FieldType.Formula,
            new FieldConfig { Formula = $"{{{total.Id}}} - {{{discount.Id}}}", ResultType = "number", Scale = 0 }, "折扣後總額");
        (await Send(new ActivateModuleRequest(p))).IsSuccess.Should().BeTrue();

        var rig = new PurchaseRig(p, (await Detail(p)).Id, status.Id, draft, confirmed, cancelled, partial, received, returned, changing,
            total.Id, lineSum.Id, extraSum.Id, discount.Id, net.Id, due.Id, memo.Id, supplier.Id, supplierName.Id, owner.Id, no.Id,
            $"{p}-line", head.Id, qty.Id, price.Id, subtotal.Id, $"{p}-extra", extraRef.Id, amount.Id, $"{p}-vendor", vendorName.Id);
        (await SaveWorkflow(rig, BaseTransitions(rig))).IsSuccess.Should().BeTrue();
        return rig;
    }

    protected static List<WorkflowTransition> BaseTransitions(PurchaseRig r) =>
    [
        new(r.Draft, r.Confirmed, []), new(r.Confirmed, r.Partial, []), new(r.Partial, r.Received, []), new(r.Confirmed, r.Cancelled, [])
    ];

    protected static List<WorkflowState> AllStates(PurchaseRig r, params long[] editable) =>
        new[] { r.Draft, r.Confirmed, r.Cancelled, r.Partial, r.Received, r.Returned, r.Changing }
            .Select(id => new WorkflowState(id, Editable: editable.Contains(id))).ToList();

    protected Task<ServiceResult> SaveWorkflow(PurchaseRig r, List<WorkflowTransition> transitions,
        List<WorkflowState>? states = null, long? initial = null) =>
        Send(new SaveWorkflowRequest(r.Module, new WorkflowInput(r.Status, initial ?? r.Draft, states ?? AllStates(r), transitions)));

    protected static WorkflowTransition Plain(long from, long to) => new(from, to, []);

    protected static WorkflowTransition Needs(long from, long to, string noMatch = ApprovalNoMatch.Block) =>
        new(from, to, [], new TransitionApproval(true, noMatch));

    // ───────── 流程定義 ─────────

    protected static JsonElement Cond(long field, string op, object value) =>
        JsonSerializer.SerializeToElement(new { all = new[] { new { field, op, value } } });

    protected static ApprovalStageDef Stage(string code, ApproverSource approvers, JsonElement? when = null,
        string mode = ApprovalModes.Any, List<long>? editable = null, int? dueHours = null, string onEmpty = ApprovalOnEmpty.Fallback) =>
        new(code, Text(code), when, approvers, mode, editable ?? [], dueHours, onEmpty);

    protected static ApproverSource Group(params long[] ids) => new(ApprovalSources.Group, GroupIds: [.. ids]);
    protected static ApproverSource Manager(int level) => new(ApprovalSources.Manager, Level: level);
    protected static ApproverSource Users(params string[] ids) => new(ApprovalSources.Users, UserIds: [.. ids]);

    protected static ApprovalDefinition Def(JsonElement? when, params ApprovalStageDef[] stages) =>
        new(ApprovalShapes.Sequence, when, null, null, [.. stages]);

    /// <summary>直接在 Db 建流程與版本 1（不經過驗證），回傳流程。</summary>
    protected async Task<ApprovalFlow> SeedFlow(PurchaseRig r, string key, long to, int priority, ApprovalDefinition d, bool active = true)
    {
        var flow = new ApprovalFlow
        {
            Key = key, NameI18n = Text(key), ModuleId = r.ModuleId, ToOptionId = to, Priority = priority, Active = active,
            CreatedUserId = "admin", CreatedTime = SeedTime
        };
        Db.Context.Add(flow);
        await Db.Context.SaveChangesAsync();
        var version = new ApprovalFlowVersion { FlowId = flow.Id, Version = 1, Definition = d, CreatedBy = "admin", CreatedTime = SeedTime };
        Db.Context.Add(version);
        await Db.Context.SaveChangesAsync();
        flow.CurrentVersionId = version.Id;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return flow;
    }

    /// <summary>直接在 Db 建一個綁定這個流程目前版本的實例（status 預設 pending）；snapshotOnly 時只出現在快照索引。</summary>
    protected async Task<ApprovalInstance> SeedInstance(PurchaseRig r, ApprovalFlow flow, string status = ApprovalInstanceStatus.Pending,
        long recordId = 1, bool snapshotOnly = false, long? versionId = null)
    {
        var vid = versionId ?? Db.Context.Set<ApprovalFlow>().AsNoTracking().Single(f => f.Id == flow.Id).CurrentVersionId!.Value;
        var inst = new ApprovalInstance
        {
            RecordId = recordId, ModuleId = r.ModuleId, FlowId = snapshotOnly ? null : flow.Id, FlowVersionId = snapshotOnly ? null : vid,
            RouteKind = snapshotOnly ? ApprovalRouteKind.Fallback : ApprovalRouteKind.Flow,
            FromOptionId = r.Draft, ToOptionId = flow.ToOptionId, InitiatorUserId = "buyer1", Status = status, TitleSnapshot = "x",
            SubmittedTime = SeedTime, RowVersion = Guid.NewGuid().ToString(), OperationId = Guid.NewGuid().ToString("N")
        };
        Db.Context.Add(inst);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new ApprovalInstanceFlow { InstanceId = inst.Id, FlowId = flow.Id, FlowVersionId = vid });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        return inst;
    }
}
