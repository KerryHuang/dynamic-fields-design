import { pruneCondition } from './condition';
import type { ConditionNode, I18nText, Locale, SchemaField } from './types';

export type TaskRuleOn = 'onCreate' | 'onUpdateMatch' | 'onFieldBecomes' | 'manual';
export type TaskRuleRepeat = 'once' | 'everyTime';
export const TRIGGERS: TaskRuleOn[] = ['onCreate', 'onUpdateMatch', 'onFieldBecomes', 'manual'];
export const REPEATS: TaskRuleRepeat[] = ['once', 'everyTime'];

export interface TaskRuleTrigger {
  on: TaskRuleOn;
  /** onFieldBecomes only: a dropdown or yes/no field. */
  fieldId?: number | null;
  /** onFieldBecomes only: `opt_<id>` for a dropdown, true/false for yes/no. */
  value?: string | boolean | null;
}

export interface TaskRuleTaskInput {
  code: string;
  title: I18nText;
  description?: I18nText | null;
  assigneeFieldId: number;
  dueFieldId?: number | null;
  dueOffsetDays: number;
  when?: ConditionNode | null;
}

/** Body of POST／PUT /modules/{key}/task-rules. */
export interface TaskRuleInput {
  key: string;
  name: I18nText;
  active: boolean;
  repeat: TaskRuleRepeat;
  trigger: TaskRuleTrigger;
  when?: ConditionNode | null;
  tasks: TaskRuleTaskInput[];
}

/** The server omits null properties; Warnings are computed on read (e.g. a referenced field was deleted). */
export interface TaskRule extends TaskRuleInput {
  moduleKey: string;
  appKey?: string | null;
  warnings: string[];
}

/** Editing copy: conditions may still hold half-filled rows, descriptions may be empty. */
export type TaskRuleDraft = TaskRuleInput;

export const MAX_TASKS = 20;
export const MAX_OFFSET_DAYS = 3650;
const TEXT_MAX = 200;
const KEY_PATTERN = /^[a-z][a-z0-9-]{1,39}$/;
const CODE_PATTERN = /^[a-z][a-zA-Z0-9_]{0,39}$/;

export const blankTaskRuleDraft = (): TaskRuleDraft => ({
  key: '', name: {}, active: true, repeat: 'once', trigger: { on: 'onCreate' }, tasks: [blankTask(1)],
});
export const blankTask = (n: number): TaskRuleTaskInput => ({
  code: `task${n}`, title: {}, assigneeFieldId: 0, dueFieldId: null, dueOffsetDays: 0,
});

export const assigneeFields = (fields: SchemaField[]) => fields.filter((f) => f.type === 'user');
export const dueFields = (fields: SchemaField[]) => fields.filter((f) => f.type === 'date' || f.type === 'datetime');
export const triggerFields = (fields: SchemaField[]) => fields.filter((f) => f.type === 'enum' || f.type === 'boolean');

const clone = <T>(v: T): T => JSON.parse(JSON.stringify(v)) as T;

export function fromTaskRule(rule: TaskRule): TaskRuleDraft {
  return clone({
    key: rule.key, name: rule.name, active: rule.active, repeat: rule.repeat, trigger: rule.trigger, when: rule.when,
    tasks: rule.tasks,
  });
}

/** Trim every locale and drop empty ones; nothing left means no text. */
function compact(text: I18nText | null | undefined): I18nText | undefined {
  const out: I18nText = {};
  for (const [l, v] of Object.entries(text ?? {})) if (v?.trim()) out[l as Locale] = v.trim();
  return Object.keys(out).length ? out : undefined;
}

export function toTaskRuleBody(draft: TaskRuleDraft): TaskRuleInput {
  const t = draft.trigger;
  const trigger: TaskRuleTrigger = t.on === 'onFieldBecomes' ? { on: t.on, fieldId: t.fieldId, value: t.value } : { on: t.on };
  const when = pruneCondition(draft.when ?? undefined);
  return {
    key: draft.key.trim(),
    name: compact(draft.name) ?? {},
    active: draft.active,
    repeat: draft.repeat,
    trigger,
    ...(when ? { when } : {}),
    tasks: draft.tasks.map((task) => {
      const description = compact(task.description);
      const taskWhen = pruneCondition(task.when ?? undefined);
      return {
        code: task.code.trim(),
        title: compact(task.title) ?? {},
        ...(description ? { description } : {}),
        assigneeFieldId: task.assigneeFieldId,
        ...(task.dueFieldId ? { dueFieldId: task.dueFieldId } : {}),
        dueOffsetDays: task.dueOffsetDays,
        ...(taskWhen ? { when: taskWhen } : {}),
      };
    }),
  };
}

const tooLong = (text: I18nText | null | undefined) => Object.values(text ?? {}).some((v) => (v ?? '').trim().length > TEXT_MAX);

/** Same checks as the server's TaskRuleValidator; returns i18n keys (empty = fine to send). */
export function validateDraft(draft: TaskRuleDraft, fields: SchemaField[]): string[] {
  const errors = new Set<string>();
  const err = (k: string) => errors.add(`taskRules.errors.${k}`);
  const byId = new Map(fields.map((f) => [f.id, f]));

  if (!KEY_PATTERN.test(draft.key.trim())) err('keyBad');
  if (!draft.name['zh-TW']?.trim()) err('nameRequired');
  else if (tooLong(draft.name)) err('nameTooLong');

  const t = draft.trigger;
  if (t.on === 'onUpdateMatch' && !pruneCondition(draft.when ?? undefined)) err('updateMatchNeedsWhen');
  if (t.on === 'onCreate' && draft.repeat === 'everyTime') err('createCannotRepeat');
  if (t.on === 'onFieldBecomes') {
    const f = t.fieldId ? byId.get(t.fieldId) : undefined;
    if (!f || (f.type !== 'enum' && f.type !== 'boolean')) err('triggerField');
    else if (f.type === 'boolean' ? typeof t.value !== 'boolean' : !f.options.some((o) => o.value === t.value)) err('triggerValue');
  }

  if (draft.tasks.length < 1 || draft.tasks.length > MAX_TASKS) err('tasksCount');
  const codes = new Set<string>();
  for (const task of draft.tasks) {
    const code = task.code.trim();
    if (!CODE_PATTERN.test(code)) err('taskCodeBad');
    else if (codes.has(code)) err('taskCodeDup');
    codes.add(code);
    if (!task.title['zh-TW']?.trim()) err('titleRequired');
    else if (tooLong(task.title)) err('titleTooLong');
    if (tooLong(task.description)) err('descriptionTooLong');
    if (byId.get(task.assigneeFieldId)?.type !== 'user') err('assigneeField');
    if (task.dueFieldId) {
      const due = byId.get(task.dueFieldId);
      if (due?.type !== 'date' && due?.type !== 'datetime') err('dueField');
    }
    if (!Number.isInteger(task.dueOffsetDays) || Math.abs(task.dueOffsetDays) > MAX_OFFSET_DAYS) err('dueOffsetRange');
  }
  return [...errors];
}
