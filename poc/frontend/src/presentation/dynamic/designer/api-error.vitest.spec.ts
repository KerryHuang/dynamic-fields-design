import { describe, expect, it } from 'vitest';
import { messagesOutside } from './api-error';

describe('messagesOutside', () => {
  it('returns messages whose key is not an exact rendered key', () => {
    const errors = { f_1: ['a'], f_10: ['b'], '$.values': ['c'] };
    expect(messagesOutside(errors, ['f_1'])).toEqual(['b', 'c']);
    expect(messagesOutside(undefined, ['f_1'])).toEqual([]);
  });
});
