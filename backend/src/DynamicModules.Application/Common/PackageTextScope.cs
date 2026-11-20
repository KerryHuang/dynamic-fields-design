using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Common;

/// <summary>
/// 開啟應用程式套件或樣板期間，必填的多語文字照套件規則（必須有繁中），不跟政策 locale.requiredText（政策設計 p3 計畫判斷 12）：
/// 套件經一般的建立請求（建模組、加欄位、建事件…）寫入，<see cref="RequiredTextGuard"/> 在這個範圍內只要求繁中。
/// 旗標存在 AsyncLocal，只有程式碼呼叫 <see cref="Begin"/> 才生效，不會流到其他請求（同 <c>ServerUploadScope</c>）。
/// </summary>
public static class PackageTextScope
{
    private static readonly AsyncLocal<bool> Flag = new();

    public static bool Active => Flag.Value;

    /// <summary>套件規則的必填語系。</summary>
    public static IReadOnlyList<string> RequiredLocales { get; } = [I18nText.FallbackLocale];

    /// <summary>進入範圍；Dispose 還原成進入前的值（巢狀時還原成外層）。</summary>
    public static IDisposable Begin()
    {
        var previous = Flag.Value;
        Flag.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Flag.Value = previous;
        }
    }
}
