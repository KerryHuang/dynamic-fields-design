import axios, { AxiosError } from 'axios';
import { currentLocale, tFor } from 'src/i18n/translate';
import type { BlockingOption, BlockingRef, DeleteCheck } from 'src/domain/dynamic/types';

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

export interface AuthHandlers {
  onUnauthorized(): void;
  onPasswordChangeRequired(): void;
}

let authHandlers: AuthHandlers | undefined;
export const setAuthHandlers = (h: AuthHandlers) => { authHandlers = h; };

export function toApiError(e: unknown): ApiError {
  const err = e as AxiosError<{ errors?: Record<string, string[]>; message?: string; code?: string; params?: Record<string, unknown>; title?: string; references?: BlockingRef[]; total?: number; blockingOptions?: BlockingOption[] } & Partial<DeleteCheck>>;
  if (!err?.response) {
    return NETWORK_CODES.includes(err?.code ?? '')
      ? { status: 0, code: 'network', message: tFor(currentLocale())('common.networkError') }
      : { status: 0, message: err?.message };
  }
  const { status, data } = err.response;
  if (status === 400) return { status, errors: data?.errors, message: data?.title };
  const base = { status, message: data?.message ?? data?.title, ...(data?.code ? { code: data.code } : {}), ...(data?.params ? { params: data.params } : {}) };
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
  handleAuthFailure(err, (e as AxiosError)?.config?.url);
  return Promise.reject(err);
});
