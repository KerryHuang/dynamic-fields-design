namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>七個動作軸（spec §10）；群組權限與個人權限共用。</summary>
public abstract class ProgramPrivilege
{
    public string ProgramCode { get; set; } = "";
    public bool CanRead { get; set; }
    public bool CanAdd { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool CanPrint { get; set; }
    public bool CanAmt { get; set; }
    public bool CanApprove { get; set; }
}

public class GroupProgramPrivilege : ProgramPrivilege
{
    public long GroupId { get; set; }
}

public class UserProgramPrivilege : ProgramPrivilege
{
    public string UserId { get; set; } = "";
}
