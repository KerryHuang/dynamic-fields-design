using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.PermissionAxes;

/// <summary>
/// 授權寫入邊界負向案例（PAX-23a–i）的共用 fixture：§18 的 persona（小王 wang：設計器；小李 li：權限設定；小陳 chen：兩者），
/// 一個模組 pax-w 與兩個欄位（目標欄位 Target、旁邊的欄位 Other），群組 pax-g-x 在兩個欄位各有一列欄位權限。
/// 測試用請求與 handler 都在這裡（不進正式組件），以 DI 個別註冊。
/// </summary>
public abstract class AuthorizationBoundaryTestBase : DesignerTestBase
{
    protected long ModuleId;
    protected string ModuleKey = "pax-w";
    protected long Target;
    protected long Other;
    protected long GroupX;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IRequestHandler<ForgotToDeclareRequest, ServiceResult>, ForgotToDeclareHandler>();
        services.AddTransient<IRequestHandler<CleanupProbeRequest, ServiceResult>, CleanupProbeHandler>();
        services.AddTransient<IRequestHandler<PermissionAdminProbeRequest, ServiceResult>, PermissionAdminProbeHandler>();
        services.AddTransient<IRequestHandler<SystemAdminProbeRequest, ServiceResult>, SystemAdminProbeHandler>();
        services.AddTransient<IRequestHandler<ConditionalRelaxRequest, ServiceResult>, ConditionalRelaxHandler>();
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var db = Db.Context;
        db.Set<AppUser>().AddRange(
            new AppUser { UserId = "wang", DisplayName = "小王" }, new AppUser { UserId = "li", DisplayName = "小李" },
            new AppUser { UserId = "chen", DisplayName = "小陳" });
        var (design, perm, both, x) = (Group("pax-g-design"), Group("pax-g-perm"), Group("pax-g-both"), Group("pax-g-x"));
        db.Set<PermissionGroup>().AddRange(design, perm, both, x);
        await db.SaveChangesAsync();
        GroupX = x.Id;
        db.Set<UserGroupMapping>().AddRange(
            new UserGroupMapping { GroupId = design.Id, UserId = "wang" }, new UserGroupMapping { GroupId = perm.Id, UserId = "li" },
            new UserGroupMapping { GroupId = both.Id, UserId = "chen" });
        db.Set<GroupProgramPrivilege>().AddRange(
            new GroupProgramPrivilege { GroupId = design.Id, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true },
            new GroupProgramPrivilege { GroupId = perm.Id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true },
            new GroupProgramPrivilege { GroupId = both.Id, ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true },
            new GroupProgramPrivilege { GroupId = both.Id, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true });
        await db.SaveForTestsAsync();

