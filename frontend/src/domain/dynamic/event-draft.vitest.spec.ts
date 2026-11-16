import { describe, expect, it } from 'vitest';
import {
  buildEvent, draftFromEvent, emptyEffect, emptyEventDraft, emptySet, formulaFieldIds, groupEventErrors, hiddenFieldIds, nextEffectCode,
  setIsComplete, usesHiddenSource,
} from './event-draft';
import type { ModuleEvent } from './events-ledger';

const shipEvent: ModuleEvent = {
  key: 'ship-post', name: { 'zh-TW': '出貨過帳' }, sourceModuleKey: 'ship', active: true,
  definition: {
    trigger: { on: 'transition', fromStates: [11], toState: 12 },
    when: { all: [{ field: 21, op: 'gt', value: 0 }] },
    effects: [
      { code: 'post', kind: 'create', targetModuleKey: 'stock-move', each: 'ship-line', set: [
        { to: 71, from: { side: 'line', fieldId: 41 }, allowHiddenSource: false },
        { to: 72, formula: { side: 'line', expr: '-{41}' }, allowHiddenSource: true },
        { to: 74, const: 'opt_55', allowHiddenSource: false },
      ] },
      { code: 'touch', kind: 'update', targetModuleKey: 'balance', via: 33, set: [{ to: 61, const: 'today', allowHiddenSource: false }] },
      { code: 'undo', kind: 'reverse', targetModuleKey: 'stock-move', ofEvent: 'ship-post', ofEffect: 'post' },
    ],
  },
};

describe('event draft', () => {
  it('round-trips a saved event unchanged', () => {
    expect(buildEvent(draftFromEvent(shipEvent), 'ship')).toEqual(shipEvent);
  });

  it('keeps a custom effect exactly as the API stored it', () => {
    const custom: ModuleEvent = {
      ...shipEvent, definition: { trigger: { on: 'create' }, effects: [{ code: 'x', kind: 'custom', handler: 'notify', args: { a: 1 } }] },
    };
    expect(buildEvent(draftFromEvent(custom), 'ship')).toEqual(custom);
  });

  it('is not buildable until the required picks are made', () => {
    const d = emptyEventDraft();
    expect(buildEvent(d, 'ship')).toBeUndefined();
    d.key = 'e1';
    expect(buildEvent(d, 'ship')).toBeUndefined(); // no zh-TW name
    d.name = { 'zh-TW': 'x' };
    expect(buildEvent(d, 'ship')).toBeDefined(); // create trigger, no effects, is valid input (server decides)
    d.on = 'transition';
    expect(buildEvent(d, 'ship')).toBeUndefined(); // needs the target state
    d.toState = 12;
    expect(buildEvent(d, 'ship')?.definition.trigger).toEqual({ on: 'transition', toState: 12 });
  });

  it('drops the trigger parts that do not belong to the chosen type', () => {
    const d = draftFromEvent(shipEvent);
    d.on = 'update';
    d.watch = [5];
    const t = buildEvent(d, 'ship')!.definition.trigger;
    expect(t).toEqual({ on: 'update', watch: [5] });
  });

  it('refuses incomplete effects and mapping rows', () => {
    const d = draftFromEvent(shipEvent);
    d.effects[0]!.set.push(emptySet());
    expect(buildEvent(d, 'ship')).toBeUndefined();
    expect(setIsComplete(emptySet())).toBe(false);
    d.effects[0]!.set.pop();
    d.effects[1]!.via = null;
    expect(buildEvent(d, 'ship')).toBeUndefined();
  });

  it('an update effect only reads the header side', () => {
    const d = draftFromEvent(shipEvent);
    const row = { ...emptySet(), to: 9, source: 'from' as const, side: 'line' as const, fieldId: 3 };
    d.effects[1]!.set = [row];
    expect(buildEvent(d, 'ship')!.definition.effects[1]!.set).toEqual([{ to: 9, from: { side: 'header', fieldId: 3 }, allowHiddenSource: false }]);
  });

  it('a mapping from a constant never asks to copy hidden values', () => {
    const d = draftFromEvent(shipEvent);
    d.effects[1]!.set = [{ ...emptySet(), to: 9, source: 'const', value: 5, allowHiddenSource: true }];
    expect(buildEvent(d, 'ship')!.definition.effects[1]!.set).toEqual([{ to: 9, const: 5, allowHiddenSource: false }]);
  });

  it('numbers new effect codes without clashing', () => {
    expect(nextEffectCode([])).toBe('effect-1');
    expect(nextEffectCode([emptyEffect('effect-1'), emptyEffect('effect-3')])).toBe('effect-2');
  });
});

describe('hidden source fields', () => {
  it('collects the fields hidden for any group', () => {
    const ids = hiddenFieldIds({ groups: [], users: [], options: [], fields: [
      { fieldId: 41, groupId: 1, access: 'hidden' }, { fieldId: 42, groupId: 1, access: 'readOnly' }, { fieldId: 41, groupId: 2, access: 'hidden' },
    ] });
    expect([...ids]).toEqual([41]);
  });

  it('reads field ids out of a formula', () => {
    expect(formulaFieldIds('-{41} * ({7} + 2)')).toEqual([41, 7]);
  });

  it('flags a mapping whose source field or formula uses a hidden field', () => {
    const hidden = new Set([41]);
    expect(usesHiddenSource({ ...emptySet(), source: 'from', fieldId: 41 }, hidden)).toBe(true);
    expect(usesHiddenSource({ ...emptySet(), source: 'from', fieldId: 40 }, hidden)).toBe(false);
    expect(usesHiddenSource({ ...emptySet(), source: 'formula', expr: '-{41}' }, hidden)).toBe(true);
    expect(usesHiddenSource({ ...emptySet(), source: 'const', value: 1 }, hidden)).toBe(false);
  });
});

describe('groupEventErrors', () => {
  it('puts each message where the person can act on it', () => {
    const g = groupEventErrors({
      key: ['代碼已被使用'], name: ['請輸入名稱'],
      'definition.trigger.toState': ['目標狀態不在狀態機內'], 'definition.when': ['條件的欄位不屬於來源模組'],
      'definition.effects[0].set[1].to': ['目的欄位型別不相容'],
      'definition.effects[0].targetModuleKey': ['目標模組不存在'],
      'definition.effects[2]': ['循環：a → b → a'],
      definition: ['事件最多 10 個效果'],
    });
    expect(g.key).toEqual(['代碼已被使用']);
    expect(g.name).toEqual(['請輸入名稱']);
    expect(g.trigger).toEqual(['目標狀態不在狀態機內']);
    expect(g.when).toEqual(['條件的欄位不屬於來源模組']);
    expect(g.effects.get(0)?.set.get(1)).toEqual(['目的欄位型別不相容']);
    expect(g.effects.get(0)?.own).toEqual(['目標模組不存在']);
    expect(g.effects.get(2)?.own).toEqual(['循環：a → b → a']);
    expect(g.general).toEqual(['事件最多 10 個效果']);
  });
});
