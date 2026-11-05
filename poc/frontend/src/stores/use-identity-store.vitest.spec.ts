import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useIdentityStore } from './use-identity-store';

describe('identity store', () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
  });
  afterEach(() => vi.unstubAllGlobals());

  it('defaults to admin', () => {
    expect(useIdentityStore().userId).toBe('admin');
  });

  it('identity store falls back to the first API user when the stored id is unknown', () => {
    localStorage.setItem('dynamic.userId', 'ghost');
    const list = [{ userId: 'floor01', displayName: 'F' }, { userId: 'sales01', displayName: 'S' }];
    const s = useIdentityStore();
    expect(s.syncUsers(list)).toBe(true);
    expect(s.userId).toBe('floor01');
    expect(localStorage.getItem('dynamic.userId')).toBe('floor01');
    expect(s.syncUsers(list)).toBe(false);
    expect(s.userId).toBe('floor01');
    s.setUser('sales01');
    expect(s.syncUsers(list)).toBe(false);
    expect(s.userId).toBe('sales01');
  });

  it('persists userId and survives storage errors', () => {
    useIdentityStore().setUser('sales01');
    expect(localStorage.getItem('dynamic.userId')).toBe('sales01');
    setActivePinia(createPinia());
    expect(useIdentityStore().userId).toBe('sales01');

    const boom = {
      getItem: () => { throw new Error('denied'); },
      setItem: () => { throw new Error('denied'); },
    };
    vi.stubGlobal('localStorage', boom);
    setActivePinia(createPinia());
    const s = useIdentityStore();
    expect(s.userId).toBe('admin');
    expect(() => s.setUser('floor01')).not.toThrow();
    expect(s.userId).toBe('floor01');
  });
});
