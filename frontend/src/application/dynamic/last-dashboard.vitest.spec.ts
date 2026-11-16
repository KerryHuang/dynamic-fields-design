import { afterEach, describe, expect, it, vi } from 'vitest';
import { pickDashboard, readLastDashboard, writeLastDashboard } from './last-dashboard';

afterEach(() => { localStorage.clear(); vi.restoreAllMocks(); });

describe('last-dashboard', () => {
  it('is stored per user', () => {
    writeLastDashboard('a', 3);
    writeLastDashboard('b', 5);
    expect(readLastDashboard('a')).toBe(3);
    expect(readLastDashboard('b')).toBe(5);
    expect(readLastDashboard('c')).toBeNull();
  });

  it('ignores broken content and a missing user', () => {
    localStorage.setItem('dashboard.last.a', '{oops');
    expect(readLastDashboard('a')).toBeNull();
    localStorage.setItem('dashboard.last.a', '"3"');
    expect(readLastDashboard('a')).toBeNull();
    localStorage.setItem('dashboard.last.a', '-1');
    expect(readLastDashboard('a')).toBeNull();
    expect(readLastDashboard('')).toBeNull();
  });

  it('never throws when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(readLastDashboard('a')).toBeNull();
    expect(() => writeLastDashboard('a', 1)).not.toThrow();
  });

  it('pickDashboard prefers the last one if still listed, else the first, else null', () => {
    expect(pickDashboard([1, 2, 3], 2)).toBe(2);
    expect(pickDashboard([1, 2, 3], 9)).toBe(1);
    expect(pickDashboard([1, 2, 3], null)).toBe(1);
    expect(pickDashboard([], 2)).toBeNull();
  });
});
