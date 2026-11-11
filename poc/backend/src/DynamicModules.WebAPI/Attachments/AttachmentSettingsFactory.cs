using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Policies;
using Microsoft.Data.Sqlite;

namespace DynamicModules.WebAPI.Attachments;

/// <summary>
/// 從設定檔組出附件設定（節 Attachments）：Root、UploadEnabled、DisabledReason 是部署設定；
/// MaxBytes、MaxFilesPerField、AllowedExtensions 只當該環境的政策出廠值（有寫才算，超出技術範圍夾值並回報 Warning）。
/// UploadEnabled 沒明確設定時：Demo:LoginEnabled 為 true（公開 Demo 站）就關閉上傳，否則開放。
/// </summary>
public static class AttachmentSettingsFactory
{
    public static (AttachmentSettings Settings, PolicyEnvironmentDefaults Defaults, IReadOnlyList<string> Warnings) Create(
        IConfiguration config, string dynamicConnection)
    {
        var section = config.GetSection("Attachments");
        var configured = section["Root"];
        var dataSource = new SqliteConnectionStringBuilder(dynamicConnection).DataSource;
        var root = !string.IsNullOrWhiteSpace(configured) ? Path.GetFullPath(configured)
            : Path.IsPathRooted(dataSource) ? Path.Combine(Path.GetDirectoryName(dataSource)!, "attachments")
            : Path.Combine(Path.GetTempPath(), "formwright-attachments");

        var demo = config.GetValue<bool>("Demo:LoginEnabled");
        var enabled = section.GetValue<bool?>("UploadEnabled") ?? !demo;
        var reason = section["DisabledReason"];
        var settings = new AttachmentSettings(root, enabled, string.IsNullOrWhiteSpace(reason) ? null : reason);

        var values = new List<(PolicyDefinition Def, string Raw)>();
        if (section["MaxBytes"] is { Length: > 0 } maxBytes) values.Add((PolicyCatalog.AttachmentMaxFileBytes, maxBytes));
        if (section["MaxFilesPerField"] is { Length: > 0 } maxFiles) values.Add((PolicyCatalog.AttachmentMaxFilesPerField, maxFiles));
        if (section.GetSection("AllowedExtensions").Get<string[]>() is { Length: > 0 } extensions)
            values.Add((PolicyCatalog.AttachmentAllowedExtensions, string.Join(",", extensions)));

        var warnings = new List<string>();
        return (settings, PolicyEnvironmentDefaults.Build(values, warnings), warnings);
    }
}
