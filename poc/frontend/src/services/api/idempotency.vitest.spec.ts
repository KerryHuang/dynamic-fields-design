import { describe, expect, it } from 'vitest';
import { newIdempotencyKey } from './idempotency';

describe('newIdempotencyKey', () => {
  it('fits the server format: 8 to 64 characters of letters, digits, underscore and hyphen', () => {
    const key = newIdempotencyKey();
    expect(key).toMatch(/^[A-Za-z0-9_-]{8,64}$/);
  });

  it('is different every time', () => {
    expect(newIdempotencyKey()).not.toBe(newIdempotencyKey());
  });
});
