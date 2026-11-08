using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public enum AppStatus { Open, Closed }

/// <summary>
/// 已開啟的應用程式：只是帳，記錄「哪個套件、哪些模組、現在開或關」。開啟後模組與設定都屬於客戶，
/// 執行期沒有任何程式依賴套件檔；Module 本身不指向應用程式，所以沒有應用程式的模組不受影響。
/// </summary>
public class App : AuditableEntity
{
    public long Id { get; set; }
    /// <summary>= manifest.id，全站唯一。</summary>
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
    public string? Icon { get; set; }
    /// <summary>manifest.version，只是資訊，不參與任何判斷。</summary>
    public string PackageVersion { get; set; } = "";
    public int FormatVersion { get; set; }
    /// <summary>builtIn 或 imported。</summary>
    public string Source { get; set; } = "";
    public AppStatus Status { get; set; }
    /// <summary>manifest.requires 的副本（JSON）；相依判斷的依據之一。</summary>
    public AppRequires Requires { get; set; } = new();
    /// <summary>GUID 字串，每次寫入換新值，避免兩位管理員同時開關。</summary>
    public string RowVersion { get; set; } = "";
    public List<AppModule> Modules { get; set; } = [];
    public List<AppReport> Reports { get; set; } = [];
    /// <summary>開啟當時 integrations 的原文副本（JSON 陣列）；調和只看它，不看目錄，所以目錄換版不影響已開啟的應用程式。</summary>
    public string? IntegrationsJson { get; set; }

    public void TouchRowVersion() => RowVersion = Guid.NewGuid().ToString();
}

/// <summary>套件宣告的相依：其他應用程式的模組，以及既有實體。</summary>
public class AppRequires
{
    public List<AppRequirement> Apps { get; set; } = [];
    public List<string> Entities { get; set; } = [];
}

public class AppRequirement
{
    public string Id { get; set; } = "";
    public List<string> Modules { get; set; } = [];
}

/// <summary>套件內模組代號對實際模組；Code 開啟後永遠不變，其他套件靠它引用。</summary>
public class AppModule
{
    public long AppId { get; set; }
    public string Code { get; set; } = "";
    public long ModuleId { get; set; }
}

/// <summary>套件內報表代號對實際報表；匯出重建 reports[]，刪除應用程式時一併刪除報表。</summary>
public class AppReport
{
    public long AppId { get; set; }
    public long ReportId { get; set; }
    public string Code { get; set; } = "";
}

/// <summary>
/// 開啟時的代號對照：套件內的限定欄位名 &lt;模組代號&gt;.&lt;欄位代號&gt; 對實際欄位。外鍵 cascade，欄位被刪對照列就跟著消失，
/// 整合靠它解析夥伴的符號，匯出靠它保留原始代號。
/// </summary>
public class AppField
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public long FieldId { get; set; }
}

/// <summary>&lt;模組代號&gt;.&lt;選項代號&gt; 對實際選項。</summary>
public class AppOption
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public long OptionId { get; set; }
}

/// <summary>&lt;模組代號&gt;.&lt;區塊代號&gt; 對實際區塊；整合要把欄位加進擁有者既有的區塊。</summary>
public class AppBlock
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public long BlockId { get; set; }
}

/// <summary>
/// 應用程式宣告的一個整合（可選相依）的執行狀態。Status 只是上一次調和的結果，守衛每次重算；
/// ItemsJson 登記整合建立的欄位、事件、檢查（見設計 §4）。
/// </summary>
public class AppIntegration
{
    public long AppId { get; set; }
    public string Code { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Installed { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string ItemsJson { get; set; } = "{}";
}

/// <summary>匯入的套件；Json 是收到的原文（每次讀取才升級，不回寫）。</summary>
public class AppPackage : AuditableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Version { get; set; } = "";
    public string Json { get; set; } = "";
}
