using DynamicModules.Application.Dynamic.AttachmentService;
using Microsoft.Data.Sqlite;

namespace DynamicModules.WebAPI.Attachments;

/// <summary>
/// 從設定檔組出附件設定（節 Attachments）：Root、MaxBytes、AllowedExtensions、MaxFilesPerField、UploadEnabled、DisabledReason。
/// UploadEnabled 沒明確設定時：Demo:LoginEnabled 為 true（公開 Demo 站）就關閉上傳，否則開放。
/// </summary>
public static class AttachmentSettingsFactory
{
    public static AttachmentSettings Create(IConfiguration config, string dynamicConnection)
    {
        var section = config.GetSection("Attachments");
        var configured = section["Root"];
        var dataSource = new SqliteConnectionStringBuilder(dynamicConnection).DataSource;
        var root = !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured)
            : Path.IsPathRooted(dataSource) ? Path.Combine(Path.GetDirectoryName(dataSource)!, "attachments")
            : Path.Combine(Path.GetTempPath(), "formwright-attachments");

        var extensions = section.GetSection("AllowedExtensions").Get<string[]>() is { Length: > 0 } list
            ? list.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToArray()
            : AttachmentSettings.DefaultExtensions;

        var demo = config.GetValue<bool>("Demo:LoginEnabled");
        var enabled = section.GetValue<bool?>("UploadEnabled") ?? !demo;
        var reason = section["DisabledReason"];
        return new AttachmentSettings(
            root,
            section.GetValue("MaxBytes", AttachmentSettings.DefaultMaxBytes),
            extensions,
            section.GetValue("MaxFilesPerField", AttachmentSettings.DefaultMaxFilesPerField),
            enabled,
            string.IsNullOrWhiteSpace(reason) ? null : reason);
    }
}
