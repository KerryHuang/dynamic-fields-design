import { afterEach, describe, expect, it, vi } from 'vitest';
import { readRecent, recordRecent } from './recent-modules';

afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });

describe('recent modules', () => {
  it('records most recent first, de-duplicates and keeps 10', () => {
    for (const k of ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'k']) recordRecent('u1', k);
    expect(readRecent('u1')).toEqual(['k', 'l', 'j', 'i', 'h', 'g', 'f', 'e', 'd', 'c']);
  });
  it('is separated per user and ignores an empty user', () => {
    recordRecent('u1', 'a');
    recordRecent('', 'x');
    expect(readRecent('u2')).toEqual([]);
    expect(readRecent('')).toEqual([]);
  });
  it('treats corrupt data as empty', () => {
    localStorage.setItem('formwright:recent-modules:u1', '{"not":"array"}');
    expect(readRecent('u1')).toEqual([]);
    localStorage.setItem('formwright:recent-modules:u1', '[1,"a"]');
    expect(readRecent('u1')).toEqual(['a']);
  });
  it('does not throw when storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(() => recordRecent('u1', 'a')).not.toThrow();
    expect(readRecent('u1')).toEqual([]);
  });
});
