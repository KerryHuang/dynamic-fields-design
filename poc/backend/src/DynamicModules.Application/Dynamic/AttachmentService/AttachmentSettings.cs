namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 附件的設定（由 WebAPI 從設定檔組出來）。副檔名是白名單，不是黑名單：沒列出的一律拒絕。
/// UploadEnabled 為 false 時，既有附件仍可下載，只是不能再上傳；DisabledReason 給畫面顯示原因。
/// </summary>
public sealed record AttachmentSettings(
    string Root, long MaxBytes, IReadOnlyList<string> AllowedExtensions, int MaxFilesPerField, bool UploadEnabled, string? DisabledReason)
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    public const int DefaultMaxFilesPerField = 20;

    /// <summary>預設白名單：文件、試算表、簡報與常見圖片。不含 svg、html、壓縮檔與任何可執行、腳本類。</summary>
    public static readonly string[] DefaultExtensions = ["pdf", "png", "jpg", "jpeg", "webp", "txt", "csv", "docx", "xlsx", "pptx"];

    public bool IsAllowed(string extension) =>
        AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>給前端的設定：上傳是否開放、為什麼不開放、上限。不含伺服器路徑。</summary>
public sealed record AttachmentSettingsResponse(
    bool UploadEnabled, string? DisabledReason, long MaxBytes, IReadOnlyList<string> AllowedExtensions, int MaxFilesPerField)
{
    public static AttachmentSettingsResponse From(AttachmentSettings s) =>
        new(s.UploadEnabled, s.UploadEnabled ? null : s.DisabledReason, s.MaxBytes, s.AllowedExtensions, s.MaxFilesPerField);
}