        await NewModule(ModuleKey);
        Target = (await MustAdd(ModuleKey, FieldType.Text, label: "目標欄位")).Id;
        Other = (await MustAdd(ModuleKey, FieldType.Text, label: "旁邊的欄位")).Id;
        ModuleId = (await Detail(ModuleKey)).Id;
        db.Set<FieldPermission>().AddRange(
            new FieldPermission { FieldId = Target, GroupId = GroupX, Access = FieldAccess.Hidden },
            new FieldPermission { FieldId = Other, GroupId = GroupX, Access = FieldAccess.Hidden });
        await db.SaveForTestsAsync();
        db.ChangeTracker.Clear();
    }

    private static PermissionGroup Group(string key) => new() { Key = key, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]) };

    /// <summary>守衛表逐列的快照（排序後序列化），比對「逐列不變」。</summary>
    protected async Task<List<string>> GuardRowsAsync()
    {
        var db = Db.Context;
        var rows = new List<string>();
        async Task Add<T>() where T : class =>
            rows.AddRange((await db.Set<T>().AsNoTracking().ToListAsync()).Select(r => $"{typeof(T).Name} {JsonSerializer.Serialize(r)}"));
        await Add<GroupProgramPrivilege>();
        await Add<UserProgramPrivilege>();
        await Add<FieldPermission>();
        await Add<ModuleDataScope>();
        await Add<FieldOptionScope>();
        await Add<FieldGrant>();
        await Add<AppGrant>();
        await Add<ModuleActionScope>();
        await Add<UserGroupMapping>();
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    protected Task<bool> VisibilityRestrictedAsync(long fieldId) =>
        Db.Context.Set<Field>().AsNoTracking().Where(f => f.Id == fieldId).Select(f => f.VisibilityRestricted).SingleAsync();

    protected async Task SetVisibilityRestrictedAsync(long fieldId, bool value)
    {
        // 收緊（改成 true）不經守衛；放寬要許可，這裡用測試許可
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == fieldId);
        field.VisibilityRestricted = value;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    // ── 測試用請求與 handler ─────────────────────────────────────────────────

    public enum ForgotMode { BatchReplace, TrackedAdd }

    /// <summary>PAX-23a、23b：刻意漏宣告權限設定，只宣告設計器修改。</summary>
    public sealed record ForgotToDeclareRequest(long ModuleId, long FieldId, long GroupId, ForgotMode Mode) : IRequest<ServiceResult>, IDesignerRequest
    {
        public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
    }

    public sealed class ForgotToDeclareHandler(DynamicDbContext db, IPermissionRepository permissions) : IRequestHandler<ForgotToDeclareRequest, ServiceResult>
    {
        public async Task<ServiceResult> Handle(ForgotToDeclareRequest r, CancellationToken ct)
        {
            if (r.Mode == ForgotMode.BatchReplace)
            {
                var module = await db.Set<Module>().AsNoTracking().SingleAsync(m => m.Id == r.ModuleId, ct);
                await permissions.ReplaceModuleRowsAsync(module, new ModulePermissionRows([], [], [], [], []), ct);
            }
            else db.Add(new FieldPermission { FieldId = r.FieldId, GroupId = r.GroupId + 1000, Access = FieldAccess.ReadOnly });
            await db.SaveChangesAsync(ct);
            return ServiceResult.Success();
        }
    }

    public enum CleanupMode { AddRow, DeleteOtherFieldRow, PurgeWithoutDeletingField }

    /// <summary>PAX-23c：在 Cleanup 許可內做不該做的事；交易請求，例外時整筆回滾。</summary>
    public sealed record CleanupProbeRequest(long TargetFieldId, long OtherFieldId, long GroupId, CleanupMode Mode) : IRequest<ServiceResult>, ITransactionalRequest;

    public sealed class CleanupProbeHandler(DynamicDbContext db, IDeletionRepository deletion) : IRequestHandler<CleanupProbeRequest, ServiceResult>
    {
        public async Task<ServiceResult> Handle(CleanupProbeRequest r, CancellationToken ct)
        {
            await deletion.RunCleanupAsync(CleanupTarget.Fields([r.TargetFieldId]), async () =>
            {
                switch (r.Mode)
                {
                    case CleanupMode.AddRow:
                        db.Add(new FieldPermission { FieldId = r.TargetFieldId, GroupId = r.GroupId + 1000, Access = FieldAccess.ReadOnly });
                        db.Remove(await db.Set<FieldPermission>().SingleAsync(p => p.FieldId == r.TargetFieldId && p.GroupId == r.GroupId, ct));
                        break;
                    case CleanupMode.DeleteOtherFieldRow:
                        db.Remove(await db.Set<FieldPermission>().SingleAsync(p => p.FieldId == r.OtherFieldId && p.GroupId == r.GroupId, ct));
                        break;
                    case CleanupMode.PurgeWithoutDeletingField:
                        var field = await db.Set<Field>().AsNoTracking().Include(f => f.Options).SingleAsync(f => f.Id == r.TargetFieldId, ct);
                        await deletion.PurgeFieldsAsync([field], ct);
                        break;
                }
                await db.SaveChangesAsync(ct);
            }, ct);
            return ServiceResult.Success();
        }
    }

    public enum AdminProbeMode { SystemCodeRow, Membership, SystemCodeBatch }

    /// <summary>PAX-23e：宣告權限設定（Authorized 許可、沒有 SystemAdminVerified）。</summary>
    public sealed record PermissionAdminProbeRequest(long ModuleId, long GroupId, AdminProbeMode Mode) : IRequest<ServiceResult>, IPermissionAdminRequest;

    /// <summary>PAX-23e：同時宣告系統管理員與權限設定（許可帶 SystemAdminVerified）。</summary>
    public sealed record SystemAdminProbeRequest(long ModuleId, long GroupId, AdminProbeMode Mode) : IRequest<ServiceResult>, IPermissionAdminRequest, ISystemAdminRequest;

    public sealed class PermissionAdminProbeHandler(DynamicDbContext db, IPermissionRepository permissions) : IRequestHandler<PermissionAdminProbeRequest, ServiceResult>
    {
        public Task<ServiceResult> Handle(PermissionAdminProbeRequest r, CancellationToken ct) => AdminProbe.RunAsync(db, permissions, r.ModuleId, r.GroupId, r.Mode, ct);
    }

    public sealed class SystemAdminProbeHandler(DynamicDbContext db, IPermissionRepository permissions) : IRequestHandler<SystemAdminProbeRequest, ServiceResult>
    {
        public Task<ServiceResult> Handle(SystemAdminProbeRequest r, CancellationToken ct) => AdminProbe.RunAsync(db, permissions, r.ModuleId, r.GroupId, r.Mode, ct);
    }

    private static class AdminProbe
    {
        public static async Task<ServiceResult> RunAsync(DynamicDbContext db, IPermissionRepository permissions, long moduleId, long groupId, AdminProbeMode mode, CancellationToken ct)
        {
            switch (mode)
            {
                case AdminProbeMode.SystemCodeRow:
                    db.Add(new GroupProgramPrivilege { GroupId = groupId, ProgramCode = SystemProgramCodes.PermissionAdmin, CanEdit = true });
                    break;
                case AdminProbeMode.Membership:
                    db.Add(new UserGroupMapping { GroupId = groupId, UserId = "wang" });
                    break;
                case AdminProbeMode.SystemCodeBatch:
                    await permissions.ReplaceModuleRowsAsync(
                        new Module { Id = moduleId, Key = "pax-sys", ProgramCode = SystemProgramCodes.PermissionAdmin }, new ModulePermissionRows([], [], [], [], []), ct);
                    break;
            }
            await db.SaveChangesAsync(ct);
            return ServiceResult.Success();
        }
    }

    /// <summary>PAX-23h：合法的條件式寫入：handler 以 PermissionAdminGate.RunAsync 包住「放寬守衛欄＋存檔」。</summary>
    public sealed record ConditionalRelaxRequest(long FieldId) : IRequest<ServiceResult>;

    public sealed class ConditionalRelaxHandler(DynamicDbContext db, PermissionAdminGate gate) : IRequestHandler<ConditionalRelaxRequest, ServiceResult>
    {
        public Task<ServiceResult> Handle(ConditionalRelaxRequest r, CancellationToken ct) =>
            gate.RunAsync(async () =>
            {
                var field = await db.Set<Field>().SingleAsync(f => f.Id == r.FieldId, ct);
                field.VisibilityRestricted = false;
                await db.SaveChangesAsync(ct);
                return ServiceResult.Success();
            }, ct);
    }
}
