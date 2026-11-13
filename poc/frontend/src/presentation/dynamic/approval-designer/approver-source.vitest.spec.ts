import { describe, expect, it } from 'vitest';
import zhTW from 'src/i18n/zh-TW.json';
import { clearUnmatched, defaultSourceOf, hintOf, isSingleApprover, levelOptions, removalBlocked, unmatchedOf } from './approver-source';

const lookup = (k: string) => k.split('.').reduce<unknown>((o, p) => (o as Record<string, unknown>)[p], zhTW) as string;
const t = (k: string, p: Record<string, unknown> = {}) => lookup(k).replace(/\{(\w+)\}/g, (_, n) => String(p[n]));

describe('approver-source', () => {
  it('isSingleApprover for manager, userField, one user, pick max 1', () => {
    expect(isSingleApprover({ source: 'manager', level: 1 })).toBe(true);
    expect(isSingleApprover({ source: 'userField', fieldId: 1, candidateGroupIds: [1] })).toBe(true);
    expect(isSingleApprover({ source: 'users', userIds: ['a'] })).toBe(true);
    expect(isSingleApprover({ source: 'users', userIds: ['a', 'b'] })).toBe(false);
    expect(isSingleApprover({ source: 'initiatorPick', min: 1, max: 1 })).toBe(true);
    expect(isSingleApprover({ source: 'initiatorPick', min: 1, max: 3 })).toBe(false);
    expect(isSingleApprover({ source: 'group', groupIds: [1] })).toBe(false);
  });

  it('levelOptions keeps a saved level above max and marks it', () => {
    expect(levelOptions(3)).toEqual([{ value: 1, legacy: false }, { value: 2, legacy: false }, { value: 3, legacy: false }]);
    expect(levelOptions(2, 4).map((o) => [o.value, o.legacy])).toEqual([[1, false], [2, false], [3, true], [4, true]]);
    expect(levelOptions(undefined, 2)).toEqual([]);
  });

  it('hint texts carry policy numbers and omit them when undefined', () => {
    expect(hintOf('users', { maxApprovers: 5, techMaxApprovers: 50 }, t)).toBe('存檔時最多 5 位（模組政策）。名單存檔後，執行時只受技術上限 50 位限制。');
    expect(hintOf('group', { maxApprovers: 5 }, t)).toBe('每一關啟動時，解析出的人數不能超過當時的每關人數上限（目前 5 位）。');
    expect(hintOf('manager', { maxApprovers: 5 }, t)).toBe(hintOf('userField', { maxApprovers: 5 }, t));
    expect(hintOf('initiatorPick', { maxApprovers: 5 }, t)).toContain('最多人數不能超過 5');
    expect(hintOf('users', { maxApprovers: 5 }, t)).toBeUndefined();
    expect(hintOf('group', {}, t)).toBeUndefined();
    expect(hintOf('users', { techMaxApprovers: 50 }, t, true)).toBe('備援簽核人頂替找不到人的來源時，不計入每關人數上限；整關合計（含備援，去重後）不超過 50 位。');
    expect(hintOf('users', {}, t, true)).toBeUndefined();
  });

  it('defaultSourceOf gives each kind its own params', () => {
    expect(defaultSourceOf('manager')).toEqual({ source: 'manager', level: 1 });
    expect(defaultSourceOf('initiatorPick')).toEqual({ source: 'initiatorPick', candidateGroupIds: [], min: 1, max: 1 });
    expect(defaultSourceOf('users')).toEqual({ source: 'users', userIds: [] });
  });

  it('unmatchedOf returns names and the candidates flag', () => {
    expect(unmatchedOf({ source: 'group', groupIds: [1], unmatchedGroups: ['採購組', '財務組'] })).toEqual({ names: ['採購組', '財務組'], candidates: false });
    expect(unmatchedOf({ source: 'userField', fieldId: 1, candidateGroupIds: [], candidatesUnmatched: true })).toEqual({ names: [], candidates: true });
    expect(unmatchedOf({ source: 'group', groupIds: [1], unmatchedGroups: null, candidatesUnmatched: false })).toEqual({ names: [], candidates: false });
    expect(unmatchedOf({ source: 'manager', level: 1 })).toEqual({ names: [], candidates: false });
  });

  it('unmatchedOf keeps blank names so the export check and the notice agree', () => {
    expect(unmatchedOf({ source: 'group', groupIds: [], unmatchedGroups: [' '] })).toEqual({ names: [' '], candidates: false });
  });

  it('removalBlocked only for a pick source whose candidates are all skipped', () => {
    expect(removalBlocked({ source: 'initiatorPick', candidateGroupIds: [], min: 1, max: 1, candidatesUnmatched: true })).toBe(true);
    expect(removalBlocked({ source: 'initiatorPick', candidateGroupIds: [3], min: 1, max: 1, candidatesUnmatched: true })).toBe(false);
    expect(removalBlocked({ source: 'userField', fieldId: 1, candidateGroupIds: [], candidatesUnmatched: true })).toBe(false);
    expect(removalBlocked({ source: 'initiatorPick', candidateGroupIds: [], min: 1, max: 1 })).toBe(false);
  });

  it('clearUnmatched keeps the other properties', () => {
    const cleared = clearUnmatched({ source: 'group', groupIds: [3], unmatchedGroups: ['採購組'], candidatesUnmatched: true });
    expect(cleared).toEqual({ source: 'group', groupIds: [3], unmatchedGroups: null, candidatesUnmatched: false });
  });
});
