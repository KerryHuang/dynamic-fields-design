using DynamicModules.Application.Common;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetUiMessages;

/// <summary>
/// 一個語系補齊後的介面文字（<c>GET i18n/ui/{code}</c>；語系包設計 §8.2）。登入與否都可呼叫：匿名只取得到啟用語系，
/// 已登入可取已安裝但未啟用的語系（系統管理員試看用；後端本來就把未啟用的 X-Locale 當成預設語系，取到文字不會改變資料語言）。
/// </summary>
public sealed record GetUiMessagesRequest(string Code, bool SignedIn) : IRequest<ServiceResult>;

/// <summary>成功時的結果：WebAPI 依 <c>Hash</c> 與請求的 <c>v</c>、<c>Enabled</c> 設快取標頭（i4b 計畫判斷 8）。</summary>
public sealed record UiMessagesResult(string Hash, byte[] Json, bool Enabled);
