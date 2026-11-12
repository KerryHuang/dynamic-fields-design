namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 記下本請求寫進檔案儲存的檔案 id。上傳經過寫入管線後，後盾與檢查失敗回的是 <see cref="ServiceResult"/>（不是例外），
/// handler 看不到；控制器在最終結果非成功或丟例外時呼叫 <see cref="DeleteAll"/>，不留孤兒檔。
/// </summary>
public sealed class AttachmentUploadTracker(IAttachmentStore store)
{
    private readonly List<string> _ids = [];

    public void Track(string id) => _ids.Add(id);

    public void DeleteAll()
    {
        foreach (var id in _ids) store.Delete(id);
        _ids.Clear();
    }
}
