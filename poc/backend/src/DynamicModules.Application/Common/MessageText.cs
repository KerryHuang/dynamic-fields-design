using System.Globalization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 後端回給使用者的訊息（四語系，形式同 ReportText：鍵、四語、string.Format 參數）。繁中必須與搬進來之前的字面值逐字相同。
/// 先收共用訊息與登入、Session；其餘訊息仍是呼叫端的中文字面值，之後分段搬。泰文與越南文待母語者校對。
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
        UserIdFormat = nameof(UserIdFormat), DisplayNameRequired = nameof(DisplayNameRequired), DisplayNameTooLong = nameof(DisplayNameTooLong);

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
            "This module already has {0} records. Once it becomes a ledger they can no longer be edited or deleted, and this setting cannot be undone. Send confirmFreeze to confirm",
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

    /// <summary>字面值是表內某句的繁中原文就換成該語系，否則原樣。</summary>
    public static string TranslateLiteral(string? locale, string literal) =>
        KeyByZh.TryGetValue(literal, out var key) ? Get(locale, key) : literal;
}
