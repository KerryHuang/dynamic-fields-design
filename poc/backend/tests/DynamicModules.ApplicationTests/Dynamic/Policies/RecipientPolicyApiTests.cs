using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.Commands.DeleteGroup;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.ApplicationTests.Dynamic.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Policies;

/// <summary>收件人型別政策（A10、A11）：PUT 驗證存在且啟用、不出現在生效值、被引用的群組不能刪。</summary>
public class RecipientPolicyApiTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private static JsonElement Arr(params string[] items) => JsonSerializer.SerializeToElement(items);

    private Task<ServiceResult> PutSystem(PolicyDefinition def, params string[] items) =>
        Send(new UpdateSystemPolicyRequest(def.Code, Arr(items)));

    private Task<ServiceResult> PutModule(string module, PolicyDefinition def, params string[] items) =>
        Send(new UpdateModulePolicyRequest(module, def.Code, Arr(items)));

    private static string Error(ServiceResult r) => r.Errors["value"][0];

    [Fact]
    public async Task PutRecipients_UnknownGroup_Returns400ListingItem()
    {
        var res = await PutSystem(PolicyCatalog.ApprovalAlertRecipients, "admins", "group:99999");
        res.IsSuccess.Should().BeFalse();
        Error(res).Should().Contain("group:99999").And.NotContain("admins");
    }

    [Fact]
    public async Task PutRecipients_UnknownUser_Returns400()
    {
        var res = await PutSystem(PolicyCatalog.ApprovalAlertRecipients, "user:nobody");
        res.IsSuccess.Should().BeFalse();
        Error(res).Should().Contain("user:nobody");
    }

    [Fact]
    public async Task PutRecipients_DisabledUser_Returns400()
    {
        var u = await Db.Context.Set<AppUser>().SingleAsync(x => x.UserId == "fin1");
        u.IsActive = false;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        var res = await PutSystem(PolicyCatalog.ApprovalAlertRecipients, "user:fin1");
        res.IsSuccess.Should().BeFalse();
        Error(res).Should().Contain("user:fin1");
    }

    [Fact]
    public async Task PutRecipients_Valid_Persists()
    {
        var res = await PutSystem(PolicyCatalog.ApprovalAlertRecipients, "user:fin1", $"group:{FinanceGroup}", "admins");
        res.IsSuccess.Should().BeTrue(Dump(res));
        var row = await Db.Context.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == "policy.approval.alertRecipients");
        row.Value.Should().Be($"user:fin1,group:{FinanceGroup},admins");

        var r = await BuildPurchase();
        (await PutModule(r.Module, PolicyCatalog.ApprovalDefaultFallbackApprovers, $"group:{FinanceGroup}")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task EffectivePolicies_ExcludesRecipients()
    {
        var r = await BuildPurchase();
        var res = await Send(new GetEffectivePoliciesRequest(r.Module));
        res.IsSuccess.Should().BeTrue(Dump(res));
        var values = ((EffectivePoliciesResponse)res.Data!).Values;
        values.Keys.Should().NotContain("approval.defaultFallbackApprovers").And.NotContain("approval.alertRecipients");
        values.Keys.Should().Contain("approval.maxApprovers");
    }

    [Fact]
    public async Task DeleteGroup_ReferencedBySystemPolicy_Returns409()
    {
        (await PutSystem(PolicyCatalog.ApprovalAlertRecipients, $"group:{FinanceGroup}")).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteGroupRequest(FinanceGroup));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Contain(PolicyCatalog.ApprovalAlertRecipients.Code);
    }

    [Fact]
    public async Task DeleteGroup_ReferencedByModuleOverride_Returns409()
    {
        var r = await BuildPurchase();
        (await PutModule(r.Module, PolicyCatalog.ApprovalDefaultFallbackApprovers, $"group:{BoardGroup}")).IsSuccess.Should().BeTrue();
        var res = await Send(new DeleteGroupRequest(BoardGroup));
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Contain(PolicyCatalog.ApprovalDefaultFallbackApprovers.Code);
    }

    [Fact]
    public async Task DeleteGroup_PrefixLookalike_Succeeds()
    {
        // 政策引用 group:<id>1x 這類字首相同的另一個 id，刪掉 id 本身不能誤中
        var lookalike = FinanceGroup * 10 + 2;
        Db.Context.Add(new SystemSetting { Key = "policy.approval.alertRecipients", Value = $"group:{lookalike}" });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();

        (await Send(new DeleteGroupRequest(FinanceGroup))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteGroup_Unreferenced_Succeeds()
    {
        (await PutSystem(PolicyCatalog.ApprovalAlertRecipients, $"group:{FinanceGroup}")).IsSuccess.Should().BeTrue();
        (await Send(new DeleteGroupRequest(BoardGroup))).IsSuccess.Should().BeTrue();
    }
}
