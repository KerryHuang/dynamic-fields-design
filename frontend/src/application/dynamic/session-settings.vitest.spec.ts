import { afterEach, describe, expect, it, vi } from 'vitest';
import { businessOffset, setBusinessOffset } from 'src/domain/dynamic/business-time';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { loadSessionSettings, resetSessionSettings, sessionSettingsLoaded } from './session-settings';

afterEach(() => {
  vi.restoreAllMocks();
  resetSessionSettings();
  setBusinessOffset('+08:00');
});

describe('session-settings', () => {
  it('載入營運時區', async () => {
    vi.spyOn(dynamicApi, 'getEffectivePolicies').mockResolvedValue({ locales: [], values: { 'locale.timeZone': '+07:00' } });
    await loadSessionSettings();
    expect(businessOffset()).toBe('+07:00');
    expect(sessionSettingsLoaded()).toBe(true);
    resetSessionSettings();
    expect(sessionSettingsLoaded()).toBe(false);
    expect(businessOffset()).toBe('+07:00');
  });

  it('API 失敗時丟錯、仍未載入', async () => {
    vi.spyOn(dynamicApi, 'getEffectivePolicies').mockRejectedValue(new Error('down'));
    await expect(loadSessionSettings()).rejects.toThrow('down');
    expect(sessionSettingsLoaded()).toBe(false);
  });
});
