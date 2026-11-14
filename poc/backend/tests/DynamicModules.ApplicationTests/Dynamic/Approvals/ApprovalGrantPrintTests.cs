using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PrintService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核人臨時讀取的列印預覽（簽核設計 §10.1、§20.4 第 26 條；計畫 a3f、U7）。</summary>
public class ApprovalGrantPrintTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private const string Sheet = "po-sheet", Draft = "po-draft";
    private const string MemoText = "內部備註XYZ";

    private PurchaseRig _r = null!;
    private long _amount;
    private long _buyers;

    /// <summary>金額（Currency）欄位；buyers 群組有模組完整權限；finance 群組（fin1、fin2）預設沒有任何權限。流程一關 finance 或簽；兩個範本（一個停用）。</summary>
    private async Task Rig()
    {
        _r = await BuildPurchase();
        _amount = (await MustAdd(_r.Module, FieldType.Currency, label: "金額")).Id;
        await SeedFlow(_r, "po-g", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup))));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Plain(_r.Confirmed, _r.Cancelled)])).IsSuccess.Should().BeTrue();

        var buyers = new PermissionGroup { Key = "buyers", NameI18n = Text("buyers") };
        Db.Context.Add(buyers);
        await Db.Context.SaveChangesAsync();
        _buyers = buyers.Id;
        Db.Context.Add(new UserGroupMapping { GroupId = buyers.Id, UserId = "buyer1" });
        await Db.Context.SaveChangesAsync();
        var body = $"<h1>{{{{_title}}}}</h1><p>備註：{{{{f_{_r.Memo}}}}}</p><p>金額：{{{{f_{_amount}}}}}</p>" +
            $"<table><tbody>{{{{#each {_r.Line}}}}}<tr><td>數量{{{{f_{_r.LineQty}}}}}</td><td>單價{{{{f_{_r.LinePrice}}}}}</td></tr>{{{{/each}}}}</tbody></table>";
        var moduleId = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Module).Id;
        Db.Context.AddRange(
            new PrintTemplate { ModuleId = moduleId, Key = Sheet, NameI18n = Text("採購單"), Body = body, Active = true },
            new PrintTemplate { ModuleId = moduleId, Key = Draft, NameI18n = Text("草稿範本"), Body = "<p>x</p>", Active = false });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(buyers.Id, new PrivilegesDto(true, true, true, true, true, true, true), DataScope.All)], [], [], [])))).IsSuccess.Should().BeTrue();
    }

    private async Task Hide(string module, long field)
    {
        As("admin");
        (await Send(new SaveModulePermissionsRequest(module, new ModulePermissionsDto(
            module == _r.Module ? [new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, true), DataScope.All)] : [], [],
            [new FieldPermissionDto(field, FinanceGroup, FieldAccess.Hidden)], []))))
            .IsSuccess.Should().BeTrue();
    }

    private async Task<(long RecordId, long InstanceId)> Pending()
    {
        As("buyer1");
        var po = await NewPo(_r, qty: 7, price: 4321, extra: V((_r.Memo, MemoText), (_amount, 30000)));
        return (po.Id, (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value);
    }

    private Task<ServiceResult> Print(string user, long id)
    {
        As(user);
        return Send(new PrintRecordRequest(id, Sheet, "zh-TW"));
    }

    private static string Html(ServiceResult r) => ((PrintedDocument)r.Data!).Html;

    private Task<ServiceResult> Templates(string user, long? recordId, string? module = null)
    {
        As(user);
        return Send(new GetPrintTemplatesRequest(module ?? _r.Module, recordId));
    }

    [Fact]
    public async Task Print_Grant_NoModuleRead_200_AmountVisible_HiddenFieldBlank()
    {
        await Rig();
        await Hide(_r.Module, _r.Memo);
        var (po, _) = await Pending();

        var res = await Print("fin1", po);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Html(res).Should().Contain("30,000").And.NotContain(MemoText).And.Contain("備註：</p>");
    }

    [Fact]
    public async Task Print_Grant_DetailLoopUsesDetailGrant()
    {
        await Rig();
        await Hide(_r.Line, _r.LinePrice);
        var (po, _) = await Pending();

        var html = Html(await Print("fin1", po));

        html.Should().Contain("數量7").And.Contain("單價</td>").And.NotContain("4321");
    }

    [Fact]
    public async Task Print_HasReadNoPrintExport_OpenTask_200()
    {
        await Rig();
        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, true), DataScope.All),
             new GroupPermissionDto(FinanceGroup, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.All)], [], [], []))))
            .IsSuccess.Should().BeTrue();
        var (po, _) = await Pending();

        (await Print("fin1", po)).IsSuccess.Should().BeTrue();
        (await Templates("fin1", po)).IsSuccess.Should().BeTrue();
        (await Templates("fin1", null)).IsForbidden.Should().BeTrue();   // 沒帶 recordId 維持一般路徑的拒絕
    }

    [Fact]
    public async Task Print_AfterTaskEnds_403()
    {
        await Rig();
        var (po, id) = await Pending();
        (await Print("fin2", po)).IsSuccess.Should().BeTrue();
        As("fin1");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "fin1");
        (await Send(new ApproveTaskRequest(task.Id, Inst(id).RowVersion, null, null))).IsSuccess.Should().BeTrue();   // 或簽：同關另一份任務一起收回

        (await Print("fin1", po)).IsForbidden.Should().BeTrue();
        (await Print("fin2", po)).IsForbidden.Should().BeTrue();
        (await Templates("fin2", po)).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Print_AfterTransfer_OldAssignee403()
    {
        await Rig();
        var (po, id) = await Pending();
        As("fin1");
        var task = TasksOf(id).Single(t => t.AssigneeUserId == "fin1");
        (await Send(new TransferTaskRequest(task.Id, Inst(id).RowVersion, "emp1", null))).IsSuccess.Should().BeTrue();

        (await Print("fin1", po)).IsForbidden.Should().BeTrue();
        (await Templates("fin1", po)).IsForbidden.Should().BeTrue();
        (await Print("emp1", po)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Print_DelegatedOriginal_200()
    {
        await Rig();
        var (po, id) = await Pending();
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id && t.AssigneeUserId == "fin1")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeUserId, "emp2").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));

        (await Print("fin1", po)).IsSuccess.Should().BeTrue();   // 本人
        (await Print("emp2", po)).IsSuccess.Should().BeTrue();   // 代理人
        (await Print("emp1", po)).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Print_OtherRecordSameModule_403()
    {
        await Rig();
        await Pending();
        As("buyer1");
        var other = await NewPo(_r, price: 100);

        (await Print("fin1", other.Id)).IsForbidden.Should().BeTrue();
        (await Templates("fin1", other.Id)).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task PrintTemplates_RecordIdOfOtherModuleOrMissing_403()
    {
        await Rig();
        await Pending();
        As("admin");
        var b = await BuildPurchase("pb");
        var otherModuleRecord = await NewPo(b);

        (await Templates("fin1", otherModuleRecord.Id)).IsForbidden.Should().BeTrue();
        (await Templates("fin1", 9_999_999)).IsForbidden.Should().BeTrue();
        (await Print("fin1", 9_999_999)).IsNotFound.Should().BeTrue();   // 與改動前相同：記錄不存在
    }

    [Fact]
    public async Task PrintTemplates_RecordId_GrantPath_ListsActiveOnly()
    {
        await Rig();
        var (po, _) = await Pending();

        var res = await Templates("fin1", po);

        res.IsSuccess.Should().BeTrue(Dump(res));
        ((List<PrintTemplateOption>)res.Data!).Select(t => t.Key).Should().Equal(Sheet);
    }

    [Fact]
    public async Task Print_NormalPath_Unchanged()
    {
        await Rig();
        var (po, _) = await Pending();

        var res = await Print("buyer1", po);

        res.IsSuccess.Should().BeTrue(Dump(res));
        Html(res).Should().Contain(MemoText).And.Contain("30,000").And.Contain("單價4321");
        (await Print("emp1", po)).IsForbidden.Should().BeTrue();   // 沒有任何權限、也不是可動作者
        ((List<PrintTemplateOption>)(await Templates("buyer1", null)).Data!).Should().ContainSingle();
    }
}
