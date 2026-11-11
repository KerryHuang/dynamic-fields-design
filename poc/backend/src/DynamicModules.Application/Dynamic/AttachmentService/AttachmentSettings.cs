namespace DynamicModules.Application.Dynamic.AttachmentService;

/// <summary>
/// 附件的部署設定（由 WebAPI 從設定檔組出來）：存放位置與環境開關。大小、數量與副檔名白名單是政策（A20），
/// 由 <see cref="PolicyService.IPolicyReader"/> 讀；部署設定檔 Attachments 節的同名值只當該環境的出廠值。
/// UploadEnabled 為 false 時，既有附件仍可下載，只是不能再上傳；DisabledReason 給畫面顯示原因。
/// </summary>
public sealed record AttachmentSettings(string Root, bool UploadEnabled, string? DisabledReason);

/// <summary>
/// 給前端的設定：上傳是否開放、為什麼不開放、<b>全系統</b>的上限。模組若有覆寫值，這裡不反映，
/// 需要模組值的畫面改讀生效值 API（<c>GET policies/effective</c>）。
/// </summary>
public sealed record AttachmentSettingsResponse(
    bool UploadEnabled, string? DisabledReason, long MaxBytes, IReadOnlyList<string> AllowedExtensions, int MaxFilesPerField);
