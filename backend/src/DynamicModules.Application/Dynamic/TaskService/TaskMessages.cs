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
    public const string StartAfterDue = "開始日不能晚於到期日";
    public const string BodyRequired = "留言不能是空的";
    public const string BodyTooLong = "留言不能超過 4000 字";
    public const string CommentNotFound = "找不到留言";
    public const string SystemEntryNoWithdraw = "系統記錄不能撤回";
    public static string TooManyFiles(int max) => $"一則留言最多 {max} 個附件";
    public const string AttachmentNotFound = "找不到附件";
    public const string UndoInvalid = "這次變更已無法復原";
    public const string SupervisorOnlyAssignee = "你以主管身分只能改負責人";
    public const string Stale = "任務已被別人修改，已重新整理";
    public const string ExpectedVersionRequired = "缺少任務版本";
    public const string BadGroupBy = "分欄依據必須是 status、assignee 或 dueBucket";
    public const string BadColumn = "看板欄不正確";
    public const string DropOverdue = "不能拖到逾期；要改成過去的日期請開任務詳細";
    public const string DropThisWeekEmpty = "今天已是本週最後一天";
    public const string DropInactiveAssignee = "停用的使用者不能指派";
    public const string LaterNeedsDate = "拖到「之後」要選日期";
    public const string LaterNotAfterWeekEnd = "日期必須晚於本週最後一天";
    public const string SubtaskNeedsParentEdit = "要能修改這個任務才能新增子任務";
    public const string SubtaskOneLevel = "子任務不能再有子任務";
    public const string SubtaskParentClosed = "已完成或已取消的任務不能新增子任務";
    public static string TooManySubtasks(int max) => $"一個任務最多 {max} 個子任務";
    public static string SubtasksOpen(int count) => $"還有 {count} 個未完成的子任務";
    public const string BadCascade = "連動方式必須是 complete 或 cancel";
    public const string ParentCancelledReopen = "父任務已取消，請先恢復父任務";
}
