using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>簽核人沒有模組讀取權時，帶 recordId 取得該筆的臨時讀取 schema（a4b 計畫 J4、簽核設計 §10.1）。</summary>
public class ApprovalGrantSchemaTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;

    /// <summary>草稿 → 已確認 需簽核，採購主管群組（buyer2）簽核；之後收回全部人的模組權限。</summary>
    private async Task<long> Rig()
    {
        _r = await BuildPurchase();
        await SeedFlow(_r, "po-a", _r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(_r.Module);
        As("buyer1");
        var po = await NewPo(_r);
        await MustSubmit(po.Id, _r.Confirmed);
        As("admin");
        await RevokeAll(_r.Module);
        return po.Id;
    }

    private async Task<ServiceResult> Schema(string user, long? recordId)
    {
        As(user);
        return await Send(new GetModuleSchemaRequest(_r.Module, recordId));
    }

    [Fact]
    public async Task Schema_NoRead_WithOpenTask_ReturnsGrantSchema()
    {
        var po = await Rig();

        var res = await Schema("buyer2", po);

        res.IsSuccess.Should().BeTrue(Dump(res));
        var schema = (ModuleSchemaResponse)res.Data!;
        schema.Permissions.Should().Be(new SchemaPermissions(true, false, false, false, false, true));
        schema.Workflow!.Transitions.Should().BeEmpty();
        schema.Details.Should().ContainSingle(d => d.ModuleKey == _r.Line).Which.Fields.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Schema_NoRead_RecordWithoutTask_Returns403()
    {
        var po = await Rig();
        (await Schema("emp1", po)).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Schema_NoRead_RecordOfOtherModule_Returns404()
    {
        await Rig();
        var other = await BuildPurchase("pb");
        As("admin");
        var otherPo = await NewPo(other);

        var res = await Schema("buyer2", otherPo.Id);

        res.IsNotFound.Should().BeTrue(Dump(res));
    }

    [Fact]
    public async Task Schema_NoRead_NoRecordId_Returns403()
    {
        await Rig();
        (await Schema("buyer2", null)).IsForbidden.Should().BeTrue();
    }

    [Fact]
    public async Task Schema_GrantHiddenField_NotInBlocks()
    {
        var po = await Rig();
        var group = new PermissionGroup { Key = "hide-memo", NameI18n = Text("hide-memo") };
        Db.Context.Add(group);
        await Db.Context.SaveChangesAsync();
        Db.Context.Add(new UserGroupMapping { GroupId = group.Id, UserId = "buyer2" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        As("admin");
        await HideFor(_r.Module, _r.Memo, group.Id);

        var schema = (ModuleSchemaResponse)(await Schema("buyer2", po)).Data!;

        schema.Blocks.SelectMany(b => b.Fields).Select(f => f.Id).Should().NotContain(_r.Memo).And.Contain(_r.DueDate);
    }

    [Fact]
    public async Task Schema_WithRead_RecordIdIgnored()
    {
        var po = await Rig();
        await GrantEveryone(_r.Module);

        var plain = (ModuleSchemaResponse)(await Schema("buyer2", null)).Data!;
        var withId = (ModuleSchemaResponse)(await Schema("buyer2", po)).Data!;

        JsonSerializer.Serialize(withId).Should().Be(JsonSerializer.Serialize(plain));
        withId.PermHash.Should().Be(plain.PermHash);
    }
}
