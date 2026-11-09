import { flushPromises, mount } from '@vue/test-utils';
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { DesignerField, FieldType, ModuleDetail } from 'src/domain/dynamic/types';
import enUS from 'src/i18n/en-US.json';
import th from 'src/i18n/th.json';
import vi18n from 'src/i18n/vi.json';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import FieldEditorDialog from './FieldEditorDialog.vue';

const field = (type: FieldType): DesignerField => ({
  id: 5, key: 'f_5', type, label: { 'zh-TW': '欄位' }, blockId: 1, sequence: 1, status: 'Active', options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});

function mountDialog(props: Record<string, unknown>, queryClient = new QueryClient()) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(FieldEditorDialog, {
    props: { modelValue: true, moduleKey: 'm', blockId: 1, ...props },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient }]] },
  });
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);

describe('FieldEditorDialog', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { document.body.innerHTML = ''; });

  it('keeps isFilterable/isSortable on edit save', async () => {
    const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
    const f = field('text');
    f.config.isFilterable = true;
    f.config.isSortable = true;
    const w = mountDialog({ field: f });
    await flushPromises();
    const save = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!;
    save.click();
    await flushPromises();
    expect(spy).toHaveBeenCalledOnce();
    expect(spy.mock.calls[0][1].config).toMatchObject({ isFilterable: true, isSortable: true });
    spy.mockRestore();
    w.unmount();
  });

  const toggle = (id: string) => q(id) as HTMLElement;
  const isDisabled = (id: string) => toggle(id).getAttribute('aria-disabled') === 'true';

  it('textarea disables both toggles', async () => {
    const w = mountDialog({ field: field('textarea') });
    await flushPromises();
    expect(isDisabled('filterable-toggle')).toBe(true);
    expect(isDisabled('sortable-toggle')).toBe(true);
    expect(document.body.textContent).toContain('此欄位型別不支援篩選');
    expect(document.body.textContent).toContain('此欄位型別不支援排序');
    w.unmount();
  });

  it('multienum disables sortable only', async () => {
    const w = mountDialog({ field: field('multienum') });
    await flushPromises();
    expect(isDisabled('filterable-toggle')).toBe(false);
    expect(isDisabled('sortable-toggle')).toBe(true);
    expect(document.body.textContent).not.toContain('此欄位型別不支援篩選');
    expect(document.body.textContent).toContain('此欄位型別不支援排序');
    w.unmount();
  });

  it('save sends isFilterable/isSortable in config', async () => {
    const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
    const w = mountDialog({ field: field('text') });
    await flushPromises();
    toggle('filterable-toggle').click();
    toggle('sortable-toggle').click();
    await flushPromises();
    const save = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!;
    save.click();
    await flushPromises();
    expect(spy.mock.calls[0][1].config).toMatchObject({ isFilterable: true, isSortable: true });
    spy.mockRestore();
    w.unmount();
  });

  it('new textarea defaults to full width; other types do not', async () => {
    const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
    const w = mountDialog({});
    await flushPromises();
    const isOn = () => toggle('fullwidth-toggle').getAttribute('aria-checked') === 'true';
    expect(isOn()).toBe(false);
    await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'textarea');
    await flushPromises();
    expect(isOn()).toBe(true);
    await w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', { 'zh-TW': '說明' });
    await flushPromises();
    [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
    await flushPromises();
    expect(spy.mock.calls[0]![1].config).toMatchObject({ fullWidth: true });
    spy.mockRestore();
    w.unmount();
  });

  it('edit keeps an explicit fullWidth=true on a text field', async () => {
    const f = field('text');
    f.config.fullWidth = true;
    const w = mountDialog({ field: f });
    await flushPromises();
    expect(toggle('fullwidth-toggle').getAttribute('aria-checked')).toBe('true');
    w.unmount();
  });

  it('edit resolves an unset fullWidth from the type and lets it be switched off', async () => {
    const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
    const f = field('textarea');
    const w = mountDialog({ field: f });
    await flushPromises();
    expect(toggle('fullwidth-toggle').getAttribute('aria-checked')).toBe('true');
    toggle('fullwidth-toggle').click();
    await flushPromises();
    [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
    await flushPromises();
    expect(spy.mock.calls[0]![1].config).toMatchObject({ fullWidth: false });
    spy.mockRestore();
    w.unmount();
  });

  it('type selector is locked in edit mode', async () => {
    const w = mountDialog({ field: field('text') });
    await flushPromises();
    expect(q('type-select')!.querySelector('.q-field--readonly') !== null).toBe(true);
    expect(document.body.textContent).toContain('欄位型別建立後不能更改');
    w.unmount();

    const c = mountDialog({});
    await flushPromises();
    expect(q('type-select')!.querySelector('.q-field--readonly') !== null).toBe(false);
    expect(document.body.textContent).not.toContain('欄位型別建立後不能更改');
    c.unmount();
  });

  it('shows scale input only for decimal currency percent', async () => {
    const shown: Record<string, boolean> = {};
    for (const type of ['text', 'integer', 'decimal', 'currency', 'percent', 'enum'] as FieldType[]) {
      const w = mountDialog({ field: field(type) });
      await flushPromises();
      shown[type] = q('scale-input') !== null;
      w.unmount();
      document.body.innerHTML = '';
    }
    expect(shown).toEqual({ text: false, integer: false, decimal: true, currency: true, percent: true, enum: false });
  });

  it('shows max length only for text and textarea, option editor only for enum types in edit mode', async () => {
    const seen: Record<string, [boolean, boolean]> = {};
    for (const type of ['text', 'textarea', 'decimal', 'enum', 'multienum'] as FieldType[]) {
      const w = mountDialog({ field: field(type) });
      await flushPromises();
      seen[type] = [q('maxlength-input') !== null, q('option-editor') !== null];
      w.unmount();
      document.body.innerHTML = '';
    }
    expect(seen).toEqual({
      text: [true, false], textarea: [true, false], decimal: [false, false], enum: [false, true], multienum: [false, true],
    });
    const c = mountDialog({});
    await flushPromises();
    expect(q('option-editor')).toBeNull();
    c.unmount();
  });

  it('disables the default value for enum types in add mode and shows the hint', async () => {
    const w = mountDialog({});
    await flushPromises();
    await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'enum');
    await flushPromises();
    expect(q('default-value')!.textContent).toContain('加完選項後再設定預設值');
    expect(q('default-value')!.querySelector('input, .q-field')).toBeNull();
    w.unmount();
  });

  it('clearing scale omits it from the payload instead of sending an empty string', async () => {
    const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
    const f = field('decimal');
    f.config.scale = 3;
    const w = mountDialog({ field: f });
    await flushPromises();
    const input = q('scale-input')!.querySelector('input')!;
    input.value = '';
    input.dispatchEvent(new Event('input'));
    await flushPromises();
    const save = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!;
    save.click();
    await flushPromises();
    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.mock.calls[0]![1].config).not.toHaveProperty('scale');
    spy.mockRestore();
    w.unmount();
  });

  describe('computed types', () => {
    const detail = {
      id: 1, key: 'm', programCode: 'P', name: {}, status: 'Draft', version: 1,
      blocks: [{
        id: 1, label: {}, sequence: 1, collapsed: false, columns: 2,
        fields: [
          { ...field('currency'), id: 7, label: { 'zh-TW': '單價' } },
          { ...field('text'), id: 8, label: { 'zh-TW': '備註' } },
          { ...field('date'), id: 9, label: { 'zh-TW': '日期欄' } },
        ],
      }],
    } as ModuleDetail;
    beforeEach(() => { vi.spyOn(dynamicApi, 'getModule').mockResolvedValue(detail); });
    afterEach(() => { vi.restoreAllMocks(); });

    const insertSelect = (w: ReturnType<typeof mountDialog>) =>
      w.findAllComponents({ name: 'QSelect' }).find((c) => c.props('label') === '插入欄位')!;
    const textarea = () => q('formula-input')!.querySelector('textarea')!;

    it('formula shows expression, insert-field and result type; hides required/readOnly/default/maxLength', async () => {
      const w = mountDialog({ field: field('formula') });
      await flushPromises();
      expect(q('formula-input')).not.toBeNull();
      expect(q('insert-field')).not.toBeNull();
      expect(q('result-type')).not.toBeNull();
      expect(q('required-toggle')).toBeNull();
      expect(q('readonly-toggle')).toBeNull();
      expect(q('default-value')).toBeNull();
      expect(q('maxlength-input')).toBeNull();
      expect(q('autonumber-format')).toBeNull();
      expect(document.body.textContent).toContain('DATEDIFF(a, b)');
      expect(document.body.textContent).toContain('{欄位編號}');
      w.unmount();
    });

    it('help texts keep literal braces in every locale', () => {
      const messages = { 'zh-TW': zhTW, 'en-US': enUS, th, vi: vi18n };
      const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages });
      const tokens = { 'zh-TW': '{欄位編號}', 'en-US': '{fieldId}', th: '{fieldId}', vi: '{fieldId}' } as const;
      for (const loc of Object.keys(messages) as (keyof typeof tokens)[]) {
        const t = (k: string) => i18n.global.t(k, {}, { locale: loc });
        expect(t('designer.formula.hint'), loc).toContain(tokens[loc]);
        for (const tok of ['{yyyy}', '{yy}', '{MM}', '{yyyyMM}', '{0000}']) expect(t('designer.autonumber.hint'), loc).toContain(tok);
      }
    });

    it('scale input is only for number/currency formulas', async () => {
      const f = field('formula');
      f.config.resultType = 'date';
      const w = mountDialog({ field: f });
      await flushPromises();
      expect(q('scale-input')).toBeNull();
      w.unmount();
      document.body.innerHTML = '';
      const g = field('formula');
      g.config.resultType = 'currency';
      const w2 = mountDialog({ field: g });
      await flushPromises();
      expect(q('scale-input')).not.toBeNull();
      w2.unmount();
    });

    it('inserting a field puts {id} at the cursor', async () => {
      const f = field('formula');
      f.config.formula = 'ROUND(, 2)';
      f.config.resultType = 'number';
      const w = mountDialog({ field: f });
      await flushPromises();
      const ta = textarea();
      ta.setSelectionRange(6, 6);
      await insertSelect(w).vm.$emit('update:modelValue', 7);
      await flushPromises();
      expect(textarea().value).toBe('ROUND({7}, 2)');
      w.unmount();
    });

    it('insert-field 挑完後輸入框清空，再開啟列出全部可引用欄位', async () => {
      const f = field('formula');
      f.config.formula = 'ROUND(, 2)';
      const w = mountDialog({ field: f });
      await flushPromises();
      const sel = insertSelect(w);
      const input = sel.find('input').element as HTMLInputElement;
      input.focus();
      const vm = sel.vm as unknown as { toggleOption(o: unknown): void; showPopup(): void };
      vm.toggleOption((sel.props('options') as unknown[])[0]);
      await flushPromises();
      expect(input.value).toBe('');
      vm.showPopup();
      await new Promise((r) => setTimeout(r, 20));
      await flushPromises();
      expect(sel.props('options')).toHaveLength(2);
      w.unmount();
    });

    it('lists only referable fields, not text', async () => {
      const w = mountDialog({ field: field('formula') });
      await flushPromises();
      const opts = (insertSelect(w).props('options') as { value: number }[]).map((o) => o.value);
      expect(opts).toEqual([7, 9]);
      w.unmount();
    });

    it('formula save sends formula and resultType, without required/readOnly/default', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const f = field('formula');
      f.config.formula = '{7} * 2';
      f.config.resultType = 'currency';
      const w = mountDialog({ field: f });
      await flushPromises();
      [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(cfg).toMatchObject({ formula: '{7} * 2', resultType: 'currency', required: false, readOnly: false });
      expect(cfg).not.toHaveProperty('format');
      expect(cfg).not.toHaveProperty('defaultValue');
      w.unmount();
    });

    it('autonumber shows format and a live example using today and serial 1', async () => {
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date(2026, 10, 3, 12));
      const f = field('autonumber');
      f.config.format = 'REP-{yyyyMM}-{0000}';
      const w = mountDialog({ field: f });
      await flushPromises();
      expect(q('autonumber-format')).not.toBeNull();
      expect(q('autonumber-example')!.textContent).toContain('REP-202611-0001');
      expect(q('autonumber-format')!.textContent).toContain('{yyyyMM}');
      expect(q('autonumber-format')!.textContent).toContain('{0000}');
      expect(q('formula-input')).toBeNull();
      expect(q('required-toggle')).toBeNull();
      vi.useRealTimers();
      w.unmount();
    });

    it('autonumber save sends format', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const f = field('autonumber');
      f.config.format = 'A-{000}';
      const w = mountDialog({ field: f });
      await flushPromises();
      [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(cfg).toMatchObject({ format: 'A-{000}' });
      expect(cfg).not.toHaveProperty('formula');
      w.unmount();
    });

    it('shows Config.Formula under the expression, Config.ResultType under the select, Config.Format under the format', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockRejectedValue({
        errors: { 'Config.Formula': ['公式有循環引用'], 'Config.ResultType': ['公式不正確：結果型別與設定不符'] },
      });
      const w = mountDialog({ field: field('formula') });
      await flushPromises();
      [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
      await flushPromises();
      expect(q('formula-input')!.textContent).toContain('公式有循環引用');
      expect(q('result-type')!.textContent).toContain('結果型別與設定不符');
      spy.mockRestore();
      w.unmount();
      document.body.innerHTML = '';

      vi.spyOn(dynamicApi, 'updateField').mockRejectedValue({ errors: { 'Config.Format': ['自動編號格式不正確'] } });
      const w2 = mountDialog({ field: field('autonumber') });
      await flushPromises();
      [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
      await flushPromises();
      expect(q('autonumber-format')!.textContent).toContain('自動編號格式不正確');
      w2.unmount();
    });
  });
  describe('reference type', () => {
    const modules = [
      { id: 1, key: 'cust', programCode: 'P1', name: { 'zh-TW': '客戶模組' }, status: 'Active', version: 1, kind: 'standalone' },
      { id: 2, key: 'ext', programCode: 'P2', name: { 'zh-TW': '擴充模組' }, status: 'Active', version: 1, kind: 'extension', hostEntityKey: 'test.entity' },
    ];
    const entities = [{ key: 'test.entity', name: { 'zh-TW': '測試實體' } }];
    beforeEach(() => {
      vi.spyOn(dynamicApi, 'getModules').mockResolvedValue(modules as never);
      vi.spyOn(dynamicApi, 'entities').mockResolvedValue(entities);
    });
    afterEach(() => { vi.restoreAllMocks(); });

    const refField = (target = { kind: 'module', key: 'cust' }, onDelete = 'restrict') => {
      const f = field('reference');
      f.config.refTarget = target as never;
      f.config.onDelete = onDelete as never;
      return f;
    };
    const sel = (w: ReturnType<typeof mountDialog>, testid: string) =>
      w.findAllComponents({ name: 'QSelect' }).find((c) => (c.vm.$el as HTMLElement).closest(`[data-testid="${testid}"]`))!;
    const keysOf = (c: { props(n: string): unknown }) => (c.props('options') as { value: string }[]).map((o) => o.value);

    it('reference type shows target kind, target select and onDelete', async () => {
      const w = mountDialog({});
      await flushPromises();
      expect(q('ref-kind')).toBeNull();
      await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'reference');
      await flushPromises();
      expect(q('ref-kind')).not.toBeNull();
      expect(q('ref-target')).not.toBeNull();
      expect(q('ref-ondelete')).not.toBeNull();
      expect(document.body.textContent).toContain('目標種類');
      w.unmount();
    });

    it('target select lists standalone modules for module kind and entities for entity kind', async () => {
      const w = mountDialog({});
      await flushPromises();
      await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'reference');
      await flushPromises();
      expect(keysOf(sel(w, 'ref-target'))).toEqual(['cust']);
      await sel(w, 'ref-kind').vm.$emit('update:modelValue', 'entity');
      await flushPromises();
      expect(keysOf(sel(w, 'ref-target'))).toEqual(['test.entity']);
      w.unmount();
    });

    it('onDelete hidden for entity target', async () => {
      const w = mountDialog({});
      await flushPromises();
      await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'reference');
      await flushPromises();
      await sel(w, 'ref-kind').vm.$emit('update:modelValue', 'entity');
      await flushPromises();
      expect(q('ref-ondelete')).toBeNull();
      w.unmount();
    });

    it('editing existing reference field disables target', async () => {
      const w = mountDialog({ field: refField() });
      await flushPromises();
      expect(sel(w, 'ref-kind').props('readonly')).toBe(true);
      expect(sel(w, 'ref-target').props('readonly')).toBe(true);
      w.unmount();
    });

    it('default value and sortable hidden for reference', async () => {
      const w = mountDialog({ field: refField() });
      await flushPromises();
      expect(q('default-value')).toBeNull();
      expect(q('sortable-toggle')).toBeNull();
      expect(q('maxlength-input')).toBeNull();
      expect(q('scale-input')).toBeNull();
      w.unmount();
    });

    it('submits config.refTarget and config.onDelete', async () => {
      const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
      const w = mountDialog({});
      await flushPromises();
      await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'reference');
      await flushPromises();
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'cust');
      await sel(w, 'ref-ondelete').vm.$emit('update:modelValue', 'cascade');
      await w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', { 'zh-TW': '客戶' });
      await flushPromises();
      [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(cfg).toMatchObject({ refTarget: { kind: 'module', key: 'cust' }, onDelete: 'cascade' });
      expect(cfg).not.toHaveProperty('defaultValue');
      expect(cfg.isSortable).toBe(false);
      w.unmount();
    });

    const save = () => [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
    const setType = async (w: ReturnType<typeof mountDialog>, type: string) => {
      await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', type);
      await flushPromises();
    };
    const setLabel = (w: ReturnType<typeof mountDialog>) =>
      w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', { 'zh-TW': '欄' });

    it('switching type away from reference drops refTarget and onDelete from the payload', async () => {
      const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'reference');
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'cust');
      await sel(w, 'ref-ondelete').vm.$emit('update:modelValue', 'cascade');
      await setType(w, 'text');
      await setLabel(w);
      await flushPromises();
      save();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(spy.mock.calls[0]![1].type).toBe('text');
      expect(cfg).not.toHaveProperty('refTarget');
      expect(cfg).not.toHaveProperty('onDelete');
      w.unmount();
    });

    it('switching target kind to entity sends the entity target without onDelete', async () => {
      const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'reference');
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'cust');
      await sel(w, 'ref-ondelete').vm.$emit('update:modelValue', 'cascade');
      await sel(w, 'ref-kind').vm.$emit('update:modelValue', 'entity');
      await flushPromises();
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'test.entity');
      await setLabel(w);
      await flushPromises();
      save();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(cfg.refTarget).toEqual({ kind: 'entity', key: 'test.entity' });
      expect(cfg).not.toHaveProperty('onDelete');
      w.unmount();
    });

    it('editing an entity-target reference keeps refTarget in the payload', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const w = mountDialog({ field: refField({ kind: 'entity', key: 'test.entity' }) });
      await flushPromises();
      save();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(cfg.refTarget).toEqual({ kind: 'entity', key: 'test.entity' });
      expect(cfg).not.toHaveProperty('onDelete');
      w.unmount();
    });

    it('editing a module-target reference sends the original refTarget and the new onDelete', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const w = mountDialog({ field: refField({ kind: 'module', key: 'cust' }, 'restrict') });
      await flushPromises();
      await sel(w, 'ref-ondelete').vm.$emit('update:modelValue', 'setNull');
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config).toMatchObject({ refTarget: { kind: 'module', key: 'cust' }, onDelete: 'setNull' });
      w.unmount();
    });

    it('entity target omits onDelete and shows Config.RefTarget under the target', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockRejectedValue({ errors: { 'Config.RefTarget': ['參照目標不正確'] } });
      const w = mountDialog({ field: refField({ kind: 'entity', key: 'test.entity' }) });
      await flushPromises();
      [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config).not.toHaveProperty('onDelete');
      expect(q('ref-target')!.textContent).toContain('參照目標不正確');
      w.unmount();
    });
  });

  describe('phase 6: detail, scope, rollup', () => {
    const ZH = (text: string) => ({ 'zh-TW': text });
    const mk = (id: number, type: FieldType, label: string, config: Partial<DesignerField['config']> = {}): DesignerField =>
      ({ ...field(type), id, key: `f_${id}`, label: ZH(label), config: { ...field(type).config, ...config } });
    const moduleOf = (key: string, fields: DesignerField[], extra: Record<string, unknown> = {}) => ({
      id: 1, key, programCode: 'P', name: ZH(key), status: 'Draft', version: 1,
      blocks: [{ id: 1, label: {}, sequence: 1, collapsed: false, columns: 2, fields }], ...extra,
    }) as unknown as ModuleDetail;
    const sourceFields = [
      mk(31, 'integer', '數量'), mk(32, 'currency', '金額'), mk(33, 'text', '備註'), mk(34, 'date', '日期'),
      mk(35, 'rollup', '子彙總', { resultType: 'number' }),
      mk(36, 'formula', '含彙總公式', { formula: '{35} * 2', resultType: 'number' }),
      mk(37, 'formula', '間接公式', { formula: '{36} + 1', resultType: 'number' }),
      mk(38, 'formula', '普通公式', { formula: '{31} * 2', resultType: 'number' }),
    ];
    const sources = [{ fieldId: 21, fieldLabel: ZH('訂單'), moduleKey: 'line', moduleName: ZH('明細'), relation: 'detail' as const }];
    let headerModule: ModuleDetail;
    beforeEach(() => {
      headerModule = moduleOf('m', [mk(5, 'text', '本欄')]);
      vi.spyOn(dynamicApi, 'getModule').mockImplementation(async (k) => (k === 'line' ? moduleOf('line', sourceFields) : headerModule));
      vi.spyOn(dynamicApi, 'rollupSources').mockResolvedValue(sources);
      vi.spyOn(dynamicApi, 'getModules').mockResolvedValue([
        { id: 1, key: 'cust', programCode: 'P1', name: ZH('客戶'), status: 'Active', version: 1, kind: 'standalone' },
      ] as never);
      vi.spyOn(dynamicApi, 'entities').mockResolvedValue([]);
    });
    afterEach(() => { vi.restoreAllMocks(); });

    const sel = (w: ReturnType<typeof mountDialog>, testid: string) =>
      w.findAllComponents({ name: 'QSelect' }).find((c) => (c.vm.$el as HTMLElement).closest(`[data-testid="${testid}"]`))!;
    const optionValues = (c: { props(n: string): unknown }) => (c.props('options') as { value: unknown }[]).map((o) => o.value);
    const save = () => [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('儲存'))!.click();
    const setType = async (w: ReturnType<typeof mountDialog>, type: string) => {
      await w.findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', type);
      await flushPromises();
    };
    const setLabel = (w: ReturnType<typeof mountDialog>) =>
      w.findComponent({ name: 'I18nTextInput' }).vm.$emit('update:modelValue', ZH('欄'));
    const refField = (config: Partial<DesignerField['config']> = {}) =>
      mk(9, 'reference', '參照', { refTarget: { kind: 'module', key: 'cust' }, onDelete: 'restrict', ...config });

    it('reference module target shows relation option in create mode', async () => {
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'reference');
      expect(q('ref-relation')).not.toBeNull();
      expect(optionValues(sel(w, 'ref-relation'))).toEqual(['lookup', 'detail']);
      await sel(w, 'ref-kind').vm.$emit('update:modelValue', 'entity');
      await flushPromises();
      expect(q('ref-relation')).toBeNull();
      w.unmount();
    });

    it('detail relation hides onDelete and locks required', async () => {
      const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'reference');
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'cust');
      await sel(w, 'ref-relation').vm.$emit('update:modelValue', 'detail');
      await flushPromises();
      expect(q('ref-ondelete')).toBeNull();
      expect(q('required-toggle')!.getAttribute('aria-disabled')).toBe('true');
      expect(q('required-toggle')!.getAttribute('aria-checked')).toBe('true');
      await setLabel(w);
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config).toMatchObject({ relation: 'detail', required: true, onDelete: 'cascade' });
      w.unmount();
    });

    it('lookup relation sends no relation', async () => {
      const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'reference');
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'cust');
      await setLabel(w);
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config).not.toHaveProperty('relation');
      w.unmount();
    });

    it('relation readonly in edit mode', async () => {
      const w = mountDialog({ field: refField({ relation: 'detail', required: true, onDelete: 'cascade' }) });
      await flushPromises();
      expect(sel(w, 'ref-relation').props('readonly')).toBe(true);
      expect(sel(w, 'ref-relation').props('modelValue')).toBe('detail');
      w.unmount();
    });

    it('autonumber in detail module shows scope parent; other modules do not', async () => {
      headerModule = moduleOf('m', [], { detailOf: 'order' });
      const f = mk(8, 'autonumber', '項次', { format: '{000}' });
      const w = mountDialog({ field: f });
      await flushPromises();
      expect(q('autonumber-scope')).not.toBeNull();
      w.unmount();
      document.body.innerHTML = '';

      headerModule = moduleOf('m', []);
      const w2 = mountDialog({ field: f });
      await flushPromises();
      expect(q('autonumber-scope')).toBeNull();
      w2.unmount();
    });

    it('autonumber scope parent is sent when on, and omitted when off', async () => {
      headerModule = moduleOf('m', [], { detailOf: 'order' });
      const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 8 } as never);
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'autonumber');
      await setLabel(w);
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config).not.toHaveProperty('scope');
      (q('autonumber-scope')!.querySelector('.q-toggle') as HTMLElement).click();
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[1]![1].config).toMatchObject({ scope: 'parent' });
      w.unmount();
    });

    it('autonumber scope is locked in edit mode', async () => {
      headerModule = moduleOf('m', [], { detailOf: 'order' });
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const f = mk(8, 'autonumber', '項次', { format: '{000}', scope: 'parent' });
      const w = mountDialog({ field: f });
      await flushPromises();
      const toggleEl = q('autonumber-scope')!.querySelector('.q-toggle') as HTMLElement;
      expect(toggleEl.getAttribute('aria-disabled')).toBe('true');
      toggleEl.click();
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config).toMatchObject({ scope: 'parent' });
      w.unmount();
    });

    it('reference targets exclude detail modules', async () => {
      vi.spyOn(dynamicApi, 'getModules').mockResolvedValue([
        { id: 1, key: 'cust', programCode: 'P1', name: ZH('客戶'), status: 'Active', version: 1, kind: 'standalone' },
        { id: 2, key: 'line', programCode: 'P2', name: ZH('明細'), status: 'Active', version: 1, kind: 'standalone', detailOf: 'order' },
      ] as never);
      const w = mountDialog({});
      await flushPromises();
      await setType(w, 'reference');
      expect(optionValues(sel(w, 'ref-target'))).toEqual(['cust']);
      w.unmount();
    });

    it('adding a field invalidates the menu (a new detail field drops the module from the sidebar)', async () => {
      vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
      const qc = new QueryClient();
      const invalidate = vi.spyOn(qc, 'invalidateQueries');
      const w = mountDialog({}, qc);
      await flushPromises();
      await setType(w, 'reference');
      await sel(w, 'ref-target').vm.$emit('update:modelValue', 'cust');
      await sel(w, 'ref-relation').vm.$emit('update:modelValue', 'detail');
      await setLabel(w);
      await flushPromises();
      save();
      await flushPromises();
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['menu'] });
      w.unmount();
    });

    it('rollup lists sources from rollupSources', async () => {
      const w = mountDialog({ field: mk(40, 'rollup', '合計') });
      await flushPromises();
      expect(dynamicApi.rollupSources).toHaveBeenCalledWith('m');
      expect(q('rollup-settings')).not.toBeNull();
      expect(optionValues(sel(w, 'rollup-source'))).toEqual([21]);
      expect(q('required-toggle')).toBeNull();
      expect(q('default-value')).toBeNull();
      w.unmount();
    });

    it('does not load rollup sources for other types', async () => {
      const w = mountDialog({ field: field('text') });
      await flushPromises();
      expect(dynamicApi.rollupSources).not.toHaveBeenCalled();
      w.unmount();
    });

    const rollupField = (config: Partial<NonNullable<DesignerField['config']['rollup']>> = {}) =>
      mk(40, 'rollup', '合計', { rollup: { sourceFieldId: 21, function: 'sum', aggregateFieldId: 32, ...config } });

    it('rollup count hides aggregate field', async () => {
      const w = mountDialog({ field: rollupField() });
      await flushPromises();
      expect(q('rollup-aggregate')).not.toBeNull();
      await sel(w, 'rollup-function').vm.$emit('update:modelValue', 'count');
      await flushPromises();
      expect(q('rollup-aggregate')).toBeNull();
      w.unmount();
    });

    it('rollup aggregate options are numeric or date fields of source module', async () => {
      const w = mountDialog({ field: rollupField() });
      await flushPromises();
      expect(optionValues(sel(w, 'rollup-aggregate'))).toEqual([31, 32, 34, 38]);
      w.unmount();
    });

    it('rollup condition uses ConditionEditor with source fields', async () => {
      const w = mountDialog({ field: rollupField() });
      await flushPromises();
      const editor = w.findComponent({ name: 'ConditionEditor' });
      expect(editor.exists()).toBe(true);
      expect((editor.props('fields') as { id: number }[]).map((f) => f.id)).toEqual([31, 32, 33, 34, 38]);
      w.unmount();
    });

    it('rollup payload shape', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const condition = { all: [{ field: 31, op: 'gt', value: 0 }] };
      const w = mountDialog({ field: rollupField({ condition: condition as never }) });
      await flushPromises();
      save();
      await flushPromises();
      const cfg = spy.mock.calls[0]![1].config;
      expect(cfg.rollup).toEqual({ sourceFieldId: 21, function: 'sum', aggregateFieldId: 32, condition });
      expect(cfg.required).toBe(false);
      expect(cfg).not.toHaveProperty('defaultValue');
      expect(cfg).not.toHaveProperty('resultType');
      expect(cfg).not.toHaveProperty('scale');
      w.unmount();
    });

    it('rollup count payload has no aggregateFieldId; an incomplete condition is pruned', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const w = mountDialog({ field: rollupField({ function: 'count', condition: { all: [{ field: 0, op: 'eq' }] } as never }) });
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config.rollup).toEqual({ sourceFieldId: 21, function: 'count' });
      w.unmount();
    });

    it('changing the source clears aggregate field and condition', async () => {
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const w = mountDialog({ field: rollupField({ condition: { all: [{ field: 31, op: 'gt', value: 1 }] } as never }) });
      await flushPromises();
      await sel(w, 'rollup-source').vm.$emit('update:modelValue', 21);
      await flushPromises();
      expect(optionValues(sel(w, 'rollup-aggregate'))).toContain(32);
      await sel(w, 'rollup-source').vm.$emit('update:modelValue', null);
      await sel(w, 'rollup-source').vm.$emit('update:modelValue', 21);
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config.rollup).toEqual({ sourceFieldId: 21, function: 'sum' });
      w.unmount();
    });

    it('reopening with another rollup keeps its aggregate field and condition', async () => {
      vi.mocked(dynamicApi.rollupSources).mockResolvedValue([
        ...sources, { fieldId: 22, fieldLabel: ZH('維修'), moduleKey: 'line', moduleName: ZH('明細'), relation: 'lookup' as const },
      ]);
      const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
      const w = mountDialog({ field: rollupField() });
      await flushPromises();
      const condition = { all: [{ field: 31, op: 'gt', value: 5 }] };
      const b = mk(41, 'rollup', '工時', { rollup: { sourceFieldId: 22, function: 'max', aggregateFieldId: 31, condition: condition as never } });
      await w.setProps({ field: b });
      await flushPromises();
      save();
      await flushPromises();
      expect(spy.mock.calls[0]![1].config.rollup).toEqual({ sourceFieldId: 22, function: 'max', aggregateFieldId: 31, condition });
      w.unmount();
    });

    it('formula insert list includes rollup fields', async () => {
      headerModule = moduleOf('m', [mk(15, 'integer', '數量'), mk(6, 'rollup', '合計', { resultType: 'currency' }), mk(7, 'text', '備註')]);
      const w = mountDialog({ field: field('formula') });
      await flushPromises();
      const insert = w.findAllComponents({ name: 'QSelect' }).find((c) => c.props('label') === '插入欄位')!;
      expect(optionValues(insert)).toEqual([15, 6]);
      w.unmount();
    });

    it('server Config.Rollup / Config.Relation / Config.Scope errors shown inline', async () => {
      vi.spyOn(dynamicApi, 'updateField').mockRejectedValue({
        errors: { 'Config.Rollup': ['彙總設定不正確：必須指定彙總欄位'] },
      });
      const w = mountDialog({ field: rollupField() });
      await flushPromises();
      save();
      await flushPromises();
      expect(q('rollup-settings')!.textContent).toContain('彙總設定不正確：必須指定彙總欄位');
      w.unmount();
      document.body.innerHTML = '';
      vi.restoreAllMocks();
      vi.spyOn(dynamicApi, 'getModule').mockImplementation(async () => moduleOf('m', [], { detailOf: 'order' }));
      vi.spyOn(dynamicApi, 'getModules').mockResolvedValue([] as never);
      vi.spyOn(dynamicApi, 'entities').mockResolvedValue([]);

      vi.spyOn(dynamicApi, 'updateField').mockRejectedValue({
        errors: { 'Config.Scope': ['只有明細模組的自動編號可以在表頭內編號'] },
      });
      const w2 = mountDialog({ field: mk(8, 'autonumber', '項次', { format: '{000}', scope: 'parent' }) });
      await flushPromises();
      save();
      await flushPromises();
      expect(q('autonumber-scope')!.textContent).toContain('只有明細模組的自動編號可以在表頭內編號');
      w2.unmount();
      document.body.innerHTML = '';
      vi.restoreAllMocks();
      vi.spyOn(dynamicApi, 'getModule').mockResolvedValue(moduleOf('m', []));
      vi.spyOn(dynamicApi, 'getModules').mockResolvedValue([] as never);
      vi.spyOn(dynamicApi, 'entities').mockResolvedValue([]);

      vi.spyOn(dynamicApi, 'updateField').mockRejectedValue({
        errors: { 'Config.Relation': ['明細設定建立後不能更改'] },
      });
      const w3 = mountDialog({ field: refField({ relation: 'detail' }) });
      await flushPromises();
      save();
      await flushPromises();
      expect(q('ref-relation')!.textContent).toContain('明細設定建立後不能更改');
      w3.unmount();
    });

    it('has the new texts in every locale', () => {
      for (const [loc, m] of Object.entries({ zhTW, enUS, th, vi: vi18n })) {
        const i18n = createI18n({ legacy: false, locale: 'x', messages: { x: m } });
        for (const k of ['designer.types.rollup', 'designer.reference.relation', 'designer.reference.relationLookup',
          'designer.reference.relationDetail', 'designer.autonumber.scopeParent', 'designer.rollup.source', 'designer.rollup.function',
          'designer.rollup.aggregate', 'designer.rollup.condition', 'designer.rollup.functions.sum', 'designer.detailOf',
          'designer.permissions.detailInherited']) expect(i18n.global.te(k), `${loc} ${k}`).toBe(true);
      }
    });

    describe('phase 8: default today and parent default', () => {
      const todayToggle = () => q('default-today')?.querySelector('.q-toggle') as HTMLElement | null;
      const dvInput = () => q('default-value')!.querySelector('input') as HTMLInputElement;
      const locked = () => dvInput().readOnly;

      it('date shows the default-today toggle; turning it on disables the default value and is sent', async () => {
        const spy = vi.spyOn(dynamicApi, 'addField').mockResolvedValue({ id: 9 } as never);
        const w = mountDialog({});
        await flushPromises();
        await setType(w, 'date');
        await setLabel(w);
        expect(todayToggle()).not.toBeNull();
        expect(locked()).toBe(false);
        todayToggle()!.click();
        await flushPromises();
        expect(locked()).toBe(true);
        save();
        await flushPromises();
        expect(spy.mock.calls[0]![1].config).toMatchObject({ defaultToday: true });
        w.unmount();
      });

      it('non-date types have no default-today toggle', async () => {
        const w = mountDialog({});
        await flushPromises();
        await setType(w, 'text');
        expect(q('default-today')).toBeNull();
        w.unmount();
      });

      it('parent default lists active header fields of the same type, only in a detail module', async () => {
        headerModule = moduleOf('m', [], { detailOf: 'order' });
        const orderModule = moduleOf('order', [
          mk(61, 'date', '訂單日'), mk(62, 'date', '已停用日', {}), mk(63, 'text', '備註'), mk(64, 'enum', '類別'),
        ]);
        orderModule.blocks[0]!.fields[1]!.status = 'Inactive';
        vi.spyOn(dynamicApi, 'getModule').mockImplementation(async (k) => (k === 'order' ? orderModule : headerModule));
        const w = mountDialog({});
        await flushPromises();
        await setType(w, 'date');
        await flushPromises();
        expect(q('parent-default')).not.toBeNull();
        expect(optionValues(sel(w, 'parent-default'))).toEqual([61]);
        w.unmount();
        document.body.innerHTML = '';

        headerModule = moduleOf('m', []);
        const w2 = mountDialog({});
        await flushPromises();
        await setType(w2, 'date');
        expect(q('parent-default')).toBeNull();
        w2.unmount();
      });

      it('editing only the label keeps defaultToday and parentDefaultFieldId', async () => {
        const spy = vi.spyOn(dynamicApi, 'updateField').mockResolvedValue(undefined);
        headerModule = moduleOf('m', [], { detailOf: 'order' });
        vi.spyOn(dynamicApi, 'getModule').mockImplementation(async (k) => (k === 'order' ? moduleOf('order', [mk(61, 'date', '訂單日')]) : headerModule));
        const today = mountDialog({ field: mk(8, 'date', '日', { defaultToday: true }) });
        await flushPromises();
        setLabel(today);
        save();
        await flushPromises();
        expect(spy.mock.calls[0]![1].config).toMatchObject({ defaultToday: true });
        today.unmount();
        document.body.innerHTML = '';

        const parent = mountDialog({ field: mk(8, 'date', '日', { parentDefaultFieldId: 61 }) });
        await flushPromises();
        setLabel(parent);
        save();
        await flushPromises();
        expect(spy.mock.calls[1]![1].config).toMatchObject({ parentDefaultFieldId: 61 });
        expect(spy.mock.calls[1]![1].config).not.toHaveProperty('defaultToday');
        parent.unmount();
      });

      it('picking a parent default disables the other default controls', async () => {
        headerModule = moduleOf('m', [], { detailOf: 'order' });
        vi.spyOn(dynamicApi, 'getModule').mockImplementation(async (k) => (k === 'order' ? moduleOf('order', [mk(61, 'date', '訂單日')]) : headerModule));
        const w = mountDialog({ field: mk(8, 'date', '日', { parentDefaultFieldId: 61 }) });
        await flushPromises();
        expect(locked()).toBe(true);
        expect(todayToggle()!.getAttribute('aria-disabled')).toBe('true');
        w.unmount();
      });
    });
  });
});
