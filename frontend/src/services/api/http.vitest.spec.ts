import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { i18n } from 'src/boot/i18n';
import { recentFailedRequestId } from 'src/services/ops/last-failure';
import { http, setAuthHandlers, toApiError } from './http';

describe('http', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('does not send X-User and normalizes errors', async () => {
    let seen: unknown = 'unset';
    http.defaults.adapter = async (config) => {
      seen = config.headers.get('X-User');
      return Promise.reject({
        config,
        response: { status: 400, data: { traceId: 't', errors: { name: ['bad'] } } },
      });
    };
    await expect(http.get('/x')).rejects.toEqual({ status: 400, errors: { name: ['bad'] } });
    expect(seen).toBeFalsy();
  });

  describe('auth handlers', () => {
    const onUnauthorized = vi.fn();
    const onPasswordChangeRequired = vi.fn();
    const fail = (status: number, data: unknown) => {
      http.defaults.adapter = async (config) => Promise.reject({ config, response: { status, data } });
    };
    beforeEach(() => {
      vi.clearAllMocks();
      setAuthHandlers({ onUnauthorized, onPasswordChangeRequired });
    });

    it('calls onUnauthorized on a 401 from a normal request', async () => {
      fail(401, { message: '請先登入' });
      await expect(http.get('/modules')).rejects.toEqual({ status: 401, message: '請先登入' });
      expect(onUnauthorized).toHaveBeenCalledTimes(1);
    });

    it('does not call onUnauthorized for a 401 from /auth/ endpoints (wrong password)', async () => {
      fail(401, { message: '帳號或密碼錯誤，或帳號暫時無法登入' });
      await expect(http.post('/auth/login', {})).rejects.toMatchObject({ status: 401 });
      expect(onUnauthorized).not.toHaveBeenCalled();
    });

    it('calls onUnauthorized for a 401 from other /auth/ endpoints (logout, change-password, options)', async () => {
      fail(401, { message: '請先登入' });
      for (const url of ['/auth/logout', '/auth/change-password', '/auth/options']) {
        onUnauthorized.mockClear();
        await expect(http.post(url, {})).rejects.toMatchObject({ status: 401 });
        expect(onUnauthorized, url).toHaveBeenCalledTimes(1);
      }
    });

    it('does not call onUnauthorized for a failed demo login either', async () => {
      fail(401, { message: '帳號或密碼錯誤，或帳號暫時無法登入' });
      await expect(http.post('/auth/demo-login', {})).rejects.toMatchObject({ status: 401 });
      expect(onUnauthorized).not.toHaveBeenCalled();
    });

    it('calls onPasswordChangeRequired on 403 passwordChangeRequired and exposes code', async () => {
      fail(403, { message: '請先變更密碼', code: 'passwordChangeRequired' });
      await expect(http.get('/menu')).rejects.toEqual({ status: 403, message: '請先變更密碼', code: 'passwordChangeRequired' });
      expect(onPasswordChangeRequired).toHaveBeenCalledTimes(1);
    });

    it('ignores other 403s', async () => {
      fail(403, { message: '沒有權限' });
      await expect(http.get('/menu')).rejects.toMatchObject({ status: 403 });
      expect(onPasswordChangeRequired).not.toHaveBeenCalled();
      expect(onUnauthorized).not.toHaveBeenCalled();
    });
  });

  it('maps 409/404 message and network errors', () => {
    expect(toApiError({ response: { status: 409, data: { message: 'conflict' } } }))
      .toEqual({ status: 409, message: 'conflict' });
    expect(toApiError({ response: { status: 404, data: {} } })).toEqual({ status: 404, message: undefined });
    expect(toApiError({ code: 'ERR_NETWORK', message: 'Network Error' })).toEqual({ status: 0, code: 'network', message: '網路連線失敗，請檢查網路後再試' });
  });

  it('uses ProblemDetails title as the message for 500 and error-less 400', () => {
    expect(toApiError({ response: { status: 500, data: { title: 'An error occurred', status: 500 } } }))
      .toEqual({ status: 500, message: 'An error occurred' });
    expect(toApiError({ response: { status: 400, data: { title: 'Bad body' } } }))
      .toEqual({ status: 400, errors: undefined, message: 'Bad body' });
  });
});

describe('X-Locale 標頭', () => {
  afterEach(() => { i18n.global.locale.value = 'zh-TW'; });

  it('每個請求都帶目前介面語系（登入前也一樣）', async () => {
    const seen: unknown[] = [];
    http.defaults.adapter = async (config) => {
      seen.push(config.headers.get('X-Locale'));
      return { config, data: {}, status: 200, statusText: 'OK', headers: {} };
    };
    await http.post('/auth/login', {});
    i18n.global.locale.value = 'th';
    await http.get('/modules');
    expect(seen).toEqual(['zh-TW', 'th']);
  });
});

