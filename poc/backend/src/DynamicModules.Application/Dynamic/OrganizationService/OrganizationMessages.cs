namespace DynamicModules.Application.Dynamic.OrganizationService;

public static class OrganizationMessages
{
    public const string CodeInvalid = "部門代號只能用小寫英文、數字、連字號，英文開頭，最多 40 字";
    public const string CodeExists = "部門代號已存在";
    public const string NameRequired = "名稱必須有繁體中文";
    public const string Cycle = "上級部門不能是自己或自己的下級部門";
    public const string TooDeep = "部門最多 10 層";
    public static string HasDependents(int children, int members) => $"部門還有 {children} 個子部門、{members} 位成員，請先搬移";
    public const string InactiveDepartment = "停用的部門不能指派使用者";
    public const string ManagerInvalid = "主管必須是啟用中的使用者";
    public const string NotFound = "找不到部門";
    public const string UserNotFound = "使用者不存在";
}
