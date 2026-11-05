import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it } from 'vitest';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { http, toApiError } from './http';

describe('http', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('adds X-User and normalizes errors', async () => {
    useIdentityStore().setUser('sales02');
    let seen = '';
    http.defaults.adapter = async (config) => {
      seen = config.headers.get('X-User') as string;
      return Promise.reject({
        config,
        response: { status: 400, data: { traceId: 't', errors: { name: ['bad'] } } },
      });
    };
    await expect(http.get('/x')).rejects.toEqual({ status: 400, errors: { name: ['bad'] } });
    expect(seen).toBe('sales02');
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
