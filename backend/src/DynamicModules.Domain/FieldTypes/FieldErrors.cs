namespace DynamicModules.Domain.FieldTypes;

/// <summary>
/// 欄位值驗證錯誤的訊息鍵（<see cref="FieldValueResult.Error"/>）。文字在 Application 的 MessageText，依請求語系解析；
/// Domain 只帶鍵與參數。每個鍵都必須在 MessageText 有四語系文字（MessageTextTests 會檢查）。
/// </summary>
public static class FieldErrors
{
    public const string
        MustBeText = nameof(MustBeText), TooLong = nameof(TooLong), MustBeNumber = nameof(MustBeNumber),
        ScaleExceeded = nameof(ScaleExceeded), DigitsExceeded = nameof(DigitsExceeded), MustBeInteger = nameof(MustBeInteger),
        MustBeBoolean = nameof(MustBeBoolean), DateFormat = nameof(DateFormat), DateTimeFormat = nameof(DateTimeFormat),
        TimeFormat = nameof(TimeFormat), EmailFormat = nameof(EmailFormat), PhoneFormat = nameof(PhoneFormat), UrlFormat = nameof(UrlFormat),
        OptionInvalid = nameof(OptionInvalid), RefNotFound = nameof(RefNotFound), RefStateMismatch = nameof(RefStateMismatch),
        UserNotFound = nameof(UserNotFound), AttachmentNotList = nameof(AttachmentNotList), AttachmentForged = nameof(AttachmentForged);
}
