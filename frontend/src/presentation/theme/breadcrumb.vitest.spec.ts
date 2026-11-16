import { describe, expect, it } from 'vitest';
import zhTW from 'src/i18n/zh-TW.json';
import { breadcrumbFor } from './breadcrumb';

const MENU = [
  { key: 'orders', name: { 'zh-TW': '訂單', 'en-US': 'Orders' }, section: { 'zh-TW': '業務', 'en-US': 'Sales operations' } },
  { key: 'plain', name: { 'zh-TW': '維修單' } },
];

/** 以 zh-TW 文案當 t，鍵不存在時回傳鍵本身，測試才看得出是哪個鍵漏了。 */
const t = (key: string) => key.split('.').reduce<unknown>((o, k) => (o as Record<string, unknown> | undefined)?.[k], zhTW) as string ?? key;

describe('breadcrumbFor', () => {
  it.each([
    ['/dynamic/orders', ['業務', '訂單']],
    ['/dynamic/orders/new', ['業務', '訂單']],
    ['/dynamic/plain/7/edit', ['自訂功能', '維修單']],
    ['/reports', ['報表', '我的報表']],
    ['/reports/3', ['報表', '我的報表']],
    ['/system/users', ['系統管理', '使用者']],
    ['/system/groups', ['系統管理', '群組']],
    ['/system/appearance', ['系統管理', '外觀預設']],
    ['/system/policies', ['系統管理', '政策設定']],
    ['/system/demo-data', ['系統管理', 'Demo 資料']],
    ['/system/dynamic-modules/orders', ['系統管理', '動態模組設計']],
    ['/', []],
    ['/change-password', []],
    ['/dynamic/unknown', []],
  ])('%s', (path, expected) => {
    expect(breadcrumbFor(path, MENU, t, 'zh-TW')).toEqual(expected);
  });
  it('shows the section in the current locale', () => {
    expect(breadcrumbFor('/dynamic/orders', MENU, t, 'en-US')).toEqual(['Sales operations', 'Orders']);
  });

  it('選單還沒載入時不顯示，不報錯', () => {
    expect(breadcrumbFor('/dynamic/orders', [], t, 'zh-TW')).toEqual([]);
  });

  it('不會把 /dynamic-x 之類的前綴相近路徑當成模組', () => {
    expect(breadcrumbFor('/dynamicorders', MENU, t, 'zh-TW')).toEqual([]);
  });
});
