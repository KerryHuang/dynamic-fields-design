import axios, { AxiosError } from 'axios';
import { currentLocale, tFor } from 'src/i18n/translate';
import type { BlockingOption, BlockingRef, DeleteCheck } from 'src/domain/dynamic/types';
import { recordFailedRequest } from 'src/services/ops/last-failure';

export interface ApiError {
  status: number;
  errors?: Record<string, string[]>;
  message?: string;
  /** Machine-readable reason: passwordChangeRequired on 403, stale / ledgerFreezeConfirm on 409, network when the request never got a response. */
  code?: string;
  /** Values that go with code, e.g. { count } for ledgerFreezeConfirm. */
  params?: Record<string, unknown>;
  /** 409 on delete: records that still reference the target. */
  references?: BlockingRef[];
  total?: number;
  /** 409 on group delete: field option scopes that still name the group. */
  blockingOptions?: BlockingOption[];
  /** 409 on module/field/block delete: why it is blocked, or the fresh check when the confirmed purge count is stale. */
  deleteCheck?: DeleteCheck;
  /** 後端的 X-Request-Id（沒有標頭時取主體的 traceId）；5xx 的 message 尾端附前 8 碼當錯誤代碼。 */
  requestId?: string;
}

export const isForbidden = (e: unknown): boolean => (e as ApiError | null)?.status === 403;

export const http = axios.create({ baseURL: '/api/v3/dynamic' });

// 後端訊息依 X-Locale 回傳；目前介面語系在啟動時已從 formwright.locale 讀入，登入前的請求也帶
http.interceptors.request.use((config) => {
  config.headers.set('X-Locale', currentLocale());
  return config;
});

/** axios 沒拿到回應的原因裡，真正屬於連線問題的（斷線、逾時）；取消請求與其他例外不算。 */
const NETWORK_CODES = ['ERR_NETWORK', 'ECONNABORTED', 'ETIMEDOUT'];

/** 瀏覽器自己中止請求（換頁、卸載）時 XHR adapter 也用 ECONNABORTED，訊息固定是這句；不是連線問題。 */
const isBrowserAbort = (err: { code?: string; message?: string } | undefined) => err?.code === 'ECONNABORTED' && err.message === 'Request aborted';

export interface AuthHandlers {
  onUnauthorized(): void;
  onPasswordChangeRequired(): void;
}

let authHandlers: AuthHandlers | undefined;
export const setAuthHandlers = (h: AuthHandlers) => { authHandlers = h; };

/** 後端 request id 是 W3C trace id（32 個小寫十六進位字元）；其他值不當錯誤代碼，也不送進錯誤回報。 */
const REQUEST_ID = /^[0-9a-f]{32}$/;

function readRequestId(headers: unknown, data: { traceId?: unknown } | undefined): string | undefined {
  const h = headers as { get?: (name: string) => unknown; [k: string]: unknown } | undefined;
  const raw = typeof h?.get === 'function' ? h.get('x-request-id') : h?.['x-request-id'];
  const id = raw ?? data?.traceId;
  return typeof id === 'string' && REQUEST_ID.test(id) ? id : undefined;
}

/** 5xx：沒有訊息時退回四語「系統發生錯誤」；有 request id 就在同一行尾端接錯誤代碼（使用者裁決 B2，O1 計畫判斷 J23）。 */
function serverErrorMessage(message: string | undefined, requestId: string | undefined): string {
  const t = tFor(currentLocale());
  const text = message || t('common.serverError');
  return requestId ? text + t('common.errorCode', { code: requestId.slice(0, 8) }) : text;
}

export function toApiError(e: unknown): ApiError {
  const err = e as AxiosError<{ traceId?: string; errors?: Record<string, string[]>; message?: string; code?: string; params?: Record<string, unknown>; title?: string; references?: BlockingRef[]; total?: number; blockingOptions?: BlockingOption[] } & Partial<DeleteCheck>>;
  if (!err?.response) {
    return NETWORK_CODES.includes(err?.code ?? '') && !isBrowserAbort(err)
      ? { status: 0, code: 'network', message: tFor(currentLocale())('common.networkError') }
      : { status: 0, message: err?.message };
  }
  const { status, headers } = err.response;
  // 代理回的 502 之類可能是 HTML 字串，不是物件
  const data = typeof err.response.data === 'object' ? err.response.data : undefined;
  const requestId = readRequestId(headers, data);
  const rid = requestId ? { requestId } : {};
  if (status === 400) return { status, errors: data?.errors, message: data?.title, ...rid };
  const base = { status, message: status >= 500 ? serverErrorMessage(data?.message ?? data?.title, requestId) : data?.message ?? data?.title, ...(data?.code ? { code: data.code } : {}), ...(data?.params ? { params: data.params } : {}), ...rid };
  if (status === 409 && data?.blockers && data.purge)
    return { ...base, deleteCheck: { canDelete: !!data.canDelete, blockers: data.blockers, purge: data.purge } };
  if (status === 409 && data?.blockingOptions) return { ...base, blockingOptions: data.blockingOptions };
  return status === 409 && data?.references ? { ...base, references: data.references, total: data.total } : base;
}

// Only a failed sign-in attempt is a plain 401; elsewhere under /auth/ (logout, change-password, options) a 401 means the session expired.
const SIGN_IN_URLS = ['/auth/login', '/auth/demo-login'];

/** Session-level reactions to a failed response. Requests that bypass the interceptor (validateStatus) call this themselves. */
export function handleAuthFailure(err: ApiError, url?: string) {
  if (err.status === 401 && !SIGN_IN_URLS.includes(url ?? '')) authHandlers?.onUnauthorized();
  else if (err.status === 403 && err.code === 'passwordChangeRequired') authHandlers?.onPasswordChangeRequired();
}

http.interceptors.response.use((r) => r, (e) => {
  const err = toApiError(e);
  if (err.requestId) recordFailedRequest(err.requestId);
  handleAuthFailure(err, (e as AxiosError)?.config?.url);
  return Promise.reject(err);
});
