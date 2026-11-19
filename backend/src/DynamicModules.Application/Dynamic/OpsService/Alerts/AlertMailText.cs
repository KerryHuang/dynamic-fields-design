using System.Globalization;
using DynamicModules.Application.Dynamic.OpsService.Health;

namespace DynamicModules.Application.Dynamic.OpsService.Alerts;

/// <summary>
/// 告警信與事件標題的四語字串（維運設計 §17.2.2、§17.2.6、O2-U14；形式同 ReportText：鍵、四語、string.Format 參數）。
/// 繁中的事件標題必須與 o2b 的站內通知快照逐字相同。泰文與越南文待母語者校對。
/// </summary>
public static class AlertMailText
{
    public const string FactoryLocale = "zh-TW";

    public static readonly IReadOnlyList<string> Locales = ["zh-TW", "en-US", "th", "vi"];

    public const string
        Subject = nameof(Subject), SubjectDirect = nameof(SubjectDirect), DigestSubject = nameof(DigestSubject),
        DigestIntro = nameof(DigestIntro), DigestMore = nameof(DigestMore), DigestLine = nameof(DigestLine),
        TestSubject = nameof(TestSubject), TestBody = nameof(TestBody), DirectNote = nameof(DirectNote), TooLong = nameof(TooLong),
        Category = nameof(Category), Handler = nameof(Handler), Transition = nameof(Transition), Count = nameof(Count),
        FirstSeen = nameof(FirstSeen), LastSeen = nameof(LastSeen), CheckedAt = nameof(CheckedAt), Version = nameof(Version), Link = nameof(Link),
        // 事件標題（§17.2.2 標題表；組字只在 IncidentTitles）
        TitleServer = nameof(TitleServer), TitleClient = nameof(TitleClient), TitleRouteSuffix = nameof(TitleRouteSuffix),
        TitleAdminGranted = nameof(TitleAdminGranted), TitleAdminRevoked = nameof(TitleAdminRevoked), TitleHealth = nameof(TitleHealth),
        TitleChannel = nameof(TitleChannel), TitleChannelCode = nameof(TitleChannelCode),
        TitleConfig = nameof(TitleConfig), TitleRulePaused = nameof(TitleRulePaused);

