namespace DynamicModules.Domain.Entities.Dynamic;

// 共用主檔（設計 2026-11-10-shared-master-design §8）的資料表。

/// <summary>應用程式或主檔（manifest.kind）。</summary>
public enum AppKind { App, Master }

/// <summary>權限來源：貢獻者應用程式、主檔自己（steward）、管理員手動。</summary>
public enum GrantSource { App, Master, Manual }

/// <summary>孤兒列記的是哪一種東西；必須與實際填的外鍵一致。</summary>
public enum OrphanKind { Field, Option, Block }

/// <summary>「使用」關係：AppId（應用程式或主檔）使用 MasterAppId 這個主檔。開啟時寫入，刪除時移除，關閉不動。</summary>
public class AppMasterUse
{
    public long AppId { get; set; }
    public long MasterAppId { get; set; }
}

/// <summary>權限來源記錄：一個來源、一個主檔模組、一個群組、一個動作一列。手動來源的 AppId 是 null。</summary>
public class AppGrant
{
    public long Id { get; set; }
    public GrantSource SourceKind { get; set; }
    public long? AppId { get; set; }
    public long ModuleId { get; set; }
    public long GroupId { get; set; }
    public PermissionAction Action { get; set; }
    public DataScope Scope { get; set; }
}

/// <summary>主檔模組的逐動作資料範圍，由權限來源記錄重算而來。</summary>
public class ModuleActionScope
{
    public long ModuleId { get; set; }
    public long GroupId { get; set; }
    public PermissionAction Action { get; set; }
    public DataScope Scope { get; set; }
}

/// <summary>受限可見的貢獻欄位對哪個群組可見、來源是誰（App 或 Manual）。</summary>
public class FieldGrant
{
    public long Id { get; set; }
    public GrantSource SourceKind { get; set; }
    public long FieldId { get; set; }
    public long GroupId { get; set; }
    public long? AppId { get; set; }
}

/// <summary>已刪除貢獻者留下的欄位、選項或區塊；前擁有者只存 id 字串（應用程式已刪，不能是外鍵）。</summary>
public class AppOrphan
{
    public long Id { get; set; }
    public string FormerAppKey { get; set; } = "";
    public OrphanKind Kind { get; set; }
    /// <summary>原對照名稱，例如 partner/partner.industry。</summary>
    public string Name { get; set; } = "";
    public long? FieldId { get; set; }
    public long? OptionId { get; set; }
    public long? BlockId { get; set; }
}
