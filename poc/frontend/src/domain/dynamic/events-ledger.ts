import type { ConditionNode, I18nText } from './types';

// ---- designer: events ----

export type EventTriggerOn = 'create' | 'update' | 'transition';
export type EffectKind = 'create' | 'update' | 'reverse' | 'custom';

export interface EventTrigger {
  on: EventTriggerOn;
  /** transition: any of these states (absent = any). */
  fromStates?: number[];
  toState?: number;
  /** update: only when one of these field Ids changed (absent = any). */
  watch?: number[];
}

export interface EventSetItem {
  /** Target field Id. */
  to: number;
  from?: { side: 'header' | 'line'; fieldId: number };
  formula?: { side: 'header' | 'line'; expr: string };
  /** A value, `opt_<id>` for a dropdown, or "today" for a date. */
  const?: unknown;
  allowHiddenSource: boolean;
}

export interface EventEffect {
  code: string;
  kind: EffectKind;
  targetModuleKey?: string;
  /** create: the source detail module whose lines each become one target record. */
  each?: string;
  /** update: reference field Id of the source that points at the target record. */
  via?: number;
  set?: EventSetItem[];
  /** reverse */
  ofEvent?: string;
  ofEffect?: string;
  /** custom (API only) */
  handler?: string;
  args?: unknown;
}

export interface EventDefinition {
  trigger: EventTrigger;
  when?: ConditionNode;
  effects: EventEffect[];
}

export interface ModuleEvent {
  key: string;
  name: I18nText;
  sourceModuleKey: string;
  active: boolean;
  definition: EventDefinition;
  /** Present on responses of PUT and POST. */
  warnings?: string[];
}

// ---- designer: checks ----

export type CheckOp = 'gte' | 'gt' | 'lte' | 'lt' | 'eq';
export type CheckLimit = { const?: number } | { field: number } | { via: number; field: number };

export interface CheckDefinition {
  groupBy: number[];
  function: 'sum' | 'count';
  fieldId?: number;
  where?: ConditionNode;
  assert: { op: CheckOp; limit: CheckLimit };
  message: I18nText;
}

export interface ModuleCheck {
  key: string;
  name: I18nText;
  moduleKey: string;
  active: boolean;
  definition: CheckDefinition;
  /** Present on responses of PUT and POST, e.g. that filtering was switched on for a field. */
  notices?: string[];
}

// ---- designer: ledger ----

export interface LedgerConfig {
  moduleKey: string;
  reversalFieldId: number | null;
  negate: number[];
  swap: number[][];
  recordCount: number;
}

export interface LedgerBody {
  reversalFieldId: number;
  negate: number[];
  swap: number[][];
  /** Resend with true after the server (409) reported how many records get frozen. */
  confirmFreeze: boolean;
}

// ---- runtime ----

export interface RecordLedger {
  reversible: boolean;
  reversedBy: { id: number; title: string } | null;
  reverses: { id: number; title: string } | null;
}

export interface EffectItem {
  eventKey: string;
  effectCode: string;
  kind: 'create' | 'reverse';
  targetModuleKey: string;
  targetModuleName: I18nText;
  targetRecordId: number;
  /** null when the user may not see the title (or the record). */
  title: string | null;
  createdTime: string;
}

export interface RecordEffects {
  items: EffectItem[];
  /** Target records the user cannot see; only counted. */
  hiddenCount: number;
}
