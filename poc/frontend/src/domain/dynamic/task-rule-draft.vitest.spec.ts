import { describe, expect, it } from 'vitest';
import type { SchemaField } from './types';
import {
  assigneeFields, blankTaskRuleDraft, dueFields, fromTaskRule, toTaskRuleBody, validateDraft, type TaskRule, type TaskRuleDraft,
} from './task-rule-draft';

const fld = (id: number, type: SchemaField['type']): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': `欄${id}` }, config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false },
  options: type === 'enum' ? [{ value: 'opt_7', label: { 'zh-TW': '甲' }, active: true }] : [],
});
const fields = [fld(1, 'user'), fld(2, 'date'), fld(3, 'datetime'), fld(4, 'text'), fld(5, 'enum'), fld(6, 'boolean')];

const LIMITS = { maxTasks: 20, originalTaskCount: 0 };
const valid = (): TaskRuleDraft => ({
  ...blankTaskRuleDraft(),
  key: 'follow-up', name: { 'zh-TW': '跟進' }, trigger: { on: 'manual' },
  tasks: [{ code: 'call', title: { 'zh-TW': '回電' }, assigneeFieldId: 1, dueOffsetDays: 0 }],
});

describe('task-rule-draft', () => {
  it('負責人只列 user 欄位，到期基準只列 date／datetime 欄位', () => {
    expect(assigneeFields(fields).map((f) => f.id)).toEqual([1]);
    expect(dueFields(fields).map((f) => f.id)).toEqual([2, 3]);
  });

  it('合法草稿沒有錯誤', () => {
    expect(validateDraft(valid(), fields, LIMITS)).toEqual([]);
  });

  it('onUpdateMatch 沒條件不能存，有條件就過', () => {
    const d = valid();
    d.trigger = { on: 'onUpdateMatch' };
    expect(validateDraft(d, fields, LIMITS)).toContain('taskRules.errors.updateMatchNeedsWhen');
    d.when = { all: [{ field: 4, op: 'eq', value: 'x' }] };
    expect(validateDraft(d, fields, LIMITS)).toEqual([]);
  });

  it('onCreate 不能 everyTime', () => {
    const d = valid();
    d.trigger = { on: 'onCreate' };
    d.repeat = 'everyTime';
    expect(validateDraft(d, fields, LIMITS)).toContain('taskRules.errors.createCannotRepeat');
  });

  it('負責人不是 user 欄位、到期基準不是日期欄位都擋下', () => {
    const d = valid();
    d.tasks[0]!.assigneeFieldId = 4;
    d.tasks[0]!.dueFieldId = 4;
    const errs = validateDraft(d, fields, LIMITS);
    expect(errs).toContain('taskRules.errors.assigneeField');
    expect(errs).toContain('taskRules.errors.dueField');
  });

  it('標題 zh-TW 必填，各語系 ≤ 200', () => {
    const d = valid();
    d.tasks[0]!.title = { 'en-US': 'Call' };
    expect(validateDraft(d, fields, LIMITS)).toContain('taskRules.errors.titleRequired');
    d.tasks[0]!.title = { 'zh-TW': '回電', th: 'ก'.repeat(201) };
    expect(validateDraft(d, fields, LIMITS)).toContain('taskRules.errors.titleTooLong');
    d.tasks[0]!.title = { 'zh-TW': '回'.repeat(200) };
    expect(validateDraft(d, fields, LIMITS)).toEqual([]);
  });

  it('任務說明各語系 ≤ 4000（與後端一致）', () => {
    const d = valid();
    d.tasks[0]!.description = { 'zh-TW': '說'.repeat(4000) };
    expect(validateDraft(d, fields, LIMITS)).toEqual([]);
    d.tasks[0]!.description = { 'zh-TW': '說'.repeat(4001) };
    expect(validateDraft(d, fields, LIMITS)).toContain('taskRules.errors.descriptionTooLong');
  });

  it('規則代碼、名稱、任務代碼、偏移天數與觸發欄位', () => {
    const d = valid();
    d.key = 'A';
    d.name = {};
    d.tasks = [
      { code: '1x', title: { 'zh-TW': 'a' }, assigneeFieldId: 1, dueOffsetDays: 4000 },
      { code: '1x', title: { 'zh-TW': 'b' }, assigneeFieldId: 1, dueOffsetDays: 0 },
    ];
    d.trigger = { on: 'onFieldBecomes', fieldId: 4 };
    const errs = validateDraft(d, fields, LIMITS);
    expect(errs).toEqual(expect.arrayContaining([
      'taskRules.errors.keyBad', 'taskRules.errors.nameRequired', 'taskRules.errors.taskCodeBad', 'taskRules.errors.dueOffsetRange',
      'taskRules.errors.triggerField',
    ]));
    d.tasks[0]!.code = 'ok';
    d.tasks[1]!.code = 'ok';
    d.trigger = { on: 'onFieldBecomes', fieldId: 5, value: 'opt_9' };
    const errs2 = validateDraft(d, fields, LIMITS);
    expect(errs2).toContain('taskRules.errors.taskCodeDup');
    expect(errs2).toContain('taskRules.errors.triggerValue');
    d.trigger = { on: 'onFieldBecomes', fieldId: 6, value: true };
    expect(validateDraft(d, fields, LIMITS)).not.toContain('taskRules.errors.triggerValue');
  });

  it('既有規則超過新上限時，不增加就不報錯', () => {
    const task = (i: number) => ({ code: `t${i}`, title: { 'zh-TW': `任務${i}` }, assigneeFieldId: 1, dueOffsetDays: 0 });
    const d = valid();
    d.tasks = [task(1), task(2), task(3)];
    expect(validateDraft(d, fields, { maxTasks: 2, originalTaskCount: 3 })).not.toContain('taskRules.errors.tasksCount');
    d.tasks = [task(1), task(2), task(3), task(4)];
    expect(validateDraft(d, fields, { maxTasks: 2, originalTaskCount: 3 })).toContain('taskRules.errors.tasksCount');
    d.tasks = [task(1), task(2), task(3)];
    expect(validateDraft(d, fields, { maxTasks: 2, originalTaskCount: 0 })).toContain('taskRules.errors.tasksCount');
  });

  it('任務數 1 到 20', () => {
    const d = valid();
    d.tasks = [];
    expect(validateDraft(d, fields, LIMITS)).toContain('taskRules.errors.tasksCount');
  });

  it('toTaskRuleBody 清掉未填的條件與空說明，只送該有的觸發欄位', () => {
    const d = valid();
    d.when = { all: [{ field: 0, op: 'eq' }] };
    d.trigger = { on: 'manual', fieldId: 5, value: 'opt_7' };
    d.tasks[0]!.description = { 'zh-TW': ' ' };
    d.tasks[0]!.dueFieldId = null;
    d.tasks[0]!.title = { 'zh-TW': ' 回電 ', th: '' };
    const body = toTaskRuleBody(d);
    expect(body.when).toBeUndefined();
    expect(body.trigger).toEqual({ on: 'manual' });
    expect(body.tasks[0]).toEqual({ code: 'call', title: { 'zh-TW': '回電' }, assigneeFieldId: 1, dueOffsetDays: 0 });
  });

  it('fromTaskRule 深拷貝，往返後 body 不變', () => {
    const rule: TaskRule = {
      key: 'r1', name: { 'zh-TW': '規則' }, moduleKey: 'm1', active: false, repeat: 'everyTime',
      trigger: { on: 'onFieldBecomes', fieldId: 6, value: true }, warnings: ['引用的欄位已刪除'],
      when: { all: [{ field: 4, op: 'eq', value: 'x' }] },
      tasks: [{ code: 'a', title: { 'zh-TW': 'T' }, assigneeFieldId: 1, dueFieldId: 2, dueOffsetDays: -2 }],
    };
    const d = fromTaskRule(rule);
    d.tasks[0]!.title['zh-TW'] = 'changed';
    expect(rule.tasks[0]!.title['zh-TW']).toBe('T');
    d.tasks[0]!.title['zh-TW'] = 'T';
    expect(toTaskRuleBody(d)).toEqual({
      key: 'r1', name: { 'zh-TW': '規則' }, active: false, repeat: 'everyTime',
      trigger: { on: 'onFieldBecomes', fieldId: 6, value: true }, when: rule.when, tasks: rule.tasks,
    });
  });
});
