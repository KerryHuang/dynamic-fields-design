import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { i18n } from 'src/boot/i18n';
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
    expect(toApiError({ message: 'Network Error' })).toEqual({ status: 0, message: '網路連線失敗，請檢查網路後再試' });
  });

  it('uses ProblemDetails title as the message for 500 and error-less 400', () => {
    expect(toApiError({ response: { status: 500, data: { title: 'An error occurred', status: 500 } } }))
      .toEqual({ status: 500, message: 'An error occurred' });
    expect(toApiError({ response: { status: 400, data: { title: 'Bad body' } } }))
      .toEqual({ status: 400, errors: undefined, message: 'Bad body' });
  });
});

describe('toApiError 網路失敗', () => {
  afterEach(() => { i18n.global.locale.value = 'zh-TW'; });

  it('status 0 不顯示 axios 英文訊息，改用語系檔', () => {
    const e = { message: 'Network Error' };
    expect(toApiError(e)).toEqual({ status: 0, message: '網路連線失敗，請檢查網路後再試' });
    i18n.global.locale.value = 'en-US';
    expect(toApiError(e)).toEqual({ status: 0, message: 'Network error. Check your connection and try again.' });
  });
});
