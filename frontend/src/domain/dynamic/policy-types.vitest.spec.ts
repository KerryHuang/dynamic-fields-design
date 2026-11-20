import { describe, expect, it } from 'vitest';
import { BUILTIN_LOCALES } from 'src/i18n/locale';
import { setLocaleInfos } from 'src/i18n/locale-settings';
import { tFor } from 'src/i18n/translate';
import { choiceLabel, groupByCategory, isLocalePolicy, policyRangeText, policyValueText, samePolicyValue, type SystemPolicy } from './policy-types';

const t = tFor('zh-TW');

describe('policy-types', () => {
  it('samePolicyValue 比內容與順序', () => {
    expect(samePolicyValue(['a', 'b'], ['a', 'b'])).toBe(true);
    expect(samePolicyValue(['a'], ['b'])).toBe(false);
    expect(samePolicyValue(['a', 'b'], ['b', 'a'])).toBe(false);
    expect(samePolicyValue(undefined, undefined)).toBe(true);
    expect(samePolicyValue(20, undefined)).toBe(false);
    expect(samePolicyValue(20, 20)).toBe(true);
  });

  it('utcOffset：policyValueText 顯示 UTC 前綴', () => {
    expect(policyValueText({ code: 'locale.timeZone', category: 'locale', kind: 'utcOffset' }, '+08:00', t)).toBe('UTC+08:00');
  });

  it('policyRangeText', () => {
    expect(policyRangeText({ code: 'x', category: 'task', kind: 'integer', min: 1, max: 100 }, t)).toBe('1 到 100');
    expect(policyRangeText({ code: 'x', category: 'task', kind: 'codeList', min: 1, max: 40 }, t)).toBe('1 到 40 個');
    expect(policyRangeText({ code: 'x', category: 'task', kind: 'boolean' }, t)).toBeNull();
  });

  it('recipients：policyValueText 換成名稱、policyRangeText 寫項數範圍', () => {
    const meta = { code: 'x', category: 'approval', kind: 'recipients', min: 1, max: 20 } as const;
    const names = new Map([['admins', '全部系統管理員'], ['user:fin1', '財務一']]);
    expect(policyValueText(meta, ['admins', 'user:fin1'], t, names)).toBe('全部系統管理員、財務一');
    expect(policyValueText(meta, ['user:gone'], t, names)).toBe('找不到或已停用');
    expect(policyValueText(meta, ['admins', 'user:fin1'], t)).toBe('admins、user:fin1');   // 名稱還沒載入：顯示原始項目，不誤報找不到
    const range = policyRangeText(meta, t);
    expect(range).toContain('1');
    expect(range).toContain('20');
  });

  it('policyValueText', () => {
    expect(policyValueText({ code: 'x', category: 'task', kind: 'boolean' }, true, t)).toBe('開');
    expect(policyValueText({ code: 'x', category: 'task', kind: 'boolean' }, false, t)).toBe('關');
    expect(policyValueText({ code: 'x', category: 'task', kind: 'roleSet', options: [] }, [], t)).toBe('（無）');
    expect(policyValueText({ code: 'x', category: 'task', kind: 'integer', unit: 'bytes' }, 10485760, t)).toContain('10.0 MB');
    expect(policyValueText({ code: 'x', category: 'task', kind: 'integer' }, 20, t)).toBe('20');
    expect(policyValueText({ code: 'x', category: 'task', kind: 'codeList' }, ['pdf', 'png'], t)).toBe('pdf、png');
  });

  it('groupByCategory 保持順序', () => {
    const items = [{ category: 'task', code: 'a' }, { category: 'attachment', code: 'b' }, { category: 'task', code: 'c' }] as SystemPolicy[];
    expect(groupByCategory(items).map((g) => [g.category, g.items.map((i) => i.code)])).toEqual([['task', ['a', 'c']], ['attachment', ['b']]]);
  });
});

describe('語系類政策顯示各語言自稱（不走 policies.choices 翻譯）', () => {
  const meta = (code: string, kind: 'choice' | 'roleSet') => ({ code, category: 'locale', kind, options: ['zh-TW', 'en-US', 'th', 'vi'] });
  it('choice 與 roleSet 的現值', () => {
    expect(policyValueText(meta('locale.default', 'choice'), 'en-US', t)).toBe('English');
    expect(policyValueText(meta('locale.enabled', 'roleSet'), ['zh-TW', 'th'], t)).toBe('繁體中文、ไทย');
  });
  it('新裝的語系：選項含 ja 仍是語系類，標籤是 API 給的自稱', () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: 'en-US' }]);
    const m = { code: 'locale.enabled', category: 'locale', kind: 'roleSet' as const, options: ['zh-TW', 'en-US', 'th', 'vi', 'ja'] };
    expect(isLocalePolicy(m)).toBe(true);
    expect(choiceLabel(m, 'ja', t)).toBe('日本語');
    setLocaleInfos(BUILTIN_LOCALES);
  });
  it('calendar.weekStart 這種非語系的 locale 類 choice 仍走翻譯', () => {
    expect(policyValueText({ code: 'calendar.weekStart', category: 'locale', kind: 'choice', options: ['monday', 'sunday'] }, 'monday', t)).toBe(t('policies.choices.monday'));
  });
});