describe('toApiError code 與 params', () => {
  it('帶出 409 的 code 與 params', () => {
    expect(toApiError({ response: { status: 409, data: { message: 'm', code: 'ledgerFreezeConfirm', params: { count: 3 } } } }))
      .toEqual({ status: 409, message: 'm', code: 'ledgerFreezeConfirm', params: { count: 3 } });
  });
});

describe('toApiError 網路失敗', () => {
  afterEach(() => { i18n.global.locale.value = 'zh-TW'; });

  it('status 0 不顯示 axios 英文訊息，改用語系檔', () => {
    const e = { code: 'ERR_NETWORK', message: 'Network Error' };
    expect(toApiError(e)).toEqual({ status: 0, code: 'network', message: '網路連線失敗，請檢查網路後再試' });
    i18n.global.locale.value = 'en-US';
    expect(toApiError(e)).toEqual({ status: 0, code: 'network', message: 'Network error. Check your connection and try again.' });
  });

  it('逾時也算網路失敗', () => {
    for (const code of ['ECONNABORTED', 'ETIMEDOUT'])
      expect(toApiError({ code, message: 'timeout of 1ms exceeded' })).toMatchObject({ status: 0, code: 'network' });
  });

  it('瀏覽器自己中止的請求（換頁、卸載，ECONNABORTED 加 "Request aborted"）不顯示網路錯誤', () => {
    expect(toApiError({ code: 'ECONNABORTED', message: 'Request aborted' })).toEqual({ status: 0, message: 'Request aborted' });
  });

  it('取消請求與非 axios 例外不顯示網路錯誤，保留原訊息', () => {
    expect(toApiError({ code: 'ERR_CANCELED', message: 'canceled' })).toEqual({ status: 0, message: 'canceled' });
    expect(toApiError(new TypeError('x is not a function'))).toEqual({ status: 0, message: 'x is not a function' });
  });
});

describe('錯誤代碼（request id 前 8 碼）', () => {
  const RID = '0123456789abcdef0123456789abcdef';
  afterEach(() => { i18n.global.locale.value = 'zh-TW'; });

  it('toApiError_500_appendsErrorCodeWithFirst8Chars', () => {
    const e = toApiError({ response: { status: 500, headers: { 'x-request-id': RID }, data: { message: '系統發生錯誤，請稍後再試' } } });
    expect(e).toEqual({ status: 500, message: '系統發生錯誤，請稍後再試（錯誤代碼：01234567）', requestId: RID });
  });

  it('toApiError_503_appendsErrorCode（標頭用 AxiosHeaders 的 get，四語）', () => {
    i18n.global.locale.value = 'en-US';
    const headers = { get: (n: string) => (n === 'x-request-id' ? RID : undefined) };
    expect(toApiError({ response: { status: 503, headers, data: { message: 'Busy' } } }).message).toBe('Busy (Error code: 01234567)');
  });

  it('沒有標頭時取主體的 traceId', () => {
    expect(toApiError({ response: { status: 500, data: { message: 'm', traceId: RID } } })).toMatchObject({ message: 'm（錯誤代碼：01234567）', requestId: RID });
  });

  it('toApiError_409_noErrorCode（4xx 不加，只帶 requestId）', () => {
    expect(toApiError({ response: { status: 409, headers: { 'x-request-id': RID }, data: { message: 'conflict' } } }))
      .toEqual({ status: 409, message: 'conflict', requestId: RID });
  });

  it('toApiError_network_noErrorCode（沒有回應就沒有代碼）', () => {
    expect(toApiError({ code: 'ERR_NETWORK', message: 'Network Error' }).message).toBe('網路連線失敗，請檢查網路後再試');
  });

  it('toApiError_502WithoutMessage_fallsBackToServerError', () => {
    const e = toApiError({ response: { status: 502, headers: { 'x-request-id': RID }, data: '<html>Bad Gateway</html>' } });
    expect(e.message).toBe('系統發生錯誤，請稍後再試（錯誤代碼：01234567）');
  });

  it('interceptor_recordsFailedRequestId', async () => {
    http.defaults.adapter = async (config) => Promise.reject({ config, response: { status: 404, headers: { 'x-request-id': RID }, data: {} } });
    await expect(http.get('/x')).rejects.toMatchObject({ status: 404 });
    const now = Date.now();
    expect(recentFailedRequestId(now)).toBe(RID);
    expect(recentFailedRequestId(now + 30_001)).toBeUndefined();
  });
});
