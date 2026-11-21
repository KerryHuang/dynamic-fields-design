using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.ApplicationTests.Dynamic.PermissionAxes;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// 「不提供給 AI」旗標的寫入規則（權限軸 §7.3、mcp1a 計畫判斷 4，Task 5）：新增帶 true、修改改變旗標要權限設定；
/// 不帶旗標、帶相同值不經 gate。persona 見 <see cref="AuthorizationBoundaryTestBase"/>：wang 只有設計器、chen 兩者都有。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class McpAiExclusionWriteRuleTests : AuthorizationBoundaryTestBase
{
    private Task<bool> Flag(long fieldId) =>
        Db.Context.Set<Field>().AsNoTracking().Where(f => f.Id == fieldId).Select(f => f.ExcludeFromAi).SingleAsync();

    private async Task<AddFieldRequest> AddRequest(bool exclude)
    {
        var m = await Detail(ModuleKey);
        return new AddFieldRequest(ModuleKey, m.Blocks[0].Id, FieldType.Decimal, Text("薪資"), new FieldConfig(), ExcludeFromAi: exclude);
    }

    private async Task<UpdateFieldRequest> UpdateRequest(bool? exclude)
    {
        var f = (await Detail(ModuleKey)).Blocks.SelectMany(b => b.Fields).Single(x => x.Id == Target);
        return new UpdateFieldRequest(f.Id, f.Type, Text("改名"), f.Config, f.BlockId, ExcludeFromAi: exclude);
    }

    [Fact]
    public async Task AddField_ExcludeTrue_NeedsPermissionAdmin()
    {
        var request = await AddRequest(true);
        As("wang");
        var denied = await Send(request);
        denied.IsForbidden.Should().BeTrue();
        denied.Code.Should().Be("permissionAdminRequired");
        (await Db.Context.Set<Field>().AsNoTracking().CountAsync(f => f.ModuleId == ModuleId)).Should().Be(3, "標題、目標、旁邊三個欄位，沒有新增");

        As("chen");
        var created = await Send(request);
        created.IsCreated.Should().BeTrue(created.Message);
        var field = (FieldResponse)created.Data!;
        field.ExcludeFromAi.Should().BeTrue();
        (await Flag(field.Id)).Should().BeTrue();

        As("wang");
        var plain = await Send(await AddRequest(false));
        plain.IsCreated.Should().BeTrue("不帶旗標照舊只要設計器新增");
        (await Flag(((FieldResponse)plain.Data!).Id)).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateField_ChangeFlag_NeedsPermissionAdmin()
    {
        As("wang");
        var denied = await Send(await UpdateRequest(true));
        denied.IsForbidden.Should().BeTrue();
        denied.Code.Should().Be("permissionAdminRequired");
        (await Flag(Target)).Should().BeFalse();

        As("chen");
        var ok = await Send(await UpdateRequest(true));
        ok.IsSuccess.Should().BeTrue(ok.Message);
        ((FieldResponse)ok.Data!).ExcludeFromAi.Should().BeTrue();
        (await Flag(Target)).Should().BeTrue();

        As("wang");
        (await Send(await UpdateRequest(false))).Code.Should().Be("permissionAdminRequired", "改回 false 也要權限設定");
        (await Flag(Target)).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateField_NullFlag_NoGate()
    {
        As("chen");
        (await Send(await UpdateRequest(true))).IsSuccess.Should().BeTrue();
        As("wang");
        var r = await Send(await UpdateRequest(null));
        r.IsSuccess.Should().BeTrue(r.Message);
        (await Flag(Target)).Should().BeTrue("null＝不改");
    }

    [Fact]
    public async Task UpdateField_SameValue_NoGate()
    {
        As("wang");
        (await Send(await UpdateRequest(false))).IsSuccess.Should().BeTrue();
        As("chen");
        (await Send(await UpdateRequest(true))).IsSuccess.Should().BeTrue();
        As("wang");
        var r = await Send(await UpdateRequest(true));
        r.IsSuccess.Should().BeTrue(r.Message);
        (await Flag(Target)).Should().BeTrue();
    }
}
