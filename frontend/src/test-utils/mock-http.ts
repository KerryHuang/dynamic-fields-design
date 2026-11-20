import type { InternalAxiosRequestConfig } from 'axios';
import { http } from 'src/services/api/http';

export interface Call { method: string; url: string; params?: Record<string, unknown>; data?: unknown }

/** Return one from a handler to make the call fail the way axios does (the interceptor turns it into an ApiError). */
export class HttpFail {
  constructor(public status: number, public data?: unknown) {}
}

/** Return one from a handler to resolve (not reject) with this exact status, body and headers, e.g. for calls that set validateStatus. */
export class HttpRaw {
  constructor(public status: number, public data?: unknown, public headers: Record<string, string> = {}) {}
}

/** Routes the shared axios instance to `handler`; whatever it returns (or a promise of it) is the response body. */
export function mockHttp(handler: (c: Call, config: InternalAxiosRequestConfig) => unknown): Call[] {
  const calls: Call[] = [];
  http.defaults.adapter = async (config) => {
    const call: Call = { method: String(config.method), url: String(config.url), params: config.params, data: config.data };
    calls.push(call);
    const reply = await handler(call, config);
    if (reply instanceof HttpFail) return Promise.reject({ config, response: { status: reply.status, data: reply.data, headers: {}, config } });
    if (reply instanceof HttpRaw) return { data: reply.data, status: reply.status, statusText: '', headers: reply.headers, config };
    return { data: reply, status: 200, statusText: 'OK', headers: {}, config };
  };
  return calls;
}

/** JSON body of a captured call. */
export const bodyOf = (c: Call | undefined): unknown => (typeof c?.data === 'string' ? JSON.parse(c.data) : c?.data);
