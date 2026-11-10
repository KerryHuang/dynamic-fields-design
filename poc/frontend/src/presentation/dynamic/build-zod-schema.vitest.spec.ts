import { describe, expect, it } from 'vitest';
import type { DynamicRecord, FieldType, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import { evaluateRules } from 'src/domain/dynamic/rule-engine';
import { buildZodSchema } from './build-zod-schema';

function field(key: string, type: FieldType, over: Partial<Omit<SchemaField, 'config'>> & { config?: Partial<SchemaField['config']> } = {}): SchemaField {
  const { config, ...rest } = over;
  return {
    id: 1, key, type, label: { 'zh-TW': key }, options: [],
    config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false, ...config },
    ...rest,
  };
}
function schemaOf(...fields: SchemaField[]): ModuleSchema {
  return { key: 'm', programCode: 'P', name: {}, titleFieldKey: fields[0].key, version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true }, blocks: [{ id: 1, label: {}, collapsed: false, columns: 2, fields }] };
}
const msg = (r: { success: boolean; error?: { issues: { message: string }[] } }) => r.error?.issues[0]?.message;

describe('buildZodSchema', () => {
  it('required text rejects whitespace-only', () => {
    const s = buildZodSchema(schemaOf(field('name', 'text', { config: { required: true } })), 'create');
    expect(msg(s.safeParse({ name: '   ' }))).toBe('此欄位必填');
    expect(msg(s.safeParse({ name: null }))).toBe('此欄位必填');
    expect(s.safeParse({ name: ' a ' })).toMatchObject({ success: true, data: { name: 'a' } });
  });

  it('optional text treats empty as null and enforces max length', () => {
    const s = buildZodSchema(schemaOf(field('name', 'text', { config: { maxLength: 3 } })), 'create');
    expect(s.safeParse({ name: '' })).toMatchObject({ success: true, data: { name: null } });
    expect(msg(s.safeParse({ name: 'abcd' }))).toBe('超過 3 個字元');
  });

  it('decimal rejects more decimals than scale', () => {
    const s = buildZodSchema(schemaOf(field('d', 'decimal', { config: { scale: 2 } })), 'create');
    expect(msg(s.safeParse({ d: 1.234 }))).toBe('小數最多 2 位');
    expect(msg(s.safeParse({ d: '3.5' }))).toBe('必須是數字');
    expect(s.safeParse({ d: 1.23 }).success).toBe(true);
  });

  it('integer rejects non-integers', () => {
    const s = buildZodSchema(schemaOf(field('i', 'integer')), 'create');
    expect(msg(s.safeParse({ i: 1.5 }))).toBe('必須是整數');
  });

  it('date rejects non-ISO', () => {
    const s = buildZodSchema(schemaOf(field('d', 'date')), 'create');
    expect(msg(s.safeParse({ d: '2026/11/03' }))).toBe('日期格式必須是 yyyy-MM-dd');
    expect(s.safeParse({ d: '2026-11-03' }).success).toBe(true);
  });

  it('datetime normalizes to +08:00 and time/boolean/email/phone/url follow backend messages', () => {
    const s = buildZodSchema(schemaOf(
      field('dt', 'datetime'), field('t', 'time'), field('b', 'boolean'),
      field('e', 'email'), field('p', 'phone'), field('u', 'url')), 'create');
    expect(s.safeParse({ dt: '2026-11-03T00:00:00Z' })).toMatchObject({ data: { dt: '2026-11-03T08:00:00+08:00' } });
    expect(msg(s.safeParse({ dt: 'nope' }))).toBe('日期時間格式錯誤');
    expect(msg(s.safeParse({ t: '9:00' }))).toBe('時間格式必須是 HH:mm');
    expect(msg(s.safeParse({ b: 'yes' }))).toBe('必須是是或否');
    expect(msg(s.safeParse({ e: 'a@b' }))).toBe('Email 格式錯誤');
    expect(msg(s.safeParse({ p: 'abc' }))).toBe('電話格式錯誤');
    expect(msg(s.safeParse({ p: '0912345678\n' }))).toBe('電話格式錯誤');
    expect(msg(s.safeParse({ u: 'ftp://x.com' }))).toBe('網址必須以 http:// 或 https:// 開頭');
    expect(s.safeParse({ dt: null, t: '09:00', b: false, e: 'a@b.co', p: '+886 (2) 123#4', u: 'https://x.com' }).success).toBe(true);
  });

  it('required boolean only treats null as missing', () => {
    const s = buildZodSchema(schemaOf(field('b', 'boolean', { config: { required: true } })), 'create');
    expect(s.safeParse({ b: false }).success).toBe(true);
    expect(msg(s.safeParse({ b: null }))).toBe('此欄位必填');
  });

  const opts = [
    { value: 'opt_1', label: { 'zh-TW': 'A' }, active: true },
    { value: 'opt_2', label: { 'zh-TW': 'B' }, active: false },
    { value: 'opt_3', label: { 'zh-TW': 'C' }, active: true },
  ];
  const record = (values: Record<string, unknown>): DynamicRecord => ({
    id: 1, moduleKey: 'm', title: '', values, ownerUserId: '', createdUserId: '', createdTime: '', modifiedUserId: '', modifiedTime: '', rowVersion: '', references: {}, hostKey: null,
  });

  it('enum in edit mode accepts existing inactive option but create mode rejects it', () => {
    const schema = schemaOf(field('e', 'enum', { options: opts }));
    expect(msg(buildZodSchema(schema, 'create').safeParse({ e: 'opt_2' }))).toBe('選項不存在或已停用');
    expect(buildZodSchema(schema, 'edit', record({ e: 'opt_2' })).safeParse({ e: 'opt_2' }).success).toBe(true);
    expect(msg(buildZodSchema(schema, 'edit', record({ e: 'opt_1' })).safeParse({ e: 'opt_2' }))).toBe('選項不存在或已停用');
    expect(buildZodSchema(schema, 'create').safeParse({ e: 'opt_3' }).success).toBe(true);
  });

  it('multienum dedups, orders by option order, and allows previously stored inactive values', () => {
    const schema = schemaOf(field('m', 'multienum', { options: opts }));
    expect(buildZodSchema(schema, 'create').safeParse({ m: ['opt_3', 'opt_1', 'opt_3'] })).toMatchObject({ data: { m: ['opt_1', 'opt_3'] } });
    expect(msg(buildZodSchema(schema, 'create').safeParse({ m: ['opt_2'] }))).toBe('選項不存在或已停用');
    expect(buildZodSchema(schema, 'edit', record({ m: ['opt_2'] })).safeParse({ m: ['opt_2', 'opt_1'] }).success).toBe(true);
    const req = schemaOf(field('m', 'multienum', { options: opts, config: { required: true } }));
    expect(msg(buildZodSchema(req, 'create').safeParse({ m: [] }))).toBe('此欄位必填');
  });

  describe('with a RuleState', () => {
    const f1 = field('a', 'text', { id: 1 });
    const f2 = field('b', 'text', { id: 2 });
    const stateOf = (s: ModuleSchema, values: Record<string, unknown>, stored?: Record<string, unknown>) =>
      evaluateRules(s.rules, values, stored, s.blocks.flatMap((b) => b.fields), s.blocks.map((b) => ({ id: b.id, fieldIds: b.fields.map((f) => f.id) })), '2026-11-03');

    it('requires what the RuleState requires, not config.required', () => {
      const s = schemaOf(f1, field('b', 'text', { id: 2, config: { required: true } }));
      s.rules = [{ kind: 'visibility', condition: { field: 1, op: 'eq', value: 'x' }, targets: ['field:2'], options: [] }];
      const hiddenState = stateOf(s, { a: null, b: null });
      expect(buildZodSchema(s, 'create', undefined, hiddenState).safeParse({ a: null, b: null }).success).toBe(true);
      const shownState = stateOf(s, { a: 'x', b: null });
      expect(msg(buildZodSchema(s, 'create', undefined, shownState).safeParse({ a: 'x', b: null }))).toBe('此欄位必填');
    });

    it('does not require a field that is read-only', () => {
      const s = schemaOf(f1, field('b', 'text', { id: 2, config: { required: true, readOnly: true } }));
      const st = stateOf(s, { a: null, b: null });
      expect(buildZodSchema(s, 'create', undefined, st).safeParse({ a: null, b: null }).success).toBe(true);
    });

    it('reports a failed Validation on the first target, or on _record when none qualifies', () => {
      const s = schemaOf(f1, f2);
      s.rules = [{ kind: 'validation', condition: { field: 1, op: 'eq', value: 'ok' }, targets: ['field:2'], options: [], message: { 'zh-TW': '不通過', 'en-US': 'no' } }];
      const st = stateOf(s, { a: 'bad', b: null });
      const r = buildZodSchema(s, 'create', undefined, st, { locale: 'en-US' }).safeParse({ a: 'bad', b: null });
      expect(r.error?.issues).toMatchObject([{ path: ['b'], message: 'no' }]);

      s.rules.unshift({ kind: 'visibility', condition: { field: 1, op: 'eq', value: 'never' }, targets: ['field:2'], options: [] });
      const st2 = stateOf(s, { a: 'bad', b: null });
      const r2 = buildZodSchema(s, 'create', undefined, st2).safeParse({ a: 'bad', b: null });
      expect(r2.error?.issues).toMatchObject([{ path: ['_record'], message: '不通過' }]);
    });

    describe('option range', () => {
      const opt = (id: number) => ({ value: `opt_${id}`, label: {}, active: true });
      const sel = (type: 'enum' | 'multienum', over = {}) => field('c', type, { id: 3, options: [opt(1), opt(2), opt(3)], ...over });
      const mk = (type: 'enum' | 'multienum', over = {}) => {
        const s = schemaOf(f1, sel(type, over));
        s.rules = [{ kind: 'optionFilter', condition: { field: 1, op: 'eq', value: 'go' }, targets: ['field:3'], options: ['opt_1'] }];
        return s;
      };
      const issues = (s: ModuleSchema, cur: Record<string, unknown>, stored?: Record<string, unknown>) =>
        buildZodSchema(s, stored ? 'edit' : 'create', stored ? ({ values: stored } as DynamicRecord) : undefined, stateOf(s, cur, stored),
          { storedRuleState: stored ? stateOf(s, stored, stored) : undefined })
          .safeParse(cur).error?.issues.map((i) => `${i.path.join('.')}:${i.message}`);

      it('en-US 下提示是英文', () => {
        const s = mk('enum');
        const r = buildZodSchema(s, 'create', undefined, stateOf(s, { a: 'go', c: 'opt_2' }), { locale: 'en-US' }).safeParse({ a: 'go', c: 'opt_2' });
        expect(r.error?.issues[0]?.message).toBe('Option is outside the allowed range');
      });

      it('create checks the whole value', () => {
        const s = mk('enum');
        expect(issues(s, { a: 'go', c: 'opt_2' })).toEqual(['c:選項不在可選範圍']);
        expect(issues(s, { a: 'go', c: 'opt_1' })).toBeUndefined();
      });

      it('edit with an unchanged allowed set only checks newly selected options', () => {
        const s = mk('multienum');
        const stored = { a: 'go', c: ['opt_1', 'opt_2'] };
        expect(issues(s, { a: 'go', c: ['opt_1', 'opt_2'] }, stored)).toBeUndefined();
        expect(issues(s, { a: 'go', c: ['opt_1', 'opt_3'] }, stored)).toEqual(['c:選項不在可選範圍']);
      });

      it('edit rechecks the whole value when the allowed set changed', () => {
        const s = mk('enum');
        expect(issues(s, { a: 'go', c: 'opt_2' }, { a: 'x', c: 'opt_2' })).toEqual(['c:選項不在可選範圍']);
      });

      it('skips fields the user cannot write', () => {
        const s = mk('enum', { config: { readOnly: true } });
        expect(issues(s, { a: 'go', c: 'opt_2' })).toBeUndefined();
      });
    });
  });
});

