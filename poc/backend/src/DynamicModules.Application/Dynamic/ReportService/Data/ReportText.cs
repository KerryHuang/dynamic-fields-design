using System.Globalization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService.Data;

/// <summary>報表結果與欄位值顯示（FieldDisplay：布林、參照狀態）用的系統字串（spec §14 四語系）。存檔驗證的 400 訊息不在這裡，照現有慣例寫繁中。泰文與越南文待母語者校對。</summary>
public static class ReportText
{
    public const string
        ModuleDisabled = nameof(ModuleDisabled), DetailAsMain = nameof(DetailAsMain), NoModuleRead = nameof(NoModuleRead), NoChildRead = nameof(NoChildRead),
        FieldGone = nameof(FieldGone), FieldGoneNamed = nameof(FieldGoneNamed), TypeChanged = nameof(TypeChanged), NoPermissionTo = nameof(NoPermissionTo),
        HiddenField = nameof(HiddenField), NotUsable = nameof(NotUsable), TooMany = nameof(TooMany), TooManyToLoad = nameof(TooManyToLoad), SqlTimeout = nameof(SqlTimeout),
        RowMeaning = nameof(RowMeaning), Count = nameof(Count), Sum = nameof(Sum), Avg = nameof(Avg), Min = nameof(Min), Max = nameof(Max), Ratio = nameof(Ratio),
        Title = nameof(Title), FieldPath = nameof(FieldPath), Deleted = nameof(Deleted), NoPermission = nameof(NoPermission), Blank = nameof(Blank),
        Yes = nameof(Yes), No = nameof(No),
        ExportTotal = nameof(ExportTotal), DedupedNote = nameof(DedupedNote), Unshared = nameof(Unshared), ReportGone = nameof(ReportGone);

