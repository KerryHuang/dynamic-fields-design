import { pruneCondition } from './condition';
import type { EffectKind, EventEffect, EventSetItem, EventTriggerOn, ModuleEvent } from './events-ledger';
import type { ConditionNode, I18nText, ModulePermissions } from './types';

/** One row of an effect's mapping: target field ← source field, constant or formula. */
export interface SetDraft {
  to: number | null;
  source: 'from' | 'const' | 'formula';
  side: 'header' | 'line';
  fieldId: number | null;
  expr: string;
  value: unknown;
  allowHiddenSource: boolean;
}

export interface EffectDraft {
  code: string;
  kind: EffectKind;
  targetModuleKey: string | null;
  each: string | null;
  via: number | null;
  set: SetDraft[];
  ofEvent: string | null;
  ofEffect: string | null;
  /** A custom effect is only viewed here and sent back exactly as stored. */
  custom?: EventEffect;
  /** Came from the saved event: its code is fixed (effects can only be added or removed). */
  saved?: boolean;
}

export interface EventDraft {
  key: string;
  name: I18nText;
  active: boolean;
  on: EventTriggerOn;
  fromStates: number[];
  toState: number | null;
  watch: number[];
  when?: ConditionNode;
  effects: EffectDraft[];
}

export const MAX_EFFECTS = 10;

export const emptySet = (): SetDraft => ({
  to: null, source: 'from', side: 'header', fieldId: null, expr: '', value: null, allowHiddenSource: false,
});

export const emptyEffect = (code: string): EffectDraft => ({
  code, kind: 'create', targetModuleKey: null, each: null, via: null, set: [], ofEvent: null, ofEffect: null,
});

export const emptyEventDraft = (): EventDraft => ({
  key: '', name: {}, active: true, on: 'create', fromStates: [], toState: null, watch: [], effects: [],
});

/** `effect-1`, `effect-2`, … the first number not taken (a code must stay unique within the event). */
export function nextEffectCode(effects: { code: string }[]): string {
  const taken = new Set(effects.map((e) => e.code));
  let n = 1;
  while (taken.has(`effect-${n}`)) n++;
  return `effect-${n}`;
}

const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v));

function setDraft(s: EventSetItem): SetDraft {
  const base = { ...emptySet(), to: s.to, allowHiddenSource: s.allowHiddenSource };
  if (s.from) return { ...base, source: 'from', side: s.from.side, fieldId: s.from.fieldId };
  if (s.formula) return { ...base, source: 'formula', side: s.formula.side, expr: s.formula.expr };
  return { ...base, source: 'const', value: s.const ?? null };
}

function effectDraft(e: EventEffect): EffectDraft {
  if (e.kind === 'custom') return { ...emptyEffect(e.code), kind: 'custom', custom: clone(e), saved: true };
  return {
    saved: true, code: e.code, kind: e.kind, targetModuleKey: e.targetModuleKey ?? null, each: e.each ?? null, via: e.via ?? null,
    set: (e.set ?? []).map(setDraft), ofEvent: e.ofEvent ?? null, ofEffect: e.ofEffect ?? null,
  };
}

export function draftFromEvent(e: ModuleEvent): EventDraft {
  const t = e.definition.trigger;
  return {
    key: e.key, name: { ...e.name }, active: e.active, on: t.on, fromStates: [...(t.fromStates ?? [])], toState: t.toState ?? null,
    watch: [...(t.watch ?? [])], ...(e.definition.when && { when: clone(e.definition.when) }), effects: e.definition.effects.map(effectDraft),
  };
}

const filled = (v: unknown) => v !== undefined && v !== null && v !== '';

export function setIsComplete(s: SetDraft): boolean {
  if (s.to == null) return false;
  if (s.source === 'from') return s.fieldId != null;
  if (s.source === 'formula') return !!s.expr.trim();
  return filled(s.value);
}

function buildSet(s: SetDraft, header: boolean): EventSetItem {
  // An update effect has no detail rows to read from.
  const side = header ? 'header' : s.side;
  if (s.source === 'from') return { to: s.to!, from: { side, fieldId: s.fieldId! }, allowHiddenSource: s.allowHiddenSource };
  if (s.source === 'formula') return { to: s.to!, formula: { side, expr: s.expr.trim() }, allowHiddenSource: s.allowHiddenSource };
  return { to: s.to!, const: s.value, allowHiddenSource: false };
}

