import axios, { AxiosError } from 'axios';
import type { BlockingOption, BlockingRef, DeleteCheck } from 'src/domain/dynamic/types';

export interface ApiError {
  status: number;
  errors?: Record<string, string[]>;
  message?: string;
  /** Machine-readable reason, e.g. passwordChangeRequired on 403. */
  code?: string;
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

export interface AuthHandlers {
  onUnauthorized(): void;
  onPasswordChangeRequired(): void;
}

let authHandlers: AuthHandlers | undefined;
export const setAuthHandlers = (h: AuthHandlers) => { authHandlers = h; };

export function toApiError(e: unknown): ApiError {
  const err = e as AxiosError<{ errors?: Record<string, string[]>; message?: string; code?: string; title?: string; references?: BlockingRef[]; total?: number; blockingOptions?: BlockingOption[] } & Partial<DeleteCheck>>;
  if (!err?.response) return { status: 0, message: err?.message };
  const { status, data } = err.response;
  if (status === 400) return { status, errors: data?.errors, message: data?.title };
  const base = { status, message: data?.message ?? data?.title, ...(data?.code ? { code: data.code } : {}) };
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
