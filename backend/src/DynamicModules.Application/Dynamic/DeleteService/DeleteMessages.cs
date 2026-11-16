using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.DeleteService.Data;

namespace DynamicModules.Application.Dynamic.DeleteService;

public static class DeleteMessages
{
    public const string Blocked = "不符合刪除條件，請先處理列出的項目";
    public const string PurgedMismatch = "要清除的資料筆數已改變，請重新確認";
    public const string PurgedRequired = "必須帶上確認過的清除筆數";

    public static MessageRef NotFound(DeleteTarget target) => MessageRef.Of(target switch
    {
        DeleteTarget.Module => MessageText.ModuleNotFound,
        DeleteTarget.Field => MessageText.FieldNotFound,
        _ => MessageText.BlockNotFound
    });
}