describe('buildZodSchema 提示隨語系', () => {
  const en = { locale: 'en-US' };
  it('en-US 下必填、數字、整數、字數、選項範圍的提示是英文', () => {
    const req = buildZodSchema(schemaOf(field('name', 'text', { config: { required: true, maxLength: 3 } })), 'create', undefined, undefined, en);
    expect(msg(req.safeParse({ name: null }))).toBe('This field is required');
    expect(msg(req.safeParse({ name: 'abcd' }))).toBe('Exceeds 3 characters');
    const num = buildZodSchema(schemaOf(field('d', 'decimal'), field('i', 'integer')), 'create', undefined, undefined, en);
    expect(msg(num.safeParse({ d: '3.5', i: null }))).toBe('Must be a number');
    expect(msg(num.safeParse({ d: null, i: 1.5 }))).toBe('Must be an integer');
    expect(msg(num.safeParse({ d: 1.234, i: null }))).toBe('At most 2 decimal places');
  });

  it('th／vi 的必填提示不是中文也不是英文', () => {
    const s = schemaOf(field('name', 'text', { config: { required: true } }));
    const th = msg(buildZodSchema(s, 'create', undefined, undefined, { locale: 'th' }).safeParse({ name: null }));
    const vi = msg(buildZodSchema(s, 'create', undefined, undefined, { locale: 'vi' }).safeParse({ name: null }));
    expect(th).not.toMatch(/[一-鿿]|This field/);
    expect(vi).toBe('Trường này là bắt buộc');
  });

  it('呼叫端傳入的 t 優先（鍵與參數交給 t 轉）', () => {
    const t = (k: string, p?: Record<string, unknown>) => `${k}|${JSON.stringify(p ?? {})}`;
    const s = buildZodSchema(schemaOf(field('name', 'text', { config: { maxLength: 3 } })), 'create', undefined, undefined, { t });
    expect(msg(s.safeParse({ name: 'abcd' }))).toBe('dynamic.validation.maxLength|{"n":3}');
  });
});
