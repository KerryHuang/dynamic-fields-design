import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it } from 'vitest';
import { useIdentityStore } from './use-identity-store';

describe('identity store', () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
  });

  it('starts logged out', () => {
    const s = useIdentityStore();
    expect(s.userId).toBe('');
    expect(s.users).toEqual([]);
    expect(s.mustChangePassword).toBe(false);
  });

  it('setSession / setUsers / clear', () => {
    const s = useIdentityStore();
    s.setSession('sales01', true);
    s.setUsers([{ userId: 'sales01', displayName: 'S' }]);
    expect(s.userId).toBe('sales01');
    expect(s.mustChangePassword).toBe(true);
    expect(s.users).toHaveLength(1);
    s.clear();
    expect(s.userId).toBe('');
    expect(s.mustChangePassword).toBe(false);
    expect(s.users).toEqual([]);
  });

  it('does not persist the session in localStorage', () => {
    useIdentityStore().setSession('sales01');
    expect(localStorage.length).toBe(0);
  });
});
