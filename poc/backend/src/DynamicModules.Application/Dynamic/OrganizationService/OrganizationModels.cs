using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.OrganizationService;

public sealed record DepartmentInput(string Code, I18nText Name, long? ParentId, string? ManagerUserId, int SortOrder, bool Active);

public sealed record DepartmentResponse(
    long Id, string Code, I18nText Name, long? ParentId, string? ManagerUserId, int SortOrder, bool Active, IReadOnlyList<string> MemberUserIds);
