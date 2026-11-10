namespace DynamicModules.Application.Dynamic.TaskService;

public static class TaskMessages
{
    public const string NotFound = "找不到任務";
    public const string TitleRequired = "標題必填";
    public const string TitleTooLong = "標題不能超過 200 字";
    public const string DescriptionTooLong = "說明不能超過 4000 字";
    public const string AssigneeInvalid = "負責人必須是啟用中的使用者";
    public const string RuleTaskNoDelete = "規則產生的任務不能刪除，只能取消";
    public const string BadStatus = "狀態不正確";
    public const string BadDate = "日期格式必須是 yyyy-MM-dd";
    public const string BodyRequired = "留言不能是空的";
    public const string BodyTooLong = "留言不能超過 4000 字";
    public const string CommentNotFound = "找不到留言";
    public const string SystemEntryNoWithdraw = "系統記錄不能撤回";
    public const string TooManyFiles = "一則留言最多 5 個附件";
    public const string AttachmentNotFound = "找不到附件";
}
