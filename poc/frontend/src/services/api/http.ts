import axios, { AxiosError } from 'axios';
import type { BlockingRef } from 'src/domain/dynamic/types';
import { useIdentityStore } from 'src/stores/use-identity-store';

export interface ApiError {
  status: number;
  errors?: Record<string, string[]>;
  message?: string;
  /** 409 on delete: records that still reference the target. */
  references?: BlockingRef[];
  total?: number;
}

export const isForbidden = (e: unknown): boolean => (e as ApiError | null)?.status === 403;

export const http = axios.create({ baseURL: '/api/v3/dynamic' });

http.interceptors.request.use((config) => {
  config.headers.set('X-User', useIdentityStore().userId);
  return config;
});

export function toApiError(e: unknown): ApiError {
  const err = e as AxiosError<{ errors?: Record<string, string[]>; message?: string; title?: string; references?: BlockingRef[]; total?: number }>;
  if (!err?.response) return { status: 0, message: err?.message };
  const { status, data } = err.response;
  if (status === 400) return { status, errors: data?.errors, message: data?.title };
  const base = { status, message: data?.message ?? data?.title };
  return status === 409 && data?.references ? { ...base, references: data.references, total: data.total } : base;
}

http.interceptors.response.use((r) => r, (e) => Promise.reject(toApiError(e)));
