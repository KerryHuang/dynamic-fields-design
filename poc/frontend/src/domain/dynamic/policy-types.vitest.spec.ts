import { describe, expect, it } from 'vitest';
import { tFor } from 'src/i18n/translate';
import { groupByCategory, policyRangeText, policyValueText, samePolicyValue, type SystemPolicy } from './policy-types';

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