/** The effect to send, or undefined while a required pick is missing. */
function buildEffect(e: EffectDraft): EventEffect | undefined {
  if (e.custom) return clone(e.custom);
  if (!e.code.trim() || !e.targetModuleKey) return undefined;
  const base = { code: e.code.trim(), kind: e.kind, targetModuleKey: e.targetModuleKey };
  if (e.kind === 'reverse') {
    return e.ofEvent && e.ofEffect ? { ...base, ofEvent: e.ofEvent, ofEffect: e.ofEffect } : undefined;
  }
  if (!e.set.every(setIsComplete)) return undefined;
  if (e.kind === 'update') {
    return e.via == null ? undefined : { ...base, via: e.via, set: e.set.map((s) => buildSet(s, true)) };
  }
  return { ...base, ...(e.each && { each: e.each }), set: e.set.map((s) => buildSet(s, false)) };
}

/** Request body, or undefined while a required pick is missing (the server validates the rest and its messages are shown as they come). */
export function buildEvent(d: EventDraft, sourceModuleKey: string): ModuleEvent | undefined {
  if (!d.key.trim() || !d.name['zh-TW']?.trim()) return undefined;
  if (d.on === 'transition' && d.toState == null) return undefined;
  const effects = d.effects.map(buildEffect);
  if (effects.some((e) => !e)) return undefined;
  const trigger = d.on === 'transition'
    ? { on: d.on, ...(d.fromStates.length && { fromStates: [...d.fromStates] }), toState: d.toState! }
    : d.on === 'update' ? { on: d.on, ...(d.watch.length && { watch: [...d.watch] }) } : { on: d.on };
  const when = pruneCondition(d.when);
  return {
    key: d.key.trim(), name: d.name, sourceModuleKey, active: d.active,
    definition: { trigger, ...(when && { when }), effects: effects as EventEffect[] },
  };
}

// ---- hidden source fields ----

/** Fields hidden for at least one group: copying them elsewhere needs the designer's explicit consent. */
export function hiddenFieldIds(p: ModulePermissions | undefined): Set<number> {
  return new Set((p?.fields ?? []).filter((f) => f.access === 'hidden').map((f) => f.fieldId));
}

export const formulaFieldIds = (expr: string): number[] => [...expr.matchAll(/\{(\d+)\}/g)].map((m) => Number(m[1]));

/** True when the mapping reads a field of `hidden` (pass the set of the side the row reads from). */
export function usesHiddenSource(s: SetDraft, hidden: Set<number>): boolean {
  if (s.source === 'from') return s.fieldId != null && hidden.has(s.fieldId);
  if (s.source === 'formula') return formulaFieldIds(s.expr).some((id) => hidden.has(id));
  return false;
}

// ---- server errors ----

export interface EffectErrors { own: string[]; set: Map<number, string[]> }
export interface EventErrors {
  key: string[];
  name: string[];
  trigger: string[];
  when: string[];
  effects: Map<number, EffectErrors>;
  /** Everything else, e.g. a cycle through several events or a limit. */
  general: string[];
}

/** Sorts 400 `errors` (keys like `definition.effects[0].set[1].to`) by the input they belong to. */
export function groupEventErrors(errors: Record<string, string[]> | undefined): EventErrors {
  const out: EventErrors = { key: [], name: [], trigger: [], when: [], effects: new Map(), general: [] };
  const effect = (i: number) => {
    if (!out.effects.has(i)) out.effects.set(i, { own: [], set: new Map() });
    return out.effects.get(i)!;
  };
  for (const [k, messages] of Object.entries(errors ?? {})) {
    const set = /^definition\.effects\[(\d+)\]\.set\[(\d+)\]/i.exec(k);
    const eff = /^definition\.effects\[(\d+)\]/i.exec(k);
    if (set) {
      const rows = effect(Number(set[1])).set;
      rows.set(Number(set[2]), [...(rows.get(Number(set[2])) ?? []), ...messages]);
    } else if (eff) effect(Number(eff[1])).own.push(...messages);
    else if (/^definition\.trigger/i.test(k)) out.trigger.push(...messages);
    else if (/^definition\.when/i.test(k)) out.when.push(...messages);
    else if (/^key/i.test(k)) out.key.push(...messages);
    else if (/^name/i.test(k)) out.name.push(...messages);
    else out.general.push(...messages);
  }
  return out;
}
