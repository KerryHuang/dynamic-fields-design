import { describe, expect, it } from 'vitest';
import catalog from '../../../../shared/policy-catalog.json';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';

type Tree = { policies?: { items?: Record<string, Record<string, unknown>>; categories?: Record<string, string>; choices?: Record<string, string> } };

describe.each([['zh-TW', zhTW], ['en-US', enUS], ['th', th], ['vi', vi]])('政策文案齊全 %s', (_n, msgs) => {
  const p = (msgs as Tree).policies;
  it('每個政策代碼都有名稱與說明', () => {
    for (const code of catalog.codes) {
      const [area, name] = [code.slice(0, code.indexOf('.')), code.slice(code.indexOf('.') + 1)];
      // 代碼含多段（task.notify.commented）時 i18n 鍵逐段巢狀
      const node = name.split('.').reduce<Record<string, unknown> | undefined>((n, k) => n?.[k] as Record<string, unknown> | undefined, p?.items?.[area]);
      expect((node?.name as string)?.trim(), `${code}.name`).toBeTruthy();
      expect((node?.help as string)?.trim(), `${code}.help`).toBeTruthy();
    }
  });
  it('每個分類與選項都有文案', () => {
    for (const c of catalog.categories) expect(p?.categories?.[c], c).toBeTruthy();
    for (const c of catalog.choices) expect(p?.choices?.[c], c).toBeTruthy();
  });
  it('A15 的 zh-TW 名稱照裁決 11', () => {
    if (_n !== 'zh-TW') return;
    expect(p?.items?.approval?.adminBypassTransitionGroups?.name).toBe('系統管理員可以略過狀態轉移的群組限制');
  });
});
