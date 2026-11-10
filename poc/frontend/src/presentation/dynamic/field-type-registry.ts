import type { Component } from 'vue';
import { getActivePinia } from 'pinia';
import { z, type ZodTypeAny } from 'zod';
import AttachmentInput from 'src/components/framework/AttachmentInput.vue';
import DateSelect from 'src/components/framework/DateSelect.vue';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import MoneyNumberInput from 'src/components/framework/MoneyNumberInput.vue';
import PopupSelect from 'src/components/framework/PopupSelect.vue';
import TextareaInput from 'src/components/framework/TextareaInput.vue';
import TextInput from 'src/components/framework/TextInput.vue';
import TimeInput from 'src/components/framework/TimeInput.vue';
import ToggleInput from 'src/components/framework/ToggleInput.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { operatorNeedsValue } from 'src/domain/dynamic/condition';
import type { FieldType, FilterOperator, SchemaField } from 'src/domain/dynamic/types';
import { currentLocale, joinList, tFor, type Translate } from 'src/i18n/translate';
import { useIdentityStore } from 'src/stores/use-identity-store';

export interface FieldTypeEntry {
  component: Component;
  props(field: SchemaField, locale: string): Record<string, unknown>;
  zod(field: SchemaField): ZodTypeAny;
  format(value: unknown, field: SchemaField, locale: string): string;
  /** Operators allowed in a filter; mirrors poc/shared/filter-operators.json. */
  operators: FilterOperator[];
  /**
   * Input for the filter value; undefined when the operator takes no value.
   * `between` uses the same input twice (FilterPanel lays out the pair);
   * `in`/`notIn` use a multi-select (text-like types: a plain TextInput, FilterPanel splits the list).
   */
  filterInput(field: SchemaField, op: FilterOperator): { component: Component; props: Record<string, unknown> } | undefined;
}

const V = 'dynamic.validation.';
const REQUIRED = V + 'required';
const OPTION_GONE = V + 'optionGone';

/** A message as i18n key plus params; the caller turns it into text with t(). */
export interface CheckMessage { key: string; params?: Record<string, unknown> }
const msgOf = (key: string, params?: Record<string, unknown>): CheckMessage => ({ key, params });

/** Returns an error message, or null when valid. `v` is never null/empty here. */
type Check = (v: unknown, field: SchemaField, previous: unknown) => CheckMessage | null;

const scaleOf = (f: SchemaField) => f.config.scale ?? 2;
const isNum = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const isStr = (v: unknown, re: RegExp, key: string) => (typeof v === 'string' && re.test(v) ? null : msgOf(key));

function scaleCheck(v: unknown, f: SchemaField) {
  if (!isNum(v)) return msgOf(V + 'number');
  const decimals = (String(v).split('.')[1] ?? '').length;
  return decimals > scaleOf(f) ? msgOf(V + 'maxDecimals', { n: scaleOf(f) }) : null;
}
const textCheck = (fallback: number): Check => (v, f) => {
  const max = f.config.maxLength ?? fallback;
  return typeof v === 'string' && v.length <= max ? null : msgOf(V + 'maxLength', { n: max });
};
const activeOrPrevious = (value: unknown, f: SchemaField, previous: unknown) =>
  typeof value === 'string' && ([previous].flat().includes(value) || f.options.some((o) => o.active && o.value === value));

