namespace DynamicModules.Application.Common.Security;

/// <summary>設計器（DYN00000）請求，宣告需要哪一個動作軸；由 DesignerAccessBehavior 把關。</summary>
public interface IDesignerRequest
{
    PermissionAction RequiredDesignerAction { get; }
}
