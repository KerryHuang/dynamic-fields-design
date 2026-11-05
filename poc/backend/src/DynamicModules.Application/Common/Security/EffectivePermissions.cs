using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Security;

public sealed record EffectivePermissions(
    IReadOnlySet<PermissionAction> Actions, DataScope Scope,
    IReadOnlySet<string>? OwnerUserIds,          // null = 不限（All）
    IReadOnlySet<long> HiddenFieldIds, IReadOnlySet<long> ReadOnlyFieldIds,
    IReadOnlySet<long> UnselectableOptionIds, string Hash)
{
    public bool Can(PermissionAction a) => Actions.Contains(a);
    public bool CanSee(Field f) => !HiddenFieldIds.Contains(f.Id);
    public bool CanWrite(Field f) => CanSee(f) && !ReadOnlyFieldIds.Contains(f.Id);
}
