using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.SharedMasters;

/// <summary>
/// 貢獻欄位的限制（共用主檔設計 §4.3）。套件驗證（contributions[]）與設計器在貢獻者區塊新增、修改欄位（§5.4、§15.2「照貢獻欄位處理」）
/// 共用這一份：型別、不得必填、不能是明細、參照不得 cascade、參照只能指向貢獻者自己的模組（必須 setNull）或主檔、公式只引用主檔基本欄位與同一貢獻者的欄位。
/// 錯誤的 Path 是相對於欄位的子路徑（"" 或 ".type"、".onDelete"），套件驗證據此組出錯誤鍵。
/// </summary>
public static class ContributionFieldRules
{
    public static readonly IReadOnlySet<FieldType> AllowedTypes = new HashSet<FieldType>
    {
        FieldType.Text, FieldType.Textarea, FieldType.Email, FieldType.Phone, FieldType.Url,
        FieldType.Integer, FieldType.Decimal, FieldType.Currency, FieldType.Percent,
        FieldType.Date, FieldType.DateTime, FieldType.Time,
        FieldType.Enum, FieldType.Boolean, FieldType.User, FieldType.Reference, FieldType.Formula
    };

    public const string TargetNotAllowed = "貢獻的參照欄位只能指向本套件的模組或 requires.masters 的主檔模組";
    public const string OwnModuleMustSetNull = "指向本套件模組的貢獻參照欄位必須是 setNull";

    public static string FormulaOutOfScope(string code) => $"公式引用了 {code}，貢獻的公式只能引用主檔的基本欄位與本貢獻的欄位";

    /// <summary>參照欄位的目標：貢獻者自己的模組、主檔模組，或兩者都不是。</summary>
    public enum Target { OwnModule, Master, Other }

    public static IEnumerable<(string Path, string Message)> ShapeErrors(FieldType type, bool required, bool detail, bool cascade)
    {
        if (!AllowedTypes.Contains(type))
            yield return (".type", $"貢獻欄位不能是 {type.ToString().ToLowerInvariant()}（不收自動編號、複選、附件、彙總）");
        if (required) yield return ("", "貢獻欄位不得必填（別的應用程式建立主檔記錄時不該被擋住）");
        if (detail) yield return ("", "貢獻欄位不能是明細的表頭參照");
        if (type == FieldType.Reference && cascade)
            yield return (".onDelete", "貢獻的參照欄位不能是 cascade（主檔記錄不能因為別的應用程式刪資料而消失）");
    }

    /// <summary>指向自己模組的要 setNull：刪除自己的記錄時清掉追溯值，不然轉換過一次之後那筆記錄就刪不掉（§4.3）。</summary>
    public static (string Path, string Message)? TargetError(Target target, bool setNull) => target switch
    {
        Target.OwnModule when !setNull => (".onDelete", OwnModuleMustSetNull),
        Target.Other => ("", TargetNotAllowed),
        _ => null
    };
}
