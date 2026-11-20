import { setBusinessOffset } from 'src/domain/dynamic/business-time';
import { applyLocalePolicies } from 'src/i18n/locale-settings';
import { dynamicApi } from 'src/services/api/dynamic-api';

// 登入後才有的全系統設定（營運時區、語系三政策）。登出與 session 過期時只清「已載入」，不清已設定的偏移：
// 登出瞬間仍在畫面上的元件不會因此丟錯，下次登入後的第一次導覽會重新載入。

let loaded = false;

export function sessionSettingsLoaded(): boolean {
  return loaded;
}

export function resetSessionSettings(): void {
  loaded = false;
}

export async function loadSessionSettings(): Promise<void> {
  const eff = await dynamicApi.getEffectivePolicies(null);
  setBusinessOffset(String(eff.values['locale.timeZone']));
  applyLocalePolicies(eff.values, eff.locales);
  loaded = true;
}
