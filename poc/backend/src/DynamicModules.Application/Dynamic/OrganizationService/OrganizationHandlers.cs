using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OrganizationService;

/// <summary>部門輸入驗證與回應組裝（設計 §11.2）。</summary>
public static partial class DepartmentRules
{
    public const int MaxDepth = 10;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,39}$")]
    private static partial Regex CodePattern();

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>departmentId 自己與所有下級（不含自己）。</summary>
    public static HashSet<long> Descendants(IReadOnlyList<Department> all, long id)
    {
        var children = all.Where(d => d.ParentId is not null).ToLookup(d => d.ParentId!.Value, d => d.Id);
        var result = new HashSet<long>();
        var stack = new Stack<long>(children[id]);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (next == id || !result.Add(next)) continue;
            foreach (var c in children[next]) stack.Push(c);
        }
        return result;
    }

    /// <summary>從樹根算起的層數（樹根是 1）。</summary>
    public static int DepthOf(IReadOnlyDictionary<long, Department> byId, long id)
    {
        var depth = 0;
        var visited = new HashSet<long>();
        for (long? cur = id; cur is { } c && visited.Add(c) && byId.TryGetValue(c, out var d); cur = d.ParentId) depth++;
        return depth;
    }

    /// <summary>以 id 為根的子樹高度（只有自己是 1）。</summary>
    public static int HeightOf(IReadOnlyList<Department> all, long id)
    {
        var children = all.Where(d => d.ParentId is not null).ToLookup(d => d.ParentId!.Value, d => d.Id);
        var visited = new HashSet<long>();
        int Walk(long n) => visited.Add(n) ? 1 + children[n].Select(Walk).DefaultIfEmpty(0).Max() : 0;
        return Walk(id);
    }

    public static DepartmentResponse ToResponse(Department d, IReadOnlyDictionary<string, long> members) =>
        new(d.Id, d.Code, d.NameI18n, d.ParentId, d.ManagerUserId, d.SortOrder, d.Active,
            members.Where(m => m.Value == d.Id).Select(m => m.Key).Order(StringComparer.Ordinal).ToList());
}

/// <summary>新增與修改共用的驗證；selfId 為 null 是新增。</summary>
public sealed class DepartmentValidator(IOrganizationRepository org, IPermissionRepository permissions)
{
    public async Task<Dictionary<string, string[]>> ValidateAsync(long? selfId, DepartmentInput input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var code = input.Code?.Trim();
        if (!DepartmentRules.IsValidCode(code)) errors["code"] = [OrganizationMessages.CodeInvalid];
        else if (await org.CodeExistsAsync(code!, selfId, ct)) errors["code"] = [OrganizationMessages.CodeExists];
        if (input.Name is null || !input.Name.IsValid(out _)) errors["name"] = [OrganizationMessages.NameRequired];
        if (await ParentErrorAsync(selfId, input.ParentId, ct) is { } parent) errors["parentId"] = [parent];
        if (input.ManagerUserId is { Length: > 0 } manager && await permissions.GetUserAsync(manager, ct) is not { IsActive: true })
            errors["managerUserId"] = [OrganizationMessages.ManagerInvalid];
        return errors;
    }

    private async Task<string?> ParentErrorAsync(long? selfId, long? parentId, CancellationToken ct)
    {
        if (parentId is not { } pid) return null;
        var all = await org.ListAsync(ct);
        var byId = all.ToDictionary(d => d.Id);
        if (!byId.ContainsKey(pid)) return OrganizationMessages.NotFound;
        if (selfId is { } id && (pid == id || DepartmentRules.Descendants(all, id).Contains(pid))) return OrganizationMessages.Cycle;
        var height = selfId is { } self ? DepartmentRules.HeightOf(all, self) : 1;
        return DepartmentRules.DepthOf(byId, pid) + height > DepartmentRules.MaxDepth ? OrganizationMessages.TooDeep : null;
    }
}

public class ListDepartmentsHandler(IOrganizationRepository org) : IRequestHandler<ListDepartmentsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ListDepartmentsRequest request, CancellationToken ct)
    {
        var members = await org.ListUserDepartmentsAsync(ct);
        return ServiceResult.Success((await org.ListAsync(ct)).Select(d => DepartmentRules.ToResponse(d, members)).ToList());
    }
}

public class CreateDepartmentHandler(
    IOrganizationRepository org, DepartmentValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<CreateDepartmentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateDepartmentRequest request, CancellationToken ct)
    {
        var input = request.Input;
        if (await validator.ValidateAsync(null, input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        var d = new Department
        {
            Code = input.Code.Trim(), NameI18n = input.Name, ParentId = input.ParentId,
            ManagerUserId = NullIfBlank(input.ManagerUserId), SortOrder = input.SortOrder, Active = input.Active
        };
        d.StampCreated(user.UserId, time.Now);
        org.Add(d);
        await uow.CommitAsync(ct);
        return ServiceResult.Created(DepartmentRules.ToResponse(d, new Dictionary<string, long>()));
    }

    internal static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class UpdateDepartmentHandler(
    IOrganizationRepository org, DepartmentValidator validator, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<UpdateDepartmentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateDepartmentRequest request, CancellationToken ct)
    {
        var d = await org.GetTrackedAsync(request.Id, ct);
        if (d is null) return ServiceResult.NotFound(OrganizationMessages.NotFound);
        var input = request.Input;
        if (await validator.ValidateAsync(d.Id, input, ct) is { Count: > 0 } errors) return ServiceResult.Invalid(errors);
        d.Code = input.Code.Trim();
        d.NameI18n = input.Name;   // 整個重新指派，EF 才偵測得到 JSON 欄位變更
        d.ParentId = input.ParentId;
        d.ManagerUserId = CreateDepartmentHandler.NullIfBlank(input.ManagerUserId);
        d.SortOrder = input.SortOrder;
        d.Active = input.Active;
        d.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(DepartmentRules.ToResponse(d, await org.ListUserDepartmentsAsync(ct)));
    }
}

public class DeleteDepartmentHandler(IOrganizationRepository org, IUnitOfWork uow) : IRequestHandler<DeleteDepartmentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteDepartmentRequest request, CancellationToken ct)
    {
        var d = await org.GetTrackedAsync(request.Id, ct);
        if (d is null) return ServiceResult.NotFound(OrganizationMessages.NotFound);
        var children = await org.CountChildrenAsync(d.Id, ct);
        var members = await org.CountMembersAsync(d.Id, ct);
        if (children + members > 0) return ServiceResult.Conflict(OrganizationMessages.HasDependents(children, members));
        org.Remove(d);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class SetUserDepartmentHandler(IOrganizationRepository org, IPermissionRepository permissions, IUnitOfWork uow)
    : IRequestHandler<SetUserDepartmentRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetUserDepartmentRequest request, CancellationToken ct)
    {
        if (await permissions.GetUserAsync(request.UserId, ct) is null) return ServiceResult.NotFound(OrganizationMessages.UserNotFound);
        if (request.DepartmentId is { } id)
        {
            var d = await org.GetTrackedAsync(id, ct);
            if (d is null) return Invalid(OrganizationMessages.NotFound);
            if (!d.Active) return Invalid(OrganizationMessages.InactiveDepartment);
        }
        await org.SetUserDepartmentAsync(request.UserId, request.DepartmentId, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }

    private static ServiceResult Invalid(string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { ["departmentId"] = [message] });
}
