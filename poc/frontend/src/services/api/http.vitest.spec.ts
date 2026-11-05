import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
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
    expect(toApiError({ message: 'Network Error' })).toEqual({ status: 0, message: 'Network Error' });
  });

  it('uses ProblemDetails title as the message for 500 and error-less 400', () => {
    expect(toApiError({ response: { status: 500, data: { title: 'An error occurred', status: 500 } } }))
      .toEqual({ status: 500, message: 'An error occurred' });
    expect(toApiError({ response: { status: 400, data: { title: 'Bad body' } } }))
      .toEqual({ status: 400, errors: undefined, message: 'Bad body' });
  });
});
