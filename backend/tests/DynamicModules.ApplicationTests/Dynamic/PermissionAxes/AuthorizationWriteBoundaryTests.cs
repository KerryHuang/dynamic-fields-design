using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 授權寫入邊界的負向案例中涉及攔截器與批次入口、要兩個 provider 行為相同的四條（權限軸設計 §7.4、§18）：
/// PAX-23b（漏宣告的追蹤式寫入）、PAX-23c（Cleanup 許可的列層與結束檢查）、PAX-23e（沒有 SystemAdminVerified 的系統代碼列與成員、批次入口拒絕系統代碼）、
/// PAX-23f（沒有許可時放寬 VisibilityRestricted）。PAX-23k：這個類別在雙跑集，PostgreSQL 再跑一次。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class AuthorizationWriteBoundaryTests : AuthorizationBoundaryTestBase
{
    [Fact]
    public async Task PAX23b_ForgotToDeclare_TrackedWrite_Throws_RowsUnchanged()
    {
        var before = await GuardRowsAsync();
        As("admin");
        var act = () => Send(new ForgotToDeclareRequest(ModuleId, Target, GroupX, ForgotMode.TrackedAdd));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*授權資料的寫入被拒*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);
    }

    [Theory]
    [InlineData(CleanupMode.AddRow, "*清除許可只准刪除*")]
    [InlineData(CleanupMode.DeleteOtherFieldRow, "*只准刪除屬於目標*")]
    [InlineData(CleanupMode.PurgeWithoutDeletingField, "*目標欄位仍存在*")]
    public async Task PAX23c_CleanupPermit_OnlyDeletesTargetRows_AndTargetMustBeGone(CleanupMode mode, string message)
    {
        var before = await GuardRowsAsync();
        var act = () => Send(new CleanupProbeRequest(Target, Other, GroupX, mode));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        Db.Context.ChangeTracker.Clear();
        // 整筆回滾：PurgeFieldsAsync 已經批次刪掉的目標欄位權限列也回來了
        (await GuardRowsAsync()).Should().Equal(before);
        (await Db.Context.Set<FieldPermission>().AsNoTracking().CountAsync(p => p.FieldId == Target)).Should().Be(1);
    }

    [Fact]
    public async Task CleanupPurgeModule_IgnoresCallerProgramCode_SystemRowsSurvive()
    {
        var systemRows = await SystemCodeRowsAsync();
        systemRows.Should().NotBeEmpty();
        await Send(new ForgedPurgeRequest(ModuleId, Target, Other, ForgedPurgeMode.ModuleWithSystemCode));
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<Module>().AsNoTracking().AnyAsync(m => m.Id == ModuleId)).Should().BeFalse("目標模組確實刪除");
        (await SystemCodeRowsAsync()).Should().Equal(systemRows);
    }

    [Fact]
    public async Task CleanupPurgeFields_IgnoresCallerOptions_OtherFieldOptionScopesSurvive()
    {
        var db = Db.Context;
        var (optionA, optionB) = (new FieldOption { FieldId = Target, Sequence = 1, Active = true }, new FieldOption { FieldId = Other, Sequence = 1, Active = true });
        db.Set<FieldOption>().AddRange(optionA, optionB);
        await db.SaveChangesAsync();
        db.Set<FieldOptionScope>().AddRange(new FieldOptionScope { OptionId = optionA.Id, GroupId = GroupX }, new FieldOptionScope { OptionId = optionB.Id, GroupId = GroupX });
        await db.SaveForTestsAsync();
        db.ChangeTracker.Clear();

        await Send(new ForgedPurgeRequest(ModuleId, Target, Other, ForgedPurgeMode.FieldWithOtherOptions));
        db.ChangeTracker.Clear();
        (await db.Set<Field>().AsNoTracking().AnyAsync(f => f.Id == Target)).Should().BeFalse("目標欄位確實刪除");
        (await db.Set<FieldOptionScope>().AsNoTracking().AnyAsync(s => s.OptionId == optionB.Id)).Should().BeTrue("旁邊欄位的選項限制不受影響");
        (await db.Set<FieldOptionScope>().AsNoTracking().AnyAsync(s => s.OptionId == optionA.Id)).Should().BeFalse("目標欄位的選項限制隨欄位清掉");
    }

    [Fact]
    public async Task CleanupPurgeModule_ModuleCarryingSystemCode_IsRejected_RowsUnchanged()
    {
        // 資料庫裡的模組 program 代碼若是系統代碼（只可能是資料毀損），清除一律拒絕、整筆回滾
        var module = await Db.Context.Set<Module>().SingleAsync(m => m.Id == ModuleId);
        module.ProgramCode = SystemProgramCodes.PermissionAdmin;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        var before = await GuardRowsAsync();

        var act = () => Send(new ForgedPurgeRequest(ModuleId, Target, Other, ForgedPurgeMode.ModuleWithSystemCode));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*系統權限代碼*不能經模組清除刪除*");
        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);
        (await Db.Context.Set<Module>().AsNoTracking().AnyAsync(m => m.Id == ModuleId)).Should().BeTrue();
    }

    private async Task<List<string>> SystemCodeRowsAsync() =>
        (await GuardRowsAsync()).Where(r => r.Contains(SystemProgramCodes.PermissionAdmin) || r.Contains(SystemProgramCodes.Designer)).ToList();

    [Theory]
    [InlineData(AdminProbeMode.SystemCodeRow, "*系統權限代碼的授權列只准在系統管理員請求內寫*")]
    [InlineData(AdminProbeMode.Membership, "*群組成員只准在系統管理員請求內寫*")]
    [InlineData(AdminProbeMode.SystemCodeBatch, "*系統權限代碼*不能經模組權限存檔寫入*")]
    public async Task PAX23e_PermissionAdminWithoutSystemAdmin_CannotWriteSystemRowsOrMembers(AdminProbeMode mode, string message)
    {
        var before = await GuardRowsAsync();
        As("li");
        var act = () => Send(new PermissionAdminProbeRequest(ModuleId, GroupX, mode));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task PAX23e_SystemCodeBatch_IsRejected_EvenWithSystemAdminVerified()
    {
        var before = await GuardRowsAsync();
        As("admin");
        var act = () => Send(new SystemAdminProbeRequest(ModuleId, GroupX, AdminProbeMode.SystemCodeBatch));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*系統權限代碼*不能經模組權限存檔寫入*");
        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);

        // 對照：同一個 admin 的系統管理員請求（許可帶 SystemAdminVerified）寫成員就放行，可見上面擋下的是批次入口本身
        (await Send(new SystemAdminProbeRequest(ModuleId, GroupX, AdminProbeMode.Membership))).IsSuccess.Should().BeTrue();
        (await Db.Context.Set<UserGroupMapping>().AsNoTracking().AnyAsync(m => m.GroupId == GroupX && m.UserId == "wang")).Should().BeTrue();
    }

    [Fact]
    public async Task PAX23f_WithoutPermit_RelaxingVisibilityRestricted_Throws()
    {
        await SetVisibilityRestrictedAsync(Target, true);

        // 追蹤式：true → false 後存檔
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == Target);
        field.VisibilityRestricted = false;
        var tracked = () => Db.Context.SaveChangesAsync();
        await tracked.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dynamic_Field*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();
        (await VisibilityRestrictedAsync(Target)).Should().BeTrue();

        // 離線實體 Update：資料庫裡本來就是 false 的欄位，Update 把每個屬性都標成已修改，不靠原值一樣擋（寧可誤擋）
        (await VisibilityRestrictedAsync(Other)).Should().BeFalse();
        var detached = await Db.Context.Set<Field>().AsNoTracking().SingleAsync(f => f.Id == Other);
        Db.Context.Update(detached);
        var offline = () => Db.Context.SaveChangesAsync();
        await offline.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dynamic_Field*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();

        // 收緊（false → true）不經守衛
        var other = await Db.Context.Set<Field>().SingleAsync(f => f.Id == Other);
        other.VisibilityRestricted = true;
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await VisibilityRestrictedAsync(Other)).Should().BeTrue();
    }

    // ── 「不提供給 AI」守衛欄（mcp1a 計畫判斷 3）：Modified 且被標已修改（不論新值）、Added 且為 true ──

    private Task<bool> ExcludeFromAiAsync(long fieldId) =>
        Db.Context.Set<Field>().AsNoTracking().Where(f => f.Id == fieldId).Select(f => f.ExcludeFromAi).SingleAsync();

    [Fact]
    public async Task ExcludeFromAi_TrackedWrite_WithoutPermit_Throws()
    {
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == Target);
        field.ExcludeFromAi = true;
        var on = () => Db.Context.SaveChangesAsync();
        await on.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dynamic_Field*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();
        (await ExcludeFromAiAsync(Target)).Should().BeFalse();

        // 測試許可內改成 true，再在沒有許可時改回 false：收回也要許可（不論新值）
        var tracked = await Db.Context.Set<Field>().SingleAsync(f => f.Id == Target);
        tracked.ExcludeFromAi = true;
        await Db.Context.SaveForTestsAsync();
        tracked.ExcludeFromAi = false;
        var off = () => Db.Context.SaveChangesAsync();
        await off.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dynamic_Field*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();
        (await ExcludeFromAiAsync(Target)).Should().BeTrue();

        // 衍生與清除許可一律拒
        foreach (var permit in new[] { AuthorizationPermit.Derived(Target, 1, [GroupX]), AuthorizationPermit.Cleanup([], [], [Target], []) })
        {
            var again = await Db.Context.Set<Field>().SingleAsync(f => f.Id == Target);
            again.ExcludeFromAi = false;
            var act = () => AuthorizationWriteScope.RunAsync(permit, () => false, () => Db.Context.SaveChangesAsync());
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dynamic_Field*");
            Db.Context.ChangeTracker.Clear();
        }
        (await ExcludeFromAiAsync(Target)).Should().BeTrue();
    }

    [Fact]
    public async Task ExcludeFromAi_AddedTrue_WithoutPermit_Throws()
    {
        var template = await Db.Context.Set<Field>().AsNoTracking().SingleAsync(f => f.Id == Other);
        Db.Context.Add(new Field
        {
            ModuleId = template.ModuleId, BlockId = template.BlockId, Type = FieldType.Text, LabelI18n = template.LabelI18n, Key = "f_ai_probe",
            Status = FieldStatus.Active, ExcludeFromAi = true, Sequence = 99,
        });
        var act = () => Db.Context.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Dynamic_Field*Added*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<Field>().AsNoTracking().AnyAsync(f => f.Key == "f_ai_probe")).Should().BeFalse();
    }

    [Fact]
    public async Task ExcludeFromAi_AddedFalse_Allowed()
    {
        var template = await Db.Context.Set<Field>().AsNoTracking().SingleAsync(f => f.Id == Other);
        Db.Context.Add(new Field
        {
            ModuleId = template.ModuleId, BlockId = template.BlockId, Type = FieldType.Text, LabelI18n = template.LabelI18n, Key = "f_ai_plain",
            Status = FieldStatus.Active, Sequence = 99,
        });
        await Db.Context.SaveChangesAsync();
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<Field>().AsNoTracking().AnyAsync(f => f.Key == "f_ai_plain")).Should().BeTrue();
    }
}
