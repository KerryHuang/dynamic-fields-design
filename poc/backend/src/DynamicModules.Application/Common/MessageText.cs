using System.Globalization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 後端回給使用者的訊息（四語系，形式同 ReportText：鍵、四語、string.Format 參數）。繁中必須與搬進來之前的字面值逐字相同。
/// 已收共用訊息、登入與 Session、記錄寫入（欄位值驗證、狀態轉移、刪除、唯讀鎖定）；其餘訊息仍是呼叫端的中文字面值，之後分段搬。泰文與越南文待母語者校對。
/// </summary>
public static class MessageText
{
    public const string
        Forbidden = nameof(Forbidden), DatabaseBusy = nameof(DatabaseBusy), IdempotencyKeyReused = nameof(IdempotencyKeyReused),
        ModuleNotFound = nameof(ModuleNotFound), ResourceNotFound = nameof(ResourceNotFound),
        Stale = nameof(Stale), LedgerFreezeConfirm = nameof(LedgerFreezeConfirm),
        LoginFailed = nameof(LoginFailed), SignInRequired = nameof(SignInRequired), PasswordChangeRequired = nameof(PasswordChangeRequired),
        UserNotFound = nameof(UserNotFound), OldPasswordWrong = nameof(OldPasswordWrong), NewPasswordSameAsOld = nameof(NewPasswordSameAsOld),
        OldPasswordRequired = nameof(OldPasswordRequired), NewPasswordTooShort = nameof(NewPasswordTooShort), PasswordTooShort = nameof(PasswordTooShort),
        UserIdFormat = nameof(UserIdFormat), DisplayNameRequired = nameof(DisplayNameRequired), DisplayNameTooLong = nameof(DisplayNameTooLong),
        // 欄位值驗證（鍵同 Domain 的 FieldErrors，UserNotFound 共用上面那條）
        MustBeText = nameof(MustBeText), TooLong = nameof(TooLong), MustBeNumber = nameof(MustBeNumber),
        ScaleExceeded = nameof(ScaleExceeded), DigitsExceeded = nameof(DigitsExceeded), MustBeInteger = nameof(MustBeInteger),
        MustBeBoolean = nameof(MustBeBoolean), DateFormat = nameof(DateFormat), DateTimeFormat = nameof(DateTimeFormat),
        TimeFormat = nameof(TimeFormat), EmailFormat = nameof(EmailFormat), PhoneFormat = nameof(PhoneFormat), UrlFormat = nameof(UrlFormat),
        OptionInvalid = nameof(OptionInvalid), RefNotFound = nameof(RefNotFound), RefStateMismatch = nameof(RefStateMismatch),
        AttachmentNotList = nameof(AttachmentNotList), AttachmentForged = nameof(AttachmentForged),
        // 記錄寫入、狀態轉移、刪除、唯讀鎖定
        Required = nameof(Required), RecordReadOnly = nameof(RecordReadOnly), OptionOutOfRange = nameof(OptionOutOfRange),
        UnknownDetail = nameof(UnknownDetail), DetailRowNotFound = nameof(DetailRowNotFound), TooManyDetailRows = nameof(TooManyDetailRows),
        WorkflowNotInitial = nameof(WorkflowNotInitial), WorkflowNotAllowed = nameof(WorkflowNotAllowed), WorkflowNotPermitted = nameof(WorkflowNotPermitted),
        WorkflowLockedDetails = nameof(WorkflowLockedDetails), WorkflowLockedDelete = nameof(WorkflowLockedDelete),
        DeleteReferenced = nameof(DeleteReferenced), DeleteLockedReferenced = nameof(DeleteLockedReferenced),
        DeleteBookedReferenced = nameof(DeleteBookedReferenced), DeleteTooDeep = nameof(DeleteTooDeep),
        ModuleNotAvailable = nameof(ModuleNotAvailable), RecordNotFound = nameof(RecordNotFound),
        AppClosedReadOnly = nameof(AppClosedReadOnly), AppClosedNoRewrite = nameof(AppClosedNoRewrite),
        FrozenField = nameof(FrozenField), FrozenFieldHidden = nameof(FrozenFieldHidden),
        LedgerImmutable = nameof(LedgerImmutable), LedgerSchemaImmutable = nameof(LedgerSchemaImmutable), LedgerBackstop = nameof(LedgerBackstop),
        // 找不到某個東西
        BlockNotFound = nameof(BlockNotFound), FieldNotFound = nameof(FieldNotFound), OptionNotFound = nameof(OptionNotFound),
        EventNotFound = nameof(EventNotFound), CheckNotFound = nameof(CheckNotFound), ConversionNotFound = nameof(ConversionNotFound),
        AttachmentFieldNotFound = nameof(AttachmentFieldNotFound), AttachmentNotFound = nameof(AttachmentNotFound);

