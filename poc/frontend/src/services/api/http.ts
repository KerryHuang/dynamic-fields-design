import axios, { AxiosError } from 'axios';
import type { BlockingOption, BlockingRef } from 'src/domain/dynamic/types';

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
  const err = e as AxiosError<{ errors?: Record<string, string[]>; message?: string; code?: string; title?: string; references?: BlockingRef[]; total?: number; blockingOptions?: BlockingOption[] }>;
  if (!err?.response) return { status: 0, message: err?.message };
  const { status, data } = err.response;
  if (status === 400) return { status, errors: data?.errors, message: data?.title };
  const base = { status, message: data?.message ?? data?.title, ...(data?.code ? { code: data.code } : {}) };
  if (status === 409 && data?.blockingOptions) return { ...base, blockingOptions: data.blockingOptions };
  return status === 409 && data?.references ? { ...base, references: data.references, total: data.total } : base;
}

http.interceptors.response.use((r) => r, (e) => {
  const err = toApiError(e);
  // A 401 from /auth/* is a failed login, not an expired session.
  if (err.status === 401 && !(e as AxiosError)?.config?.url?.startsWith('/auth/')) authHandlers?.onUnauthorized();
  else if (err.status === 403 && err.code === 'passwordChangeRequired') authHandlers?.onPasswordChangeRequired();
  return Promise.reject(err);
});
