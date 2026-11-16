using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.DeleteService;

/// <summary>欄位與區塊刪除共用的清除步驟：改寫記錄值、清索引與歷程、移除定義、遞增模組版本。須在呼叫端交易內執行。</summary>
internal static class FieldPurge
{
    public static async Task RemoveAsync(
        Module module, IReadOnlyCollection<long> fieldIds, IReadOnlyCollection<long> blockIds, IDeletionRepository deletion,
        IUnitOfWork uow, ICurrentUser user, IBusinessTime time, CancellationToken ct)
    {
        var fields = module.Blocks.SelectMany(b => b.Fields).Where(f => fieldIds.Contains(f.Id)).ToList();
        var keys = fields.Select(f => f.Key).ToList();
        var now = time.Now;

        // 從記錄值拿掉鍵；未軟刪除的記錄內容有變，換新 RowVersion 讓編輯中的人收到 409
        foreach (var record in await deletion.ListRecordsWithKeysAsync(module.Id, keys, ct))
        {
            var values = RecordValueProcessor.Parse(record.ValuesJson);
            foreach (var key in keys) values.Remove(key);
            record.ValuesJson = RecordValueProcessor.Serialize(values);
            if (record.DeleteFlag) continue;
            record.RowVersion = Guid.NewGuid().ToString();
            record.StampModified(user.UserId, now);
        }
        await uow.CommitAsync(ct);

        await deletion.PurgeFieldsAsync(fields, ct);
        foreach (var block in module.Blocks.Where(b => blockIds.Contains(b.Id)).ToList()) module.Blocks.Remove(block);
        foreach (var block in module.Blocks)
            foreach (var field in block.Fields.Where(f => fieldIds.Contains(f.Id)).ToList()) block.Fields.Remove(field);
        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
    }
}