    private static readonly Dictionary<string, (string Zh, string En, string Th, string Vi)> Table = new()
    {
        [Subject] = ("[Formwright][{0}] {1}（#{2}）", "[Formwright][{0}] {1} (#{2})", "[Formwright][{0}] {1} (#{2})", "[Formwright][{0}] {1} (#{2})"),
        [SubjectDirect] = ("[Formwright][{0}] {1}", "[Formwright][{0}] {1}", "[Formwright][{0}] {1}", "[Formwright][{0}] {1}"),
        [DigestSubject] = ("[Formwright] 過去一小時另有 {0} 則告警", "[Formwright] {0} more alerts in the past hour",
            "[Formwright] ชั่วโมงที่ผ่านมามีการแจ้งเตือนอีก {0} รายการ", "[Formwright] Một giờ qua còn {0} cảnh báo khác"),
        [DigestIntro] = ("過去一小時另有 {0} 則告警超過每小時寄信上限，沒有逐封寄出：", "{0} more alerts in the past hour exceeded the hourly email limit and were not sent one by one:",
            "ชั่วโมงที่ผ่านมามีการแจ้งเตือนอีก {0} รายการที่เกินขีดจำกัดอีเมลต่อชั่วโมงและไม่ได้ส่งทีละฉบับ:",
            "Một giờ qua còn {0} cảnh báo khác vượt giới hạn email mỗi giờ và không được gửi từng email:"),
        [DigestMore] = ("另有 {0} 則，請看監控頁。", "{0} more; see the monitoring page.", "มีอีก {0} รายการ โปรดดูหน้าติดตามระบบ", "Còn {0} cảnh báo khác, vui lòng xem trang giám sát."),
        [DigestLine] = ("#{0} [{1}] {2}：{3}", "#{0} [{1}] {2}: {3}", "#{0} [{1}] {2}: {3}", "#{0} [{1}] {2}: {3}"),
        [TestSubject] = ("[Formwright] 告警通道測試信", "[Formwright] Alert channel test email", "[Formwright] อีเมลทดสอบช่องทางแจ้งเตือน",
            "[Formwright] Email thử kênh cảnh báo"),
        [TestBody] = ("這是 Formwright 告警通道的測試信。收到這封信表示 SMTP 設定可以寄信，之後的告警信會寄到這組信箱。",
            "This is a test email from the Formwright alert channel. Receiving it means the SMTP settings work; future alert emails will be sent to these addresses.",
            "นี่คืออีเมลทดสอบจากช่องทางแจ้งเตือนของ Formwright เมื่อได้รับอีเมลนี้แสดงว่าการตั้งค่า SMTP ใช้ส่งอีเมลได้ อีเมลแจ้งเตือนต่อไปจะส่งมายังที่อยู่ชุดนี้",
            "Đây là email thử từ kênh cảnh báo Formwright. Nhận được email này nghĩa là cài đặt SMTP gửi được thư; các email cảnh báo sau sẽ gửi đến những địa chỉ này."),
        [DirectNote] = ("資料庫目前寫不進去，這封信由看門狗直接寄出；事件編號要等資料庫恢復、補寫事件後才會產生。",
            "The database cannot be written to right now, so this email was sent directly by the watchdog. The incident number will exist only after the database recovers and the incident is written.",
            "ขณะนี้ไม่สามารถเขียนฐานข้อมูลได้ อีเมลนี้จึงส่งโดยตรงจากตัวเฝ้าระบบ หมายเลขเหตุการณ์จะมีหลังจากฐานข้อมูลกลับมาและบันทึกเหตุการณ์แล้ว",
            "Hiện không thể ghi vào cơ sở dữ liệu nên email này được bộ giám sát gửi trực tiếp; số sự cố chỉ có sau khi cơ sở dữ liệu phục hồi và sự cố được ghi lại."),
        [TooLong] = ("…（內容過長，請看監控頁）", "… (too long; see the monitoring page)", "… (ยาวเกินไป โปรดดูหน้าติดตามระบบ)", "… (quá dài, vui lòng xem trang giám sát)"),
        [Category] = ("類別：{0}", "Category: {0}", "ประเภท: {0}", "Loại: {0}"),
        [Handler] = ("處理者：{0}", "Who handles it: {0}", "ผู้ดำเนินการ: {0}", "Người xử lý: {0}"),
        [Transition] = ("狀況：{0}", "What happened: {0}", "สถานการณ์: {0}", "Tình huống: {0}"),
        [Count] = ("次數：{0}", "Occurrences: {0}", "จำนวนครั้ง: {0}", "Số lần: {0}"),
        [FirstSeen] = ("首次：{0}", "First seen: {0}", "ครั้งแรก: {0}", "Lần đầu: {0}"),
        [LastSeen] = ("最近：{0}", "Last seen: {0}", "ล่าสุด: {0}", "Gần nhất: {0}"),
        [CheckedAt] = ("檢查時間：{0}", "Checked at: {0}", "เวลาที่ตรวจ: {0}", "Thời gian kiểm tra: {0}"),
        [Version] = ("版本：{0}", "Version: {0}", "เวอร์ชัน: {0}", "Phiên bản: {0}"),
        [Link] = ("事件連結：{0}", "Incident link: {0}", "ลิงก์เหตุการณ์: {0}", "Liên kết sự cố: {0}"),
        [TitleServer] = ("伺服器錯誤：{0}", "Server error: {0}", "ข้อผิดพลาดของเซิร์ฟเวอร์: {0}", "Lỗi máy chủ: {0}"),
        [TitleClient] = ("前端錯誤：{0}", "Browser error: {0}", "ข้อผิดพลาดฝั่งเบราว์เซอร์: {0}", "Lỗi phía trình duyệt: {0}"),
        [TitleRouteSuffix] = ("（{0}）", " ({0})", " ({0})", " ({0})"),
        [TitleAdminGranted] = ("系統管理員權限異動：授予", "System administrator rights changed: granted", "สิทธิ์ผู้ดูแลระบบเปลี่ยน: ให้สิทธิ์",
            "Thay đổi quyền quản trị hệ thống: cấp quyền"),
        [TitleAdminRevoked] = ("系統管理員權限異動：拿掉", "System administrator rights changed: revoked", "สิทธิ์ผู้ดูแลระบบเปลี่ยน: ถอนสิทธิ์",
            "Thay đổi quyền quản trị hệ thống: thu hồi quyền"),
        [TitleHealth] = ("健康檢查「{0}」{1}", "Health check \"{0}\": {1}", "การตรวจสุขภาพ \"{0}\": {1}", "Kiểm tra sức khỏe \"{0}\": {1}"),
        [TitleChannel] = ("告警信寄不出去（{0}）", "Alert emails cannot be sent ({0})", "ส่งอีเมลแจ้งเตือนไม่ได้ ({0})", "Không gửi được email cảnh báo ({0})"),
        [TitleChannelCode] = ("，伺服器回應 {0}", ", server replied {0}", ", เซิร์ฟเวอร์ตอบ {0}", ", máy chủ trả lời {0}"),
        [TitleConfig] = ("規則「{0}」設定有問題：{1}", "Rule \"{0}\" has a setup problem: {1}", "กฎ \"{0}\" มีปัญหาการตั้งค่า: {1}",
            "Quy tắc \"{0}\" có vấn đề cấu hình: {1}"),
        [TitleRulePaused] = ("任務規則「{0}」連續失敗 {1} 次，已自動暫停", "Task rule \"{0}\" failed {1} times in a row and was paused automatically",
            "กฎงาน \"{0}\" ล้มเหลวติดกัน {1} ครั้ง และถูกหยุดอัตโนมัติ", "Quy tắc công việc \"{0}\" lỗi liên tiếp {1} lần và đã tự động tạm dừng"),
    };

