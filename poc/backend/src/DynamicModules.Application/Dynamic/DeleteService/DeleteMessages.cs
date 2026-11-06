using DynamicModules.Application.Dynamic.DeleteService.Data;

namespace DynamicModules.Application.Dynamic.DeleteService;

public static class DeleteMessages
{
    public const string Blocked = "不符合刪除條件，請先處理列出的項目";
    public const string PurgedMismatch = "要清除的資料筆數已改變，請重新確認";
    public const string PurgedRequired = "必須帶上確認過的清除筆數";

    public static string NotFound(DeleteTarget target) => target switch
    {
        DeleteTarget.Module => "找不到模組",
        DeleteTarget.Field => "找不到欄位",
        _ => "找不到區塊"
    };
}
