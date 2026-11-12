using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 記下本請求寫進檔案儲存的檔案 id。上傳經過寫入管線後，後盾與檢查失敗回的是 <see cref="ServiceResult"/>（不是例外），
/// handler 看不到；控制器在最終結果非成功或丟例外時呼叫 <see cref="DeleteAll"/>，不留孤兒檔。
/// </summary>
public sealed class AttachmentUploadTracker(IAttachmentStore store, ILogger<AttachmentUploadTracker> logger)
{
    private readonly List<string> _ids = [];

    public void Track(string id) => _ids.Add(id);

    /// <summary>成功提交後清空清單（不刪檔），共用 scope 時後續失敗不會刪到前一次已提交的檔。</summary>
    public void Forget() => _ids.Clear();

    /// <summary>刪不掉（例如防毒程式佔住檔案）只記 Warning，不蓋掉原本的回應或例外；殘檔交給 sweep。</summary>
    public void DeleteAll()
    {
        foreach (var id in _ids)
        {
            try { store.Delete(id); }
            catch (IOException ex) { logger.LogWarning(ex, "Attachment cleanup failed for {FileId}; left for sweep", id); }
        }
        _ids.Clear();
    }
}
