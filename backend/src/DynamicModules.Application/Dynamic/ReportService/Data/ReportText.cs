using DynamicModules.Domain.ValueObjects;
using TK = DynamicModules.Domain.Reports.TaskReportFields.TextKeys;

namespace DynamicModules.Application.Dynamic.ReportService.Data;

/// <summary>
/// 報表結果與欄位值顯示（FieldDisplay：布林、參照狀態）用的系統字串（spec §14 四語系）。存檔驗證的 400 訊息不在這裡，照現有慣例寫繁中。泰文與越南文待母語者校對。
/// 這張表是 <see cref="Common.Localization.IMessageCatalog"/> 的內建層之一；常數值帶 <c>report.</c> 前綴，是語系包 <c>server</c> 段的命名空間，
/// 鍵是語系包契約，改名要留別名（語系包設計 §5.1）。
/// </summary>
public static class ReportText
{
    public const string
        ModuleDisabled = "report." + nameof(ModuleDisabled), DetailAsMain = "report." + nameof(DetailAsMain), NoModuleRead = "report." + nameof(NoModuleRead), NoChildRead = "report." + nameof(NoChildRead),
        FieldGone = "report." + nameof(FieldGone), FieldGoneNamed = "report." + nameof(FieldGoneNamed), TypeChanged = "report." + nameof(TypeChanged), NoPermissionTo = "report." + nameof(NoPermissionTo),
        HiddenField = "report." + nameof(HiddenField), NotUsable = "report." + nameof(NotUsable), TooMany = "report." + nameof(TooMany), TooManyTasks = "report." + nameof(TooManyTasks), TooManyToLoad = "report." + nameof(TooManyToLoad), SqlTimeout = "report." + nameof(SqlTimeout),
        RowMeaning = "report." + nameof(RowMeaning), Count = "report." + nameof(Count), Sum = "report." + nameof(Sum), Avg = "report." + nameof(Avg), Min = "report." + nameof(Min), Max = "report." + nameof(Max), Ratio = "report." + nameof(Ratio),
        CountIf = "report." + nameof(CountIf), CountIfEq = "report." + nameof(CountIfEq), Median = "report." + nameof(Median), Conditional = "report." + nameof(Conditional), ConditionalEq = "report." + nameof(ConditionalEq),
        Title = "report." + nameof(Title), FieldPath = "report." + nameof(FieldPath), Deleted = "report." + nameof(Deleted), NoPermission = "report." + nameof(NoPermission), Blank = "report." + nameof(Blank),
        Yes = "report." + nameof(Yes), No = "report." + nameof(No),
        ExportTotal = "report." + nameof(ExportTotal), DedupedNote = "report." + nameof(DedupedNote), Unshared = "report." + nameof(Unshared), ReportGone = "report." + nameof(ReportGone);

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
        [TooManyTasks] = ("資料太多，請縮小條件；部門篩選不會減少載入量，請縮小期間", "Too much data, please narrow the conditions. The department filter does not reduce what is loaded; narrow the period instead",
            "ข้อมูลมากเกินไป โปรดจำกัดเงื่อนไข ตัวกรองแผนกไม่ได้ลดปริมาณข้อมูลที่โหลด โปรดจำกัดช่วงเวลา", "Dữ liệu quá nhiều, vui lòng thu hẹp điều kiện. Bộ lọc phòng ban không giảm lượng dữ liệu được tải, hãy thu hẹp kỳ"),
        [TooManyToLoad] = ("來源資料超過系統的載入上限，而且這份報表的條件與欄位無法在資料庫端處理；請把用到的欄位設為可篩選，或聯絡管理員",
            "The source data exceeds the system's load limit, and this report's conditions and fields cannot be processed in the database. Mark the fields it uses as filterable, or contact an administrator",
            "ข้อมูลต้นทางเกินขีดจำกัดการโหลดของระบบ และเงื่อนไขกับฟิลด์ของรายงานนี้ประมวลผลในฐานข้อมูลไม่ได้ โปรดตั้งฟิลด์ที่ใช้เป็นกรองได้ หรือติดต่อผู้ดูแลระบบ",
            "Dữ liệu nguồn vượt quá giới hạn tải của hệ thống và các điều kiện, trường của báo cáo này không thể xử lý trong cơ sở dữ liệu. Hãy đặt các trường đang dùng là có thể lọc, hoặc liên hệ quản trị viên"),
        [SqlTimeout] = ("報表執行逾時，請加上條件縮小範圍", "The report took too long to run. Add conditions to narrow it down",
            "รายงานใช้เวลานานเกินไป โปรดเพิ่มเงื่อนไขเพื่อจำกัดขอบเขต", "Báo cáo chạy quá thời gian, vui lòng thêm điều kiện để thu hẹp phạm vi"),
        [RowMeaning] = ("一筆{0}", "a record in {0}", "{0} หนึ่งรายการ", "Một {0}"),
        [Count] = ("{0}筆數", "{0} count", "จำนวน {0}", "Số {0}"),
        [Sum] = ("{0} 加總", "{0} sum", "ผลรวม {0}", "Tổng {0}"),
        [Avg] = ("{0} 平均", "{0} average", "ค่าเฉลี่ย {0}", "Trung bình {0}"),
        [Min] = ("{0} 最小", "{0} min", "ค่าต่ำสุด {0}", "Nhỏ nhất {0}"),
        [Max] = ("{0} 最大", "{0} max", "ค่าสูงสุด {0}", "Lớn nhất {0}"),
        [Ratio] = ("{0} 條件占比", "{0} condition share", "สัดส่วนตามเงื่อนไข {0}", "Tỷ lệ theo điều kiện {0}"),
        [CountIf] = ("{0} 符合條件筆數", "{0} matching count", "จำนวนที่ตรงเงื่อนไข {0}", "Số lượng khớp điều kiện {0}"),
        [CountIfEq] = ("{0}＝{1} 筆數", "{0} = {1} count", "จำนวน {0} = {1}", "Số lượng {0} = {1}"),
        [Median] = ("{0} 中位數", "{0} median", "ค่ามัธยฐาน {0}", "Trung vị {0}"),
        [Conditional] = ("{0}（符合條件）", "{0} (matching)", "{0} (ตามเงื่อนไข)", "{0} (khớp điều kiện)"),
        [ConditionalEq] = ("{0}（{1}＝{2}）", "{0} ({1} = {2})", "{0} ({1} = {2})", "{0} ({1} = {2})"),
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
        // 任務報表（Domain 的 TaskReportFields 只留鍵，語系包 i4c 計畫判斷 9）：欄位名
        [TK.FieldTitle] = ("標題", "Title", "ชื่องาน", "Tiêu đề"),
        [TK.FieldStatus] = ("狀態", "Status", "สถานะ", "Trạng thái"),
        [TK.FieldAssignee] = ("負責人", "Assignee", "ผู้รับผิดชอบ", "Người phụ trách"),
        [TK.FieldAssigneeDepartment] = ("負責人部門", "Assignee department", "แผนกของผู้รับผิดชอบ", "Phòng ban người phụ trách"),
        [TK.FieldCreator] = ("建立者", "Created by", "ผู้สร้าง", "Người tạo"),
        [TK.FieldDueDate] = ("到期日", "Due date", "วันครบกำหนด", "Ngày đến hạn"),
        [TK.FieldCreatedTime] = ("建立時間", "Created at", "เวลาที่สร้าง", "Thời gian tạo"),
        [TK.FieldCompletedTime] = ("完成時間", "Completed at", "เวลาที่เสร็จ", "Thời gian hoàn thành"),
        [TK.FieldSource] = ("來源", "Source", "ที่มา", "Nguồn"),
        [TK.FieldModule] = ("所屬模組", "Module", "โมดูล", "Mô-đun"),
        [TK.FieldRule] = ("任務規則", "Task rule", "กฎงาน", "Quy tắc công việc"),
        [TK.FieldOverdue] = ("是否逾期", "Overdue", "เกินกำหนดหรือไม่", "Quá hạn"),
        [TK.FieldCompletion] = ("完成狀況", "Completion", "สถานะการเสร็จ", "Tình trạng hoàn thành"),
        [TK.FieldCompletionDays] = ("完成天數", "Days to complete", "จำนวนวันที่ใช้", "Số ngày hoàn thành"),
        [TK.FieldDelayDays] = ("延遲天數", "Days late", "จำนวนวันที่ล่าช้า", "Số ngày trễ"),
        [TK.FieldLevel] = ("層級", "Level", "ระดับ", "Cấp"),
        [TK.FieldEventKind] = ("事件種類", "Event type", "ประเภทเหตุการณ์", "Loại sự kiện"),
        [TK.FieldEventDate] = ("事件日期", "Event date", "วันที่ของเหตุการณ์", "Ngày sự kiện"),
        // 任務報表：狀態與前端 status.* 同字（K2），改時兩邊一起改
        [TK.StatusTodo] = ("待辦", "To do", "รอดำเนินการ", "Cần làm"),
        [TK.StatusInProgress] = ("進行中", "In progress", "กำลังดำเนินการ", "Đang làm"),
        [TK.StatusDone] = ("已完成", "Done", "เสร็จแล้ว", "Hoàn thành"),
        [TK.StatusCancelled] = ("已取消", "Cancelled", "ยกเลิกแล้ว", "Đã hủy"),
        [TK.SourcePersonal] = ("個人任務", "Personal task", "งานส่วนตัว", "Công việc cá nhân"),
        [TK.SourceRecord] = ("記錄上建立", "Created on a record", "สร้างจากระเบียน", "Tạo trên bản ghi"),
        [TK.SourceRule] = ("規則產生", "Generated by a rule", "สร้างโดยกฎ", "Do quy tắc tạo"),
        [TK.CompletionOnTime] = ("準時完成", "Completed on time", "เสร็จตรงเวลา", "Hoàn thành đúng hạn"),
        [TK.CompletionLate] = ("逾期完成", "Completed late", "เสร็จล่าช้า", "Hoàn thành trễ hạn"),
        [TK.CompletionNoDue] = ("無到期日完成", "Completed, no due date", "เสร็จโดยไม่มีวันครบกำหนด", "Hoàn thành, không có hạn"),
        [TK.CompletionNotDone] = ("未完成", "Not completed", "ยังไม่เสร็จ", "Chưa hoàn thành"),
        [TK.LevelStandalone] = ("獨立任務", "Standalone task", "งานเดี่ยว", "Công việc độc lập"),
        [TK.LevelParent] = ("父任務", "Parent task", "งานหลัก", "Công việc cha"),
        [TK.LevelChild] = ("子任務", "Subtask", "งานย่อย", "Công việc con"),
        [TK.EventCreated] = ("建立", "Created", "สร้าง", "Tạo"),
        [TK.EventCompleted] = ("完成", "Completed", "เสร็จ", "Hoàn thành"),
        [TK.BlankAssignee] = ("未指派", "Unassigned", "ยังไม่มอบหมาย", "Chưa giao"),
        [TK.BlankAssigneeDepartment] = ("（無部門）", "(No department)", "(ไม่มีแผนก)", "(Không có phòng ban)"),
        [TK.BlankCreator] = ("系統", "System", "ระบบ", "Hệ thống"),
        [TK.BlankModule] = ("（無）", "(None)", "(ไม่มี)", "(Không có)"),
        [TK.BlankRule] = ("（無）", "(None)", "(ไม่มี)", "(Không có)"),
        [TK.DatasetTasks] = ("任務", "Task", "งาน", "Công việc"),
        [TK.DatasetTaskEvents] = ("任務事件（建立或完成）", "Task event (created or completed)", "เหตุการณ์ของงาน (สร้างหรือเสร็จ)", "Sự kiện công việc (tạo hoặc hoàn thành)"),
    };

    /// <summary>給 <see cref="Common.Localization.BuiltinServerText"/> 合併用（鍵即常數值，已帶 <c>report.</c>）。</summary>
    internal static IEnumerable<KeyValuePair<string, (string Zh, string En, string Th, string Vi)>> Entries => Table;
}
