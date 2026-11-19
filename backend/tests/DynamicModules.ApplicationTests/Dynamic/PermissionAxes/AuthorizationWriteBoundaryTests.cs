using DynamicModules.Domain.Entities.Dynamic;
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
}