    /// <summary>代碼值的四語名稱：嚴重度、類別、處理者、轉換、健康狀態、寄送階段。</summary>
    private static readonly Dictionary<string, (string Zh, string En, string Th, string Vi)> Labels = new(StringComparer.Ordinal)
    {
        ["severity.info"] = ("資訊", "Info", "ข้อมูล", "Thông tin"),
        ["severity.warning"] = ("警告", "Warning", "เตือน", "Cảnh báo"),
        ["severity.critical"] = ("嚴重", "Critical", "ร้ายแรง", "Nghiêm trọng"),
        ["category.infra"] = ("基礎設施", "Infrastructure", "โครงสร้างพื้นฐาน", "Hạ tầng"),
        ["category.security"] = ("安全", "Security", "ความปลอดภัย", "Bảo mật"),
        ["category.defect"] = ("產品缺陷", "Product defect", "ข้อบกพร่องของผลิตภัณฑ์", "Lỗi sản phẩm"),
        ["category.config"] = ("客戶設定", "Customer configuration", "การตั้งค่าของลูกค้า", "Cấu hình của khách hàng"),
        ["handler.designer"] = ("需要設計者處理", "Needs the designer", "ต้องให้ผู้ออกแบบจัดการ", "Cần người thiết kế xử lý"),
        ["handler.it"] = ("需要 IT 處理", "Needs IT", "ต้องให้ฝ่าย IT จัดการ", "Cần bộ phận IT xử lý"),
        ["handler.vendor"] = ("廠商處理", "Handled by the vendor", "ผู้ให้บริการจัดการ", "Nhà cung cấp xử lý"),
        ["handler.admin"] = ("需要管理員確認", "Needs an administrator to confirm", "ต้องให้ผู้ดูแลระบบยืนยัน", "Cần quản trị viên xác nhận"),
        ["handler.contactVendor"] = ("產品問題：請附事件編號與錯誤代碼聯絡廠商", "Product problem: contact the vendor with the incident number and error code",
            "ปัญหาของผลิตภัณฑ์: โปรดติดต่อผู้ให้บริการพร้อมหมายเลขเหตุการณ์และรหัสข้อผิดพลาด", "Lỗi sản phẩm: vui lòng liên hệ nhà cung cấp kèm số sự cố và mã lỗi"),
        ["transition.opened"] = ("新發生", "New", "เกิดขึ้นใหม่", "Mới xảy ra"),
        ["transition.regressed"] = ("結案後再次發生", "Happened again after being resolved", "เกิดขึ้นอีกหลังปิดแล้ว", "Xảy ra lại sau khi đã đóng"),
        ["transition.escalated"] = ("嚴重度升高", "Severity increased", "ความรุนแรงเพิ่มขึ้น", "Mức nghiêm trọng tăng"),
        ["transition.renotify"] = ("仍未處理，再提醒一次", "Still not handled; reminder", "ยังไม่ได้จัดการ แจ้งเตือนอีกครั้ง", "Vẫn chưa xử lý, nhắc lại"),
        ["transition.occurred"] = ("再次異動", "Changed again", "เปลี่ยนแปลงอีกครั้ง", "Thay đổi lần nữa"),
        ["status.ok"] = ("正常", "OK", "ปกติ", "Bình thường"),
        ["status.warning"] = ("警告", "warning", "เตือน", "cảnh báo"),
        ["status.critical"] = ("嚴重", "critical", "ร้ายแรง", "nghiêm trọng"),
        ["status.error"] = ("檢查本身失敗", "the check itself failed", "การตรวจเองล้มเหลว", "bản thân kiểm tra bị lỗi"),
        ["stage.connect"] = ("連線", "connection", "การเชื่อมต่อ", "kết nối"),
        ["stage.tls"] = ("TLS", "TLS", "TLS", "TLS"),
        ["stage.auth"] = ("認證", "authentication", "การยืนยันตัวตน", "xác thực"),
        ["stage.send"] = ("寄送", "sending", "การส่ง", "gửi"),
        ["ruleError.event.tooDeep"] = ("連動事件串聯過深", "chained events go too deep", "เหตุการณ์ต่อเนื่องซ้อนลึกเกินไป", "chuỗi sự kiện liên động quá sâu"),
        ["ruleError.event.loop"] = ("連動事件形成循環", "chained events form a loop", "เหตุการณ์ต่อเนื่องวนเป็นลูป", "chuỗi sự kiện liên động tạo thành vòng lặp"),
        ["ruleError.check.conditionInvalid"] = ("檢查條件已不可用", "the check condition is no longer valid", "เงื่อนไขการตรวจสอบใช้ไม่ได้แล้ว",
            "điều kiện kiểm tra không còn dùng được"),
        ["ruleError.effect.targetUnavailable"] = ("效果的目標模組未啟用或已關閉", "the effect's target module is not active or is closed",
            "โมดูลเป้าหมายของผลลัพธ์ไม่ได้เปิดใช้หรือถูกปิด", "mô-đun đích của hiệu ứng chưa bật hoặc đã đóng"),
        ["ruleError.effect.missingDetail"] = ("找不到明細模組", "the detail module cannot be found", "ไม่พบโมดูลรายละเอียด", "không tìm thấy mô-đun chi tiết"),
        ["ruleError.effect.tooManyRows"] = ("明細列超過上限", "too many detail rows", "แถวรายละเอียดเกินขีดจำกัด", "số dòng chi tiết vượt giới hạn"),
        ["ruleError.effect.handlerMissing"] = ("找不到外掛處理器", "the plug-in handler cannot be found", "ไม่พบตัวจัดการปลั๊กอิน", "không tìm thấy trình xử lý plug-in"),
        ["ruleError.effect.unknownKind"] = ("未知的效果種類", "unknown effect kind", "ประเภทผลลัพธ์ที่ไม่รู้จัก", "loại hiệu ứng không xác định"),
        ["stage.config"] = ("設定不完整，請重填密碼", "settings incomplete; re-enter the password", "การตั้งค่าไม่ครบ โปรดกรอกรหัสผ่านใหม่",
            "cài đặt chưa đầy đủ, vui lòng nhập lại mật khẩu"),
    };