    private static readonly Dictionary<string, (string Zh, string En, string Th, string Vi)> Table = new()
    {
        [ModuleDisabled] = ("模組已停用", "The module is disabled", "โมดูลถูกปิดใช้งาน", "Mô-đun đã bị vô hiệu hóa"),
        [DetailAsMain] = ("明細模組不能當主模組", "A detail module cannot be the main module", "โมดูลรายละเอียดเป็นโมดูลหลักไม่ได้", "Mô-đun chi tiết không thể làm mô-đun chính"),
        [NoModuleRead] = ("無權限檢視這個模組", "No permission to view this module", "ไม่มีสิทธิ์ดูโมดูลนี้", "Không có quyền xem mô-đun này"),
        [NoChildRead] = ("無權限檢視合併的子模組", "No permission to view the merged child module", "ไม่มีสิทธิ์ดูโมดูลย่อยที่รวมไว้", "Không có quyền xem mô-đun con đã gộp"),
        [FieldGone] = ("欄位已不存在", "A field no longer exists", "ฟิลด์ไม่มีอยู่แล้ว", "Trường không còn tồn tại"),
        [FieldGoneNamed] = ("欄位已不存在：{0}", "Field no longer exists: {0}", "ฟิลด์ไม่มีอยู่แล้ว: {0}", "Trường không còn tồn tại: {0}"),
        [TypeChanged] = ("欄位型別已變更：{0}", "Field type has changed: {0}", "ประเภทฟิลด์เปลี่ยนแล้ว: {0}", "Kiểu trường đã thay đổi: {0}"),
        [NoPermissionTo] = ("無權限檢視：{0}", "No permission to view: {0}", "ไม่มีสิทธิ์ดู: {0}", "Không có quyền xem: {0}"),
        [HiddenField] = ("隱藏欄位", "Hidden field", "ฟิลด์ที่ถูกซ่อน", "Trường bị ẩn"),
        [NotUsable] = ("這個欄位不能用於報表：{0}", "This field cannot be used in reports: {0}", "ฟิลด์นี้ใช้ในรายงานไม่ได้: {0}", "Trường này không dùng được trong báo cáo: {0}"),
        [TooMany] = ("資料太多，請縮小條件", "Too much data, please narrow the conditions", "ข้อมูลมากเกินไป โปรดจำกัดเงื่อนไข", "Dữ liệu quá nhiều, vui lòng thu hẹp điều kiện"),
        [TooManyToLoad] = ("來源資料超過系統的載入上限，而且這份報表的條件與欄位無法在資料庫端處理；請把用到的欄位設為可篩選，或聯絡管理員",
            "The source data exceeds the system's load limit, and this report's conditions and fields cannot be processed in the database. Mark the fields it uses as filterable, or contact an administrator",
            "ข้อมูลต้นทางเกินขีดจำกัดการโหลดของระบบ และเงื่อนไขกับฟิลด์ของรายงานนี้ประมวลผลในฐานข้อมูลไม่ได้ โปรดตั้งฟิลด์ที่ใช้เป็นกรองได้ หรือติดต่อผู้ดูแลระบบ",
            "Dữ liệu nguồn vượt quá giới hạn tải của hệ thống và các điều kiện, trường của báo cáo này không thể xử lý trong cơ sở dữ liệu. Hãy đặt các trường đang dùng là có thể lọc, hoặc liên hệ quản trị viên"),
        [SqlTimeout] = ("報表執行逾時，請加上條件縮小範圍", "The report took too long to run. Add conditions to narrow it down",
            "รายงานใช้เวลานานเกินไป โปรดเพิ่มเงื่อนไขเพื่อจำกัดขอบเขต", "Báo cáo chạy quá thời gian, vui lòng thêm điều kiện để thu hẹp phạm vi"),
        [RowMeaning] = ("一筆{0}", "One {0}", "{0} หนึ่งรายการ", "Một {0}"),
        [Count] = ("{0}筆數", "{0} count", "จำนวน {0}", "Số {0}"),
        [Sum] = ("{0} 加總", "{0} sum", "ผลรวม {0}", "Tổng {0}"),
        [Avg] = ("{0} 平均", "{0} average", "ค่าเฉลี่ย {0}", "Trung bình {0}"),
        [Min] = ("{0} 最小", "{0} min", "ค่าต่ำสุด {0}", "Nhỏ nhất {0}"),
        [Max] = ("{0} 最大", "{0} max", "ค่าสูงสุด {0}", "Lớn nhất {0}"),
        [Ratio] = ("{0} 條件占比", "{0} condition share", "สัดส่วนตามเงื่อนไข {0}", "Tỷ lệ theo điều kiện {0}"),
        [Title] = ("標題", "Title", "ชื่อเรื่อง", "Tiêu đề"),
        [FieldPath] = ("{0}．{1}", "{0}.{1}", "{0}.{1}", "{0}.{1}"),
        [Deleted] = ("（已刪除）", "(Deleted)", "(ถูกลบแล้ว)", "(Đã xóa)"),
        [NoPermission] = ("（無權限）", "(No permission)", "(ไม่มีสิทธิ์)", "(Không có quyền)"),
        [Yes] = ("是", "Yes", "ใช่", "Có"),
        [No] = ("否", "No", "ไม่ใช่", "Không"),
        [Blank] = ("（空白）", "(Blank)", "(ว่าง)", "(Trống)"),
        [ExportTotal] = ("總計", "Total", "รวม", "Tổng"),
        [Unshared] = ("此報表已不再分享", "This report is no longer shared", "รายงานนี้ไม่ได้แชร์อีกต่อไป", "Báo cáo này không còn được chia sẻ"),
        [ReportGone] = ("報表已不存在", "The report no longer exists", "ไม่มีรายงานนี้อีกต่อไป", "Báo cáo không còn tồn tại"),
        [DedupedNote] = ("總計每筆記錄只算一次；同一筆可能出現在多列或多個分組，所以各列相加不一定等於總計。",
            "The total counts each record once. One record can appear on several rows or in several groups, so adding up the rows may not equal the total.",
            "ยอดรวมนับแต่ละรายการเพียงครั้งเดียว รายการเดียวอาจปรากฏในหลายแถวหรือหลายกลุ่ม ผลรวมของแถวจึงอาจไม่เท่ากับยอดรวม",
            "Tổng chỉ tính mỗi bản ghi một lần. Một bản ghi có thể xuất hiện ở nhiều dòng hoặc nhiều nhóm, nên cộng các dòng có thể không bằng tổng."),
    };

    /// <summary>沒有對應語系時退回繁中，與 <see cref="I18nText.FallbackLocale"/> 一致。</summary>
    public static string Get(string? locale, string key, params object[] args)
    {
        var e = Table[key];
        var text = locale switch { "en-US" => e.En, "th" => e.Th, "vi" => e.Vi, _ => e.Zh };
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }
}
