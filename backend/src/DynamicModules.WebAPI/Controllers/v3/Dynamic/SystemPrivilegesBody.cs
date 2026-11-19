using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>
/// 系統權限 API 的 body（權限軸設計 §8，T11）：五個布林都必填，型別全用 <c>bool?</c>，不讓模型繫結把缺漏當成 false；
/// 缺鍵或 null 回 400，一次列出全部缺的鍵。
/// </summary>
public record SystemPrivilegesBody(SystemPrivilegesBody.DesignerBody? Designer, bool? PermissionAdmin)
{
    public record DesignerBody(bool? Read, bool? Add, bool? Edit, bool? Delete);

    /// <summary>完整時回 dto、errors 為 null；缺任何鍵時 dto 為 null。</summary>
    public (SystemPrivilegesDto? Dto, ServiceResult? Invalid) Parse()
    {
        var missing = new List<string>();
        if (Designer?.Read is null) missing.Add("designer.read");
        if (Designer?.Add is null) missing.Add("designer.add");
        if (Designer?.Edit is null) missing.Add("designer.edit");
        if (Designer?.Delete is null) missing.Add("designer.delete");
        if (PermissionAdmin is null) missing.Add("permissionAdmin");
        if (missing.Count > 0)
            return (null, ServiceResult.Invalid(missing.ToDictionary(k => k, _ => new[] { MessageRef.Of(MessageText.Required) })));
        return (new SystemPrivilegesDto(
            new DesignerFlags(Designer!.Read!.Value, Designer.Add!.Value, Designer.Edit!.Value, Designer.Delete!.Value), PermissionAdmin!.Value), null);
    }
}