    /// <summary>健康檢查名稱的其他三語（繁中用 <see cref="HealthCheckCatalog"/> 的名稱，維持單一來源）。</summary>
    private static readonly Dictionary<string, (string En, string Th, string Vi)> CheckNames = new(StringComparer.Ordinal)
    {
        [HealthCheckCatalog.DatabaseWritable] = ("Database writable", "ฐานข้อมูลเขียนได้", "Cơ sở dữ liệu ghi được"),
        [HealthCheckCatalog.StorageDiskFree] = ("Free disk space", "พื้นที่ดิสก์คงเหลือ", "Dung lượng đĩa còn trống"),
        [HealthCheckCatalog.SchedulerJobs] = ("Scheduler", "ตัวจัดตารางงาน", "Bộ lập lịch"),
        [HealthCheckCatalog.DatabaseWalSize] = ("Database WAL file size", "ขนาดไฟล์ WAL ของฐานข้อมูล", "Kích thước tệp WAL của cơ sở dữ liệu"),
        [HealthCheckCatalog.DatabaseIntegrity] = ("Data integrity", "ความถูกต้องของข้อมูล", "Tính toàn vẹn dữ liệu"),
        [HealthCheckCatalog.BackupFreshness] = ("Backup freshness", "ความใหม่ของการสำรองข้อมูล", "Độ mới của bản sao lưu"),
        [HealthCheckCatalog.SecurityLoginFailures] = ("Failed sign-ins", "การเข้าสู่ระบบล้มเหลว", "Số lần đăng nhập thất bại"),
    };

    /// <summary>信件語系：四語之一，其他（含 null）回出廠的繁中（O2 出廠；p3 後改跟系統預設語系，O2-U14）。</summary>
    public static string Locale(string? mailLocale) => mailLocale is not null && Locales.Contains(mailLocale) ? mailLocale : FactoryLocale;

    public static string Get(string locale, string key, params object[] args)
    {
        var text = Pick(Table[key], locale);
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>代碼值的名稱（<c>severity.critical</c>…）；沒登記的回代碼本身。</summary>
    public static string Label(string locale, string key) => Labels.TryGetValue(key, out var e) ? Pick(e, locale) : key[(key.IndexOf('.') + 1)..];

    public static string CheckName(string locale, string code)
    {
        var zh = HealthCheckCatalog.Find(code)?.ZhName ?? code;
        if (!CheckNames.TryGetValue(code, out var e)) return zh;
        return locale switch { "en-US" => e.En, "th" => e.Th, "vi" => e.Vi, _ => zh };
    }

    private static string Pick((string Zh, string En, string Th, string Vi) e, string locale) =>
        locale switch { "en-US" => e.En, "th" => e.Th, "vi" => e.Vi, _ => e.Zh };
}
