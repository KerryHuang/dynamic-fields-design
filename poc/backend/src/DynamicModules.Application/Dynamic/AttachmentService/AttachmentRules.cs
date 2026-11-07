namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>檔名的清理與副檔名檢查（純函式）。檔名只用來顯示與下載時的建議檔名，從不用來決定磁碟上的路徑。</summary>
public static class AttachmentRules
{
    public const int MaxNameLength = 150;
    public const string NameRequired = "請選擇檔案";
    public const string ExtensionRejected = "不允許的檔案類型";
    public const string TooLarge = "檔案超過大小上限";
    public const string TooMany = "附件數量已達上限";
    public const string UploadDisabledPrefix = "此環境已停用附件上傳";

    /// <summary>去掉路徑（兩種分隔符）、控制字元與前後空白，限制長度並保留副檔名；清完是空的就回 null。</summary>
    public static string? CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var name = raw.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0 || name is "." or "..") return null;
        if (name.Length <= MaxNameLength) return name;
        var ext = ExtensionOf(name);
        return ext is null ? name[..MaxNameLength] : name[..(MaxNameLength - ext.Length - 1)] + "." + ext;
    }

    /// <summary>最後一個點之後的小寫副檔名；沒有點、點在最前面或最後面回 null。</summary>
    public static string? ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? null : name[(dot + 1)..].ToLowerInvariant();
    }
}
