using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 授權寫入邊界的其餘負向案例（權限軸設計 §7.4、§18，只在 SQLite 跑）：PAX-23a（漏宣告呼叫批次入口）、PAX-23d（Derived 許可的限制）、
/// PAX-23g（SuppressFlow 的背景工作帶不到許可）、PAX-23h（合法的條件式寫入）、PAX-23i（body 不存檔、許可關閉後寫入）。
/// </summary>
public sealed class AuthorizationWriteScopeTests : AuthorizationBoundaryTestBase
{
    private Func<bool> Pending => () => AuthorizationSaveChangesInterceptor.HasPendingGuardChanges(Db.Context);

    [Fact]
    public async Task PAX23a_ForgotToDeclare_BatchEntry_Throws_RowsUnchanged()
    {
        var before = await GuardRowsAsync();
        As("admin");
        var act = () => Send(new ForgotToDeclareRequest(ModuleId, Target, GroupX, ForgotMode.BatchReplace));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*批次寫入被拒（ReplaceModuleRows）*沒有寫入許可*");
        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task PAX23d_DerivedPermit_OnlyAppGrantsForItsFieldAppAndGroups_NoBatch()
    {
        var before = await GuardRowsAsync();
        const long appId = 990001;
        var permit = () => AuthorizationPermit.Derived(Target, appId, [GroupX]);

        // 手動來源的可見列
        var manual = () => AuthorizationWriteScope.RunAsync(permit(), Pending, async () =>
        {
            Db.Context.Add(new FieldGrant { SourceKind = GrantSource.Manual, FieldId = Target, GroupId = GroupX });
            await Db.Context.SaveChangesAsync();
        });
        await manual.Should().ThrowAsync<InvalidOperationException>().WithMessage("*衍生許可只准新增*");
        Db.Context.ChangeTracker.Clear();

        // 群組不在許可參數內
        var otherGroup = () => AuthorizationWriteScope.RunAsync(permit(), Pending, async () =>
        {
            Db.Context.Add(new FieldGrant { SourceKind = GrantSource.App, FieldId = Target, GroupId = GroupX + 1000, AppId = appId });
            await Db.Context.SaveChangesAsync();
        });
        await otherGroup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*衍生許可只准新增*");
        Db.Context.ChangeTracker.Clear();

        // 任一批次入口
        var batch = () => AuthorizationWriteScope.RunAsync(permit(), Pending,
            () => Svc<ISharedMasterRepository>().ReplaceActionScopesAsync(ModuleId, [], CancellationToken.None));
        await batch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*批次寫入被拒（ReplaceActionScopes）*Derived*");
        var purge = () => AuthorizationWriteScope.RunAsync(permit(), Pending,
            () => Svc<IDeletionRepository>().PurgeFieldsAsync([new Field { Id = Target }], CancellationToken.None));
        await purge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*批次寫入被拒（PurgeFields）*Derived*");

        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task PAX23g_SuppressFlowBackgroundWork_DoesNotCarryThePermit()
    {
        var before = await GuardRowsAsync();
        Task background = Task.CompletedTask;
        await TestPermits.RunForTestsAsync(async () =>
        {
            using (ExecutionContext.SuppressFlow())
                background = Task.Run(async () =>
                {
                    Db.Context.Add(new FieldPermission { FieldId = Target, GroupId = GroupX + 1000, Access = FieldAccess.ReadOnly });
                    await Db.Context.SaveChangesAsync();
                });
            var act = () => background;
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*沒有寫入許可*");
        });
        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);

        // 對照：同樣的工作不 SuppressFlow 就帶著許可，寫得進去
        await TestPermits.RunForTestsAsync(() => Task.Run(async () =>
        {
            var row = await Db.Context.Set<FieldPermission>().SingleAsync(p => p.FieldId == Other && p.GroupId == GroupX);
            row.Access = FieldAccess.ReadOnly;
            await Db.Context.SaveChangesAsync();
        }));
        Db.Context.ChangeTracker.Clear();
        (await Db.Context.Set<FieldPermission>().AsNoTracking().SingleAsync(p => p.FieldId == Other && p.GroupId == GroupX)).Access.Should().Be(FieldAccess.ReadOnly);
    }

    [Fact]
    public async Task PAX23h_ConditionalWrite_ThroughGate_OnlyForPermissionAdmin()
    {
        await SetVisibilityRestrictedAsync(Target, true);

        As("wang");
        var denied = await Send(new ConditionalRelaxRequest(Target));
        denied.IsForbidden.Should().BeTrue();
        denied.Code.Should().Be("permissionAdminRequired");
        Db.Context.ChangeTracker.Clear();
        (await VisibilityRestrictedAsync(Target)).Should().BeTrue("小王沒有權限設定，body 不執行");

        As("chen");
        var ok = await Send(new ConditionalRelaxRequest(Target));
        ok.IsSuccess.Should().BeTrue(ok.Message);
        Db.Context.ChangeTracker.Clear();
        (await VisibilityRestrictedAsync(Target)).Should().BeFalse("小陳有權限設定，gate 內放寬並存檔");
    }

    [Fact]
    public async Task PAX23i_AfterThePermit_UnsavedChangesAndDetachedWorkAreRejected()
    {
        var before = await GuardRowsAsync();
        As("chen");
        var gate = Svc<PermissionAdminGate>();

        // body 只改守衛列、不存檔 → body 結束時例外
        var unsaved = () => gate.RunAsync(() =>
        {
            Db.Context.Add(new FieldPermission { FieldId = Target, GroupId = GroupX + 1000, Access = FieldAccess.ReadOnly });
            return Task.FromResult(ServiceResult.Success());
        }, CancellationToken.None);
        await unsaved.Should().ThrowAsync<InvalidOperationException>().WithMessage("*守衛變更要在許可內存檔*");
        Db.Context.ChangeTracker.Clear();

        // body 內以 Task.Run 起、未等待的工作，在 body 結束後才寫 → 許可已關閉
        var release = new TaskCompletionSource();
        Task detached = Task.CompletedTask;
        var result = await gate.RunAsync(() =>
        {
            detached = Task.Run(async () =>
            {
                await release.Task;
                Db.Context.Add(new FieldPermission { FieldId = Target, GroupId = GroupX + 1000, Access = FieldAccess.ReadOnly });
                await Db.Context.SaveChangesAsync();
            });
            return Task.FromResult(ServiceResult.Success());
        }, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        release.SetResult();
        var late = () => detached;
        await late.Should().ThrowAsync<InvalidOperationException>().WithMessage("*沒有寫入許可*");

        Db.Context.ChangeTracker.Clear();
        (await GuardRowsAsync()).Should().Equal(before);
    }
}