const checks: Record<FieldType, Check> = {
  text: textCheck(255),
  textarea: textCheck(4000),
  integer: (v) => (isNum(v) && Number.isInteger(v) ? null : msgOf(V + 'integer')),
  decimal: scaleCheck,
  currency: scaleCheck,
  percent: scaleCheck,
  date: (v) => {
    const ok = typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v) && new Date(`${v}T00:00:00Z`).toISOString().startsWith(v);
    return ok ? null : msgOf(V + 'date');
  },
  datetime: (v) => isStr(v, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/, V + 'datetime') ?? (Number.isNaN(Date.parse(v as string)) ? msgOf(V + 'datetime') : null),
  time: (v) => isStr(v, /^([01]\d|2[0-3]):[0-5]\d$/, V + 'time'),
  boolean: (v) => (typeof v === 'boolean' ? null : msgOf(V + 'boolean')),
  email: (v) => isStr(v, /^[^@\s]+@[^@\s]+\.[^@\s]+$/, V + 'email'),
  phone: (v) => isStr(v, /^[\d +\-()#]{1,30}$/, V + 'phone'),
  url: (v) => {
    const msg = msgOf(V + 'url');
    if (typeof v !== 'string' || !/^https?:\/\//.test(v)) return msg;
    return URL.canParse(v) ? null : msg;
  },
  enum: (v, f, prev) => (activeOrPrevious(v, f, prev) ? null : msgOf(OPTION_GONE)),
  multienum: (v, f, prev) => {
    return Array.isArray(v) && v.every((x) => activeOrPrevious(x, f, prev)) ? null : msgOf(OPTION_GONE);
  },
  // Frontend has no user directory beyond ids; the backend enforces existence.
  user: (v) => (typeof v === 'string' ? null : msgOf(V + 'user')),
  // System-computed: the backend ignores any input, so nothing to check here.
  formula: () => null,
  rollup: () => null,
  autonumber: () => null,
  // Module targets store the record id (number), entity targets the entity key (string); existence is checked by the backend.
  // Files enter only through the upload endpoint; the backend rejects ids it does not already hold.
  attachment: (v) => (Array.isArray(v) ? null : msgOf(V + 'attachment')),
  reference: (v) => (isNum(v) || (typeof v === 'string' && v !== '') ? null : msgOf(V + 'reference')),
};

/** The browser's local date as yyyy-MM-dd. */
function localToday(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** Create-mode default; one pointing at a deactivated option counts as no default, like the backend (R19). */
export function defaultValueOf(f: SchemaField, today: string = localToday()): unknown {
  if (f.type === 'date' && f.config.defaultToday) return today;
  const d = f.config.defaultValue;
  const active = new Set(f.options.filter((o) => o.active).map((o) => o.value));
  if (f.type === 'enum') return typeof d === 'string' && active.has(d) ? d : null;
  if (f.type === 'multienum' && Array.isArray(d)) {
    const kept = d.filter((v) => active.has(v as string));
    return kept.length ? kept : null;
  }
  return d;
}

/** null, blank string (after trim for text types) and empty array all count as "no value". */
function normalize(v: unknown, type: FieldType): unknown {
  if (v === undefined || v === null) return null;
  if (typeof v === 'string') {
    const s = type === 'text' || type === 'textarea' ? v.trim() : v;
    return s === '' ? null : s;
  }
  if (Array.isArray(v) && v.length === 0) return null;
  return v;
}

const toTaipei = (iso: string) => {
  const d = new Date(Date.parse(iso) + 8 * 3600_000);
  return `${d.toISOString().slice(0, 19)}+08:00`;
};

const OFFSET_DATETIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/;

/**
 * Form model value as the rule engine (and backend) see it: blank becomes null, datetime carries +08:00.
 * A datetime without an offset or unparsable is treated as no value so the engine never depends on the machine timezone.
 */
export function normalizeForRules(raw: unknown, type: FieldType): unknown {
  const v = normalize(raw, type);
  if (type !== 'datetime' || v === null) return v;
  return typeof v === 'string' && OFFSET_DATETIME.test(v) && !Number.isNaN(Date.parse(v)) ? toTaipei(v) : null;
}

/**
 * Builds the validator for one field; `previous` is the stored value in edit mode (keeps inactive options valid).
 * `required` overrides config.required (the rule engine decides it per record).
 */
export function zodFor(field: SchemaField, previous?: unknown, required = field.config.required, t: Translate = tFor(currentLocale())): ZodTypeAny {
  return z.unknown().transform((raw, ctx) => {
    const v = normalize(raw, field.type);
    if (v === null) {
      if (required) ctx.addIssue({ code: 'custom', message: t(REQUIRED) });
      return null;
    }
    const err = checks[field.type](v, field, previous);
    if (err) {
      ctx.addIssue({ code: 'custom', message: t(err.key, err.params) });
      return z.NEVER;
    }
    if (field.type === 'datetime') return toTaipei(v as string);
    if (field.type === 'multienum') {
      const order = new Map(field.options.map((o, i) => [o.value, i]));
      return [...new Set(v as string[])].sort((a, b) => (order.get(a) ?? 1e9) - (order.get(b) ?? 1e9));
    }
    return v;
  });
}

const optionLabel = (field: SchemaField, value: string, locale: string) => {
  const o = field.options.find((x) => x.value === value);
  return o ? resolveI18n(o.label, locale) : value;
};

const baseProps = (f: SchemaField, locale: string) => ({
  label: resolveI18n(f.label, locale),
  required: f.config.required,
  readonly: f.config.readOnly,
});
const plain = (component: Component, extra: (f: SchemaField) => Record<string, unknown> = () => ({})) => ({
  component,
  props: (f: SchemaField, locale: string) => ({ ...baseProps(f, locale), ...extra(f) }),
});
const selectProps = (multiple: boolean) => (f: SchemaField, locale: string) => ({
  ...baseProps(f, locale),
  multiple,
  options: f.options.map((o) => ({ value: o.value, label: resolveI18n(o.label, locale), color: o.color, disable: !o.active })),
});

const asText = (v: unknown) => String(v);
const formatCurrency = (v: unknown, f: SchemaField, locale: string) =>
  Number(v).toLocaleString(locale, { minimumFractionDigits: scaleOf(f), maximumFractionDigits: scaleOf(f) });
const number = (scale: (f: SchemaField) => number) => ({
  ...plain(MoneyNumberInput, (f) => ({ scale: scale(f) })),
});
const OPERATORS: Record<FieldType, FilterOperator[]> = {
  text: ['eq', 'ne', 'in', 'notIn', 'contains', 'startsWith', 'isEmpty', 'isNotEmpty'],
  email: ['eq', 'ne', 'in', 'notIn', 'contains', 'startsWith', 'isEmpty', 'isNotEmpty'],
  phone: ['eq', 'ne', 'in', 'notIn', 'contains', 'startsWith', 'isEmpty', 'isNotEmpty'],
  url: ['eq', 'ne', 'in', 'notIn', 'contains', 'startsWith', 'isEmpty', 'isNotEmpty'],
  integer: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  decimal: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  currency: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  percent: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  time: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  date: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'withinNextDays', 'withinLastDays', 'beforeToday', 'isEmpty', 'isNotEmpty'],
  datetime: ['gt', 'gte', 'lt', 'lte', 'between', 'withinNextDays', 'withinLastDays', 'beforeToday', 'isEmpty', 'isNotEmpty'],
  boolean: ['eq', 'isEmpty', 'isNotEmpty'],
  enum: ['eq', 'ne', 'in', 'notIn', 'isEmpty', 'isNotEmpty'],
  user: ['eq', 'ne', 'in', 'notIn', 'isEmpty', 'isNotEmpty'],
  multienum: ['in', 'notIn', 'isEmpty', 'isNotEmpty'],
  textarea: [],
  formula: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  rollup: ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'between', 'isEmpty', 'isNotEmpty'],
  autonumber: ['eq', 'ne', 'in', 'notIn', 'contains', 'startsWith', 'isEmpty', 'isNotEmpty'],
  reference: ['eq', 'ne', 'in', 'notIn', 'isEmpty', 'isNotEmpty'],
  attachment: [],
};
const FORMULA_DATE_OPERATORS: FilterOperator[] = OPERATORS.date;

/** Filter operators for one field; a date-valued formula or rollup takes the date operators (shared keys `formula:date`, `rollup:date`). */
export function operatorsFor(field: SchemaField): FilterOperator[] {
  return (field.type === 'formula' || field.type === 'rollup') && field.config.resultType === 'date' ? FORMULA_DATE_OPERATORS : OPERATORS[field.type];
}

const filterBase = { label: '' };
const popupProps = (f: SchemaField) => ({
  lightUrl: f.reference?.lightUrl ?? '',
  valueKind: f.reference?.kind === 'module' ? 'number' : 'string',
});
const referenceFilterInput: FieldTypeEntry['filterInput'] = (field, op) => {
  const need = operatorNeedsValue(op);
  if (need === 'none') return undefined;
  return { component: PopupSelect, props: { ...filterBase, ...popupProps(field), multiple: need === 'list' } };
};
/** Filter input per type; options stay selectable even when inactive. */
function filterInputFor(type: FieldType, component: Component) {
  return (field: SchemaField, op: FilterOperator) => {
    const need = operatorNeedsValue(op);
    if (need === 'none') return undefined;
    if (need === 'days') return { component: MoneyNumberInput, props: { ...filterBase, scale: 0 } };
    if (type === 'enum' || type === 'multienum' || type === 'user') {
      const options = type === 'user'
        ? useIdentityStore().users.map((u) => ({ value: u.userId, label: u.displayName }))
        : field.options.map((o) => ({ value: o.value, label: resolveI18n(o.label, 'zh-TW'), color: o.color }));
      return { component, props: { ...filterBase, multiple: need === 'list' || type === 'multienum', options } };
    }
    const extra: Record<string, unknown> = type === 'datetime' ? { withTime: true } : {};
    if (type === 'integer') extra.scale = 0;
    else if (type === 'decimal' || type === 'currency' || type === 'percent') extra.scale = scaleOf(field);
    return { component, props: { ...filterBase, ...extra } };
  };
}
const formulaFilterInput: FieldTypeEntry['filterInput'] = (field, op) => {
  const need = operatorNeedsValue(op);
  if (need === 'none') return undefined;
  if (need === 'days') return { component: MoneyNumberInput, props: { ...filterBase, scale: 0 } };
  if (field.config.resultType === 'date') return { component: DateSelect, props: { ...filterBase } };
  return { component: MoneyNumberInput, props: { ...filterBase, scale: scaleOf(field) } };
};
const readOnlyText = plain(TextInput, () => ({ readonly: true }));
const entry = (
  type: FieldType,
  base: { component: Component; props: FieldTypeEntry['props'] },
  format: (v: unknown, f: SchemaField, locale: string) => string = asText,
): FieldTypeEntry => ({
  ...base,
  operators: OPERATORS[type],
  filterInput: filterInputFor(type, base.component),
  zod: (f) => zodFor(f),
  format: (v, f, locale) => (v === null || v === undefined || v === '' ? '' : format(v, f, locale)),
});

export const fieldTypeRegistry: Record<FieldType, FieldTypeEntry> = {
  text: entry('text', plain(TextInput)),
  textarea: entry('textarea', plain(TextareaInput)),
  integer: entry('integer', number(() => 0)),
  decimal: entry('decimal', number(scaleOf)),
  currency: entry('currency', number(scaleOf), formatCurrency),
  percent: entry('percent', number(scaleOf), (v) => `${v}%`),
  date: entry('date', plain(DateSelect)),
  datetime: entry('datetime', plain(DateSelect, () => ({ withTime: true })), (v) => asText(v).replace('T', ' ').slice(0, 16)),
  time: entry('time', plain(TimeInput)),
  boolean: entry('boolean', plain(ToggleInput), (v, _f, l) => tFor(l)(v ? 'common.yes' : 'common.no')),
  email: entry('email', plain(TextInput)),
  phone: entry('phone', plain(TextInput)),
  url: entry('url', plain(TextInput)),
  enum: entry('enum', { component: GenericSelect, props: selectProps(false) }, (v, f, l) => optionLabel(f, asText(v), l)),
  multienum: entry('multienum', { component: GenericSelect, props: selectProps(true) }, (v, f, l) => {
    const order = new Map(f.options.map((o, i) => [o.value, i]));
    return joinList(
      (v as string[]).toSorted((a, b) => (order.get(a) ?? 1e9) - (order.get(b) ?? 1e9)).map((x) => optionLabel(f, x, l)),
      l,
    );
  }),
  // Options come from the identity store's /users list: value = userId, label = displayName.
  formula: {
    ...entry('formula', readOnlyText, (v, f, l) =>
      f.config.resultType === 'currency' ? formatCurrency(v, f, l) : asText(v)),
    filterInput: formulaFilterInput,
  },
  rollup: {
    ...entry('rollup', readOnlyText, (v, f, l) => (f.config.resultType === 'currency' ? formatCurrency(v, f, l) : asText(v))),
    filterInput: formulaFilterInput,
  },
  autonumber: entry('autonumber', readOnlyText),
  attachment: entry(
    'attachment',
    plain(AttachmentInput, (f) => ({ fieldId: f.id })),
    (v, _f, l) => joinList((v as { name: string }[]).map((x) => x.name), l),
  ),
  // format shows the key: it has no access to record.references; list/detail pages render the title.
  reference: {
    ...entry('reference', plain(PopupSelect, popupProps)),
    filterInput: referenceFilterInput,
  },
  user: entry('user', {
    component: GenericSelect,
    props: (f, locale) => ({
      ...baseProps(f, locale),
      multiple: false,
      options: useIdentityStore().users.map((u) => ({ value: u.userId, label: u.displayName })),
    }),
  }, (v) => {
    const id = asText(v);
    // 清單與明細顯示名稱；使用者清單還沒載入或已不存在時退回 id
    return (getActivePinia() ? useIdentityStore().users.find((u) => u.userId === id)?.displayName : undefined) ?? id;
  }),
};
