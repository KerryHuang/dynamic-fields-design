import { afterEach, describe, expect, it, vi } from 'vitest';
import { businessToday, setBusinessOffset, toBusinessWallClock, withBusinessOffset } from './business-time';

afterEach(() => setBusinessOffset('+08:00'));

describe('business-time', () => {
  it('未設定時丟錯', async () => {
    vi.resetModules();
    const fresh = await import('./business-time');
    expect(fresh.isBusinessOffsetLoaded()).toBe(false);
    expect(() => fresh.businessOffset()).toThrow('business time zone not loaded');
    expect(() => fresh.businessToday()).toThrow('business time zone not loaded');
  });

  it('拒絕不合格式的偏移', () => {
    expect(() => setBusinessOffset('+8:00')).toThrow();
    expect(() => setBusinessOffset('Asia/Taipei')).toThrow();
  });

  it('換成營運時區的牆上時間', () => {
    setBusinessOffset('+07:00');
    expect(toBusinessWallClock('2026-11-11T10:00:00+08:00')).toBe('2026-11-11T09:00:00');
    expect(toBusinessWallClock('2026-11-11T02:00:00Z')).toBe('2026-11-11T09:00:00');
    expect(toBusinessWallClock('2026-11-11T09:00')).toBe('2026-11-11T09:00:00');
    expect(toBusinessWallClock('nope')).toBeNull();
    expect(withBusinessOffset('2026-11-11T09:00')).toBe('2026-11-11T09:00:00+07:00');
  });

  it('營運時區的今天', () => {
    const now = new Date('2026-11-10T17:30:00Z');
    setBusinessOffset('+07:00');
    expect(businessToday(now)).toBe('2026-11-11');
    setBusinessOffset('+05:30');
    expect(businessToday(now)).toBe('2026-11-10');
    setBusinessOffset('-12:00');
    expect(businessToday(now)).toBe('2026-11-10');
  });
});