    private static readonly Dictionary<string, (string Zh, string En, string Th, string Vi)> Table = new()
    {
        [Forbidden] = ("沒有權限執行此操作", "You do not have permission to perform this action",
            "คุณไม่มีสิทธิ์ดำเนินการนี้", "Bạn không có quyền thực hiện thao tác này"),
        [DatabaseBusy] = ("系統忙碌中，請稍後再試", "The system is busy. Please try again later",
            "ระบบไม่ว่าง โปรดลองอีกครั้งในภายหลัง", "Hệ thống đang bận, vui lòng thử lại sau"),
        [IdempotencyKeyReused] = ("同一個操作識別碼（Idempotency-Key）已用在不同內容的請求",
            "This operation ID (Idempotency-Key) was already used for a request with different content",
            "รหัสการดำเนินการนี้ (Idempotency-Key) ถูกใช้กับคำขอที่มีเนื้อหาต่างกันไปแล้ว",
            "Mã thao tác này (Idempotency-Key) đã được dùng cho một yêu cầu có nội dung khác"),
        [ModuleNotFound] = ("找不到模組", "Module not found", "ไม่พบโมดูล", "Không tìm thấy mô-đun"),
        [ResourceNotFound] = ("找不到資源", "Resource not found", "ไม่พบทรัพยากร", "Không tìm thấy tài nguyên"),
        [Stale] = ("資料已被其他人修改，請重新載入", "This data was changed by someone else. Please reload",
            "ข้อมูลนี้ถูกผู้อื่นแก้ไขแล้ว โปรดโหลดใหม่", "Dữ liệu đã bị người khác sửa, vui lòng tải lại"),
        [LedgerFreezeConfirm] = ("這個模組已有 {0} 筆記錄，設為分錄後它們會立刻變成不可修改、不可刪除，且設定後不能取消；確認請帶 confirmFreeze",
            "This module already has {0} record(s). Once it becomes a ledger they can no longer be edited or deleted, and this setting cannot be undone. Send confirmFreeze to confirm",
            "โมดูลนี้มีระเบียนอยู่แล้ว {0} รายการ เมื่อตั้งเป็นบัญชีแยกประเภท ระเบียนเหล่านี้จะแก้ไขหรือลบไม่ได้ทันที และยกเลิกการตั้งค่านี้ไม่ได้ ส่ง confirmFreeze เพื่อยืนยัน",
            "Mô-đun này đã có {0} bản ghi. Khi đặt làm sổ cái, chúng sẽ lập tức không thể sửa hoặc xóa, và không thể hủy thiết lập này. Gửi confirmFreeze để xác nhận"),
        [LoginFailed] = ("帳號或密碼錯誤，或帳號暫時無法登入", "Incorrect user ID or password, or the account cannot sign in right now",
            "รหัสผู้ใช้หรือรหัสผ่านไม่ถูกต้อง หรือบัญชีเข้าสู่ระบบไม่ได้ชั่วคราว", "Tài khoản hoặc mật khẩu không đúng, hoặc tài khoản tạm thời không thể đăng nhập"),
        [SignInRequired] = ("請先登入", "Please sign in first", "โปรดเข้าสู่ระบบก่อน", "Vui lòng đăng nhập trước"),
        [PasswordChangeRequired] = ("請先變更密碼", "Please change your password first", "โปรดเปลี่ยนรหัสผ่านก่อน", "Vui lòng đổi mật khẩu trước"),
        [UserNotFound] = ("使用者不存在", "User not found", "ไม่พบผู้ใช้", "Người dùng không tồn tại"),
        [OldPasswordWrong] = ("舊密碼錯誤", "The current password is incorrect", "รหัสผ่านเดิมไม่ถูกต้อง", "Mật khẩu cũ không đúng"),
        [NewPasswordSameAsOld] = ("新密碼不可與舊密碼相同", "The new password must be different from the old one",
            "รหัสผ่านใหม่ต้องไม่ซ้ำกับรหัสผ่านเดิม", "Mật khẩu mới không được trùng với mật khẩu cũ"),
        [OldPasswordRequired] = ("請輸入舊密碼", "Enter your current password", "โปรดป้อนรหัสผ่านเดิม", "Vui lòng nhập mật khẩu cũ"),
        [NewPasswordTooShort] = ("新密碼至少 {0} 碼", "The new password must be at least {0} characters",
            "รหัสผ่านใหม่ต้องมีอย่างน้อย {0} ตัวอักษร", "Mật khẩu mới phải có ít nhất {0} ký tự"),
        [PasswordTooShort] = ("密碼至少 {0} 碼", "The password must be at least {0} characters",
            "รหัสผ่านต้องมีอย่างน้อย {0} ตัวอักษร", "Mật khẩu phải có ít nhất {0} ký tự"),
        [UserIdFormat] = ("帳號只能用英數字、點、底線、減號，長度 2 到 50",
            "User ID may only contain letters, digits, periods, underscores and hyphens, 2 to 50 characters",
            "รหัสผู้ใช้ใช้ได้เฉพาะตัวอักษรภาษาอังกฤษ ตัวเลข จุด ขีดล่าง และขีดกลาง ยาว 2 ถึง 50 ตัวอักษร",
            "Tài khoản chỉ được dùng chữ cái, chữ số, dấu chấm, gạch dưới, gạch ngang, dài 2 đến 50 ký tự"),
        [DisplayNameRequired] = ("請輸入姓名", "Enter a name", "โปรดป้อนชื่อ", "Vui lòng nhập họ tên"),
        [DisplayNameTooLong] = ("姓名最多 {0} 字", "The name can be at most {0} characters", "ชื่อยาวได้ไม่เกิน {0} ตัวอักษร", "Họ tên tối đa {0} ký tự"),
        [MustBeText] = ("必須是文字", "Must be text", "ต้องเป็นข้อความ", "Phải là văn bản"),
        [TooLong] = ("超過 {0} 個字元", "Exceeds {0} characters", "เกิน {0} ตัวอักษร", "Vượt quá {0} ký tự"),
        [MustBeNumber] = ("必須是數字", "Must be a number", "ต้องเป็นตัวเลข", "Phải là số"),
        [ScaleExceeded] = ("小數最多 {0} 位", "At most {0} decimal places", "ทศนิยมได้ไม่เกิน {0} ตำแหน่ง", "Tối đa {0} chữ số thập phân"),
        [DigitsExceeded] = ("有效數字最多 {0} 位", "At most {0} significant digits", "ตัวเลขนัยสำคัญได้ไม่เกิน {0} หลัก", "Tối đa {0} chữ số có nghĩa"),
        [MustBeInteger] = ("必須是整數", "Must be an integer", "ต้องเป็นจำนวนเต็ม", "Phải là số nguyên"),
        [MustBeBoolean] = ("必須是是或否", "Must be yes or no", "ต้องเป็นใช่หรือไม่ใช่", "Phải là có hoặc không"),
        [DateFormat] = ("日期格式必須是 yyyy-MM-dd", "The date format must be yyyy-MM-dd", "รูปแบบวันที่ต้องเป็น yyyy-MM-dd", "Định dạng ngày phải là yyyy-MM-dd"),
        [DateTimeFormat] = ("日期時間格式錯誤", "Invalid date-time format", "รูปแบบวันที่และเวลาไม่ถูกต้อง", "Định dạng ngày giờ không hợp lệ"),
        [TimeFormat] = ("時間格式必須是 HH:mm", "The time format must be HH:mm", "รูปแบบเวลาต้องเป็น HH:mm", "Định dạng giờ phải là HH:mm"),
        [EmailFormat] = ("Email 格式錯誤", "Invalid email format", "รูปแบบอีเมลไม่ถูกต้อง", "Định dạng email không hợp lệ"),
        [PhoneFormat] = ("電話格式錯誤", "Invalid phone number format", "รูปแบบหมายเลขโทรศัพท์ไม่ถูกต้อง", "Định dạng số điện thoại không hợp lệ"),
        [UrlFormat] = ("網址必須以 http:// 或 https:// 開頭", "The URL must start with http:// or https://", "URL ต้องขึ้นต้นด้วย http:// หรือ https://", "URL phải bắt đầu bằng http:// hoặc https://"),
        [OptionInvalid] = ("選項不存在或已停用", "The option does not exist or is disabled", "ไม่มีตัวเลือกนี้หรือถูกปิดใช้งาน", "Tùy chọn không tồn tại hoặc đã bị vô hiệu hóa"),
        [RefNotFound] = ("參照的資料不存在或無權選取", "The referenced record does not exist or you are not allowed to select it",
            "ไม่มีข้อมูลที่อ้างอิงหรือไม่มีสิทธิ์เลือก", "Dữ liệu tham chiếu không tồn tại hoặc bạn không có quyền chọn"),
        [RefStateMismatch] = ("目標記錄的狀態不符，不能選", "The target record's status does not match, so it cannot be selected",
            "สถานะของระเบียนเป้าหมายไม่ตรงกัน จึงเลือกไม่ได้", "Trạng thái của bản ghi đích không phù hợp nên không thể chọn"),
        [AttachmentNotList] = ("附件的值必須是檔案清單", "The attachment value must be a file list", "ค่าของไฟล์แนบต้องเป็นรายการไฟล์", "Giá trị tệp đính kèm phải là danh sách tệp"),
        [AttachmentForged] = ("附件只能透過上傳加入", "Attachments can only be added by uploading", "เพิ่มไฟล์แนบได้โดยการอัปโหลดเท่านั้น", "Chỉ có thể thêm tệp đính kèm bằng cách tải lên"),
        [Required] = ("此欄位必填", "This field is required", "ต้องกรอกช่องนี้", "Trường này là bắt buộc"),
        [RecordReadOnly] = ("此記錄目前不可修改", "This record cannot be edited right now", "ระเบียนนี้แก้ไขไม่ได้ในขณะนี้", "Hiện không thể sửa bản ghi này"),
        [OptionOutOfRange] = ("選項不在可選範圍", "The option is not in the allowed range", "ตัวเลือกไม่อยู่ในช่วงที่เลือกได้", "Tùy chọn không nằm trong phạm vi cho phép"),
        [UnknownDetail] = ("明細模組不存在或未啟用", "The detail module does not exist or is not enabled",
            "ไม่มีโมดูลรายละเอียดหรือยังไม่เปิดใช้งาน", "Mô-đun chi tiết không tồn tại hoặc chưa được kích hoạt"),
        [DetailRowNotFound] = ("明細不存在", "Detail row not found", "ไม่พบรายการรายละเอียด", "Không tìm thấy dòng chi tiết"),
        [TooManyDetailRows] = ("明細最多 200 列", "At most 200 detail rows", "รายละเอียดได้ไม่เกิน 200 แถว", "Tối đa 200 dòng chi tiết"),
        [WorkflowNotInitial] = ("新增時只能使用起始狀態", "Only the initial status can be used when creating", "ตอนสร้างใช้ได้เฉพาะสถานะเริ่มต้น", "Khi tạo mới chỉ được dùng trạng thái ban đầu"),
        [WorkflowNotAllowed] = ("不允許這個狀態轉移", "This status change is not allowed", "ไม่อนุญาตให้เปลี่ยนสถานะนี้", "Không cho phép chuyển trạng thái này"),
        [WorkflowNotPermitted] = ("你沒有權限執行這個狀態轉移", "You do not have permission to make this status change", "คุณไม่มีสิทธิ์เปลี่ยนสถานะนี้", "Bạn không có quyền chuyển trạng thái này"),
        [WorkflowLockedDetails] = ("此狀態的單據不可修改明細", "Details cannot be edited for documents in this status",
            "เอกสารในสถานะนี้แก้ไขรายละเอียดไม่ได้", "Chứng từ ở trạng thái này không thể sửa chi tiết"),
        [WorkflowLockedDelete] = ("此狀態的單據不可刪除", "Records in this status cannot be deleted", "เอกสารในสถานะนี้ลบไม่ได้", "Chứng từ ở trạng thái này không thể xóa"),
        [DeleteReferenced] = ("還有其他資料參照這筆記錄，不能刪除", "Other records still reference this record, so it cannot be deleted",
            "ยังมีข้อมูลอื่นอ้างอิงระเบียนนี้ จึงลบไม่ได้", "Vẫn còn dữ liệu khác tham chiếu bản ghi này nên không thể xóa"),
        [DeleteLockedReferenced] = ("有處於唯讀狀態的單據參照這筆記錄，不能刪除", "A read-only document references this record, so it cannot be deleted",
            "มีเอกสารสถานะอ่านอย่างเดียวอ้างอิงระเบียนนี้ จึงลบไม่ได้", "Có chứng từ ở trạng thái chỉ đọc tham chiếu bản ghi này nên không thể xóa"),
        [DeleteBookedReferenced] = ("有已記帳的單據參照這筆記錄，不能刪除", "A posted document references this record, so it cannot be deleted",
            "มีเอกสารที่ลงบัญชีแล้วอ้างอิงระเบียนนี้ จึงลบไม่ได้", "Có chứng từ đã ghi sổ tham chiếu bản ghi này nên không thể xóa"),
        [DeleteTooDeep] = ("連帶刪除超過 5 層，請先手動刪除下層資料", "Cascading delete goes deeper than 5 levels. Delete the lower-level data manually first",
            "การลบต่อเนื่องเกิน 5 ชั้น โปรดลบข้อมูลชั้นล่างด้วยตนเองก่อน", "Xóa liên đới vượt quá 5 cấp, vui lòng xóa thủ công dữ liệu cấp dưới trước"),
        [ModuleNotAvailable] = ("模組不存在或未啟用", "The module does not exist or is not enabled", "ไม่มีโมดูลหรือยังไม่เปิดใช้งาน", "Mô-đun không tồn tại hoặc chưa được kích hoạt"),
        [RecordNotFound] = ("記錄不存在", "Record not found", "ไม่พบระเบียน", "Không tìm thấy bản ghi"),
        [AppClosedReadOnly] = ("應用程式已關閉，資料唯讀", "The app is closed and its data is read-only",
            "แอปพลิเคชันถูกปิด ข้อมูลเป็นแบบอ่านอย่างเดียว", "Ứng dụng đã đóng, dữ liệu chỉ đọc"),
        [AppClosedNoRewrite] = ("應用程式已關閉，這項變更會改寫既有資料，請先重新開啟應用程式",
            "The app is closed. This change would rewrite existing data. Reopen the app first",
            "แอปพลิเคชันถูกปิด การเปลี่ยนแปลงนี้จะเขียนทับข้อมูลเดิม โปรดเปิดแอปพลิเคชันอีกครั้งก่อน",
            "Ứng dụng đã đóng. Thay đổi này sẽ ghi đè dữ liệu hiện có, vui lòng mở lại ứng dụng trước"),
        [FrozenField] = ("欄位「{0}」屬於已關閉的應用程式「{1}」，不能修改", "The field \"{0}\" belongs to the closed app \"{1}\" and cannot be edited",
            "ฟิลด์ \"{0}\" อยู่ในแอปพลิเคชัน \"{1}\" ที่ปิดแล้ว จึงแก้ไขไม่ได้", "Trường \"{0}\" thuộc ứng dụng đã đóng \"{1}\" nên không thể sửa"),
        [FrozenFieldHidden] = ("這筆資料有無法修改的欄位", "This record has fields that cannot be edited", "ระเบียนนี้มีฟิลด์ที่แก้ไขไม่ได้", "Bản ghi này có trường không thể sửa"),
        [LedgerImmutable] = ("分錄不可修改，請新增沖銷分錄", "Ledger entries cannot be edited. Add a reversing entry instead",
            "แก้ไขรายการบัญชีไม่ได้ โปรดเพิ่มรายการกลับรายการ", "Không thể sửa bút toán, vui lòng thêm bút toán đảo"),
        [LedgerSchemaImmutable] = ("這是分錄模組，這項變更會改寫既有分錄的資料，不允許",
            "This is a ledger module. This change would rewrite existing ledger data and is not allowed",
            "นี่คือโมดูลบัญชีแยกประเภท การเปลี่ยนแปลงนี้จะเขียนทับข้อมูลรายการบัญชีเดิม จึงไม่อนุญาต",
            "Đây là mô-đun sổ cái. Thay đổi này sẽ ghi đè dữ liệu bút toán hiện có nên không được phép"),
        [LedgerBackstop] = ("分錄不可修改，請新增沖銷分錄（模組「{0}」的既有分錄被這次請求改動）",
            "Ledger entries cannot be edited. Add a reversing entry instead (existing entries of module \"{0}\" were changed by this request)",
            "แก้ไขรายการบัญชีไม่ได้ โปรดเพิ่มรายการกลับรายการ (รายการบัญชีเดิมของโมดูล \"{0}\" ถูกคำขอนี้เปลี่ยนแปลง)",
            "Không thể sửa bút toán, vui lòng thêm bút toán đảo (bút toán hiện có của mô-đun \"{0}\" bị yêu cầu này thay đổi)"),
        [BlockNotFound] = ("找不到區塊", "Block not found", "ไม่พบบล็อก", "Không tìm thấy khối"),
        [FieldNotFound] = ("找不到欄位", "Field not found", "ไม่พบฟิลด์", "Không tìm thấy trường"),
        [OptionNotFound] = ("找不到選項", "Option not found", "ไม่พบตัวเลือก", "Không tìm thấy tùy chọn"),
        [EventNotFound] = ("找不到事件", "Event not found", "ไม่พบเหตุการณ์", "Không tìm thấy sự kiện"),
        [CheckNotFound] = ("找不到檢查", "Check not found", "ไม่พบการตรวจสอบ", "Không tìm thấy kiểm tra"),
        [ConversionNotFound] = ("找不到轉換定義", "Conversion definition not found", "ไม่พบนิยามการแปลง", "Không tìm thấy định nghĩa chuyển đổi"),
        [AttachmentFieldNotFound] = ("找不到附件欄位", "Attachment field not found", "ไม่พบฟิลด์ไฟล์แนบ", "Không tìm thấy trường tệp đính kèm"),
        [AttachmentNotFound] = ("找不到附件", "Attachment not found", "ไม่พบไฟล์แนบ", "Không tìm thấy tệp đính kèm"),
    };

    /// <summary>無參數訊息的繁中原文 → 鍵；兩鍵同一句時建表就會丟例外。</summary>
    private static readonly Dictionary<string, string> KeyByZh = Table.Where(kv => !kv.Value.Zh.Contains('{'))
        .ToDictionary(kv => kv.Value.Zh, kv => kv.Key);

    /// <summary>沒有對應語系時退回繁中，與 <see cref="I18nText.FallbackLocale"/> 一致。</summary>
    public static string Get(string? locale, string key, params object[] args)
    {
        var e = Table[key];
        var text = locale switch { "en-US" => e.En, "th" => e.Th, "vi" => e.Vi, _ => e.Zh };
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    /// <summary>字面值是表內某句的繁中原文就換成該語系，否則原樣。只套在 <see cref="ServiceResult"/> 的訊息（<see cref="MessageRef.Resolve"/>），欄位錯誤不用。</summary>
    public static string TranslateLiteral(string? locale, string literal) =>
        KeyByZh.TryGetValue(literal, out var key) ? Get(locale, key) : literal;
}
