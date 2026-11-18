import { describe, expect, it } from 'vitest';
import { RECENT_MS, recentFailedRequestId, recordFailedRequest } from './last-failure';

describe('最近失敗的 request id', () => {
  it('30 秒內才帶，之後就不帶', () => {
    recordFailedRequest('a'.repeat(32), 1_000);
    expect(recentFailedRequestId(1_000 + RECENT_MS)).toBe('a'.repeat(32));
    expect(recentFailedRequestId(1_000 + RECENT_MS + 1)).toBeUndefined();
  });

  it('新的失敗蓋過舊的', () => {
    recordFailedRequest('a'.repeat(32), 1_000);
    recordFailedRequest('b'.repeat(32), 2_000);
    expect(recentFailedRequestId(2_000)).toBe('b'.repeat(32));
  });
});
