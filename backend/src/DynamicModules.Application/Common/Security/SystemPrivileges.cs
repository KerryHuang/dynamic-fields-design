using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>設計器的四個動作（API 形狀 <c>{ read, add, edit, delete }</c>，權限軸設計 §8）。</summary>
public sealed record DesignerFlags(bool Read, bool Add, bool Edit, bool Delete)
{
    public static readonly DesignerFlags None = new(false, false, false, false);
    public static readonly DesignerFlags All = new(true, true, true, true);

    public bool Any => Read || Add || Edit || Delete;

    /// <summary>固定依讀取、新增、修改、刪除的順序（安全事件明細用）。</summary>
    public List<string> Names() =>
        [.. new[] { (Read, "read"), (Add, "add"), (Edit, "edit"), (Delete, "delete") }.Where(x => x.Item1).Select(x => x.Item2)];

    public DesignerFlags Or(DesignerFlags o) => new(Read || o.Read, Add || o.Add, Edit || o.Edit, Delete || o.Delete);
}

/// <summary>兩個系統權限：直接設定或有效結果都用這個形狀（API 不混用，欄位名稱區分）。</summary>
public sealed record SystemPrivilegesDto(DesignerFlags Designer, bool PermissionAdmin)
{
    public static readonly SystemPrivilegesDto None = new(DesignerFlags.None, false);
    public static readonly SystemPrivilegesDto All = new(DesignerFlags.All, true);

    public bool Any => Designer.Any || PermissionAdmin;
}

/// <summary>系統權限的算法（權限軸設計 §5、§8、§9）：純函式，GET me、管理清單、持有者快照共用同一份。</summary>
public static class SystemPrivilegeRules
{
    /// <summary>一個來源（一個群組或一位使用者個人）的直接設定：只看系統代碼列，權限設定只看 CanEdit（T2）。</summary>
    public static SystemPrivilegesDto Direct(IEnumerable<ProgramPrivilege> rows)
    {
        var list = rows.ToList();
        var d = list.FirstOrDefault(r => r.ProgramCode == SystemProgramCodes.Designer);
        var designer = d is null ? DesignerFlags.None : new DesignerFlags(d.CanRead, d.CanAdd, d.CanEdit, d.CanDelete);
        return new SystemPrivilegesDto(designer, list.Any(r => r.ProgramCode == SystemProgramCodes.PermissionAdmin && r.CanEdit));
    }

    /// <summary>群組與個人逐項合併，再套「權限設定隱含設計器讀取」（M1）；不看啟用與管理員（持有者快照用）。</summary>
    public static SystemPrivilegesDto Merged(IEnumerable<ProgramPrivilege> rows)
    {
        var designer = DesignerFlags.None;
        var permissionAdmin = false;
        foreach (var r in rows)
            if (r.ProgramCode == SystemProgramCodes.Designer) designer = designer.Or(new DesignerFlags(r.CanRead, r.CanAdd, r.CanEdit, r.CanDelete));
            else if (r.ProgramCode == SystemProgramCodes.PermissionAdmin && r.CanEdit) permissionAdmin = true;
        if (permissionAdmin) designer = designer with { Read = true };
        return new SystemPrivilegesDto(designer, permissionAdmin);
    }

    /// <summary>有效結果：停用 → 沒有；系統管理員 → 全部；其餘照合併（和 guard 的判斷順序一致，T5）。</summary>
    public static SystemPrivilegesDto Effective(AppUser user, IEnumerable<ProgramPrivilege> rows) =>
        !user.IsActive ? SystemPrivilegesDto.None : user.IsSystemAdmin ? SystemPrivilegesDto.All : Merged(rows);

    /// <summary>直接設定轉成要存的列（全 false 的代碼不存）；CanPrint、CanAmt、CanApprove 一律 false。</summary>
    public static List<ProgramPrivilege> ToRows(SystemPrivilegesDto dto, Func<ProgramPrivilege> create)
    {
        var rows = new List<ProgramPrivilege>();
        if (dto.Designer.Any)
        {
            var r = create();
            r.ProgramCode = SystemProgramCodes.Designer;
            (r.CanRead, r.CanAdd, r.CanEdit, r.CanDelete) = (dto.Designer.Read, dto.Designer.Add, dto.Designer.Edit, dto.Designer.Delete);
            rows.Add(r);
        }
        if (dto.PermissionAdmin)
        {
            var r = create();
            r.ProgramCode = SystemProgramCodes.PermissionAdmin;
            r.CanEdit = true;
            rows.Add(r);
        }
        return rows;
    }
}
