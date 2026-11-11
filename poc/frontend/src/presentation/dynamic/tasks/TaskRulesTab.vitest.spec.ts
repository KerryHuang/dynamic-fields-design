import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import { FACTORY_ME_THEME } from 'src/domain/theme/theme-fixtures';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { bodyOf, HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import TaskRulesTab from './TaskRulesTab.vue';

const fld = (id: number, type: DesignerField['type'], label: string): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, blockId: 1, sequence: id, status: 'Active', options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: false, isSortable: false },
});
const mod = (extra: Partial<ModuleDetail> = {}): ModuleDetail => ({
  id: 1, key: 'm1', programCode: 'DYN_M1', name: { 'zh-TW': '模組' }, status: 'Active', version: 1,
  blocks: [{
    id: 1, label: { 'zh-TW': '區塊' }, sequence: 1, collapsed: false, columns: 2,
    fields: [fld(1, 'user', '業務'), fld(2, 'user', '經理'), fld(3, 'date', '簽約日'), fld(4, 'datetime', '建立時間'), fld(5, 'text', '備註')],
  }],
  ...extra,
});
const ruleWire = (o: Record<string, unknown> = {}) => ({
  key: 'follow', name: { 'zh-TW': '跟進' }, moduleKey: 'm1', active: true, repeat: 'once', trigger: { on: 'manual' },
  tasks: [{ code: 'call', title: { 'zh-TW': '回電' }, assigneeFieldId: 1, dueOffsetDays: 0 }], warnings: [], ...o,
});

let calls: Call[];
let w: VueWrapper;

async function mountTab(opts: { module?: ModuleDetail; canEdit?: boolean; rules?: unknown[]; saveFails?: boolean; maxRules?: number; maxTasks?: number; effectiveHangs?: boolean } = {}) {
  const pinia = createPinia();
  setActivePinia(pinia);
  useIdentityStore().setSession('admin');
  const { canEdit = true, rules = [ruleWire()] } = opts;
  calls = mockHttp((c) => {
    if (c.url === '/me') return { userId: 'admin', displayName: 'A', isSystemAdmin: false, mustChangePassword: false, groups: [], designer: { read: true, add: true, edit: canEdit, delete: false }, theme: FACTORY_ME_THEME };
    if (c.url === '/modules/m1/task-rules' && c.method === 'get') return rules;
    if (c.url === '/policies/effective') return opts.effectiveHangs ? new HttpFail(500, {}) : { moduleKey: 'm1', values: { 'task.maxRulesPerModule': opts.maxRules ?? 20, 'task.maxTasksPerRule': opts.maxTasks ?? 20 } };
    if (opts.saveFails && c.method !== 'get') return new HttpFail(400, { errors: { 'tasks[0].assigneeFieldId': ['負責人必須是這個模組的人員欄位'] } });
    return ruleWire();
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  w = mount(TaskRulesTab, {
    props: { module: opts.module ?? mod() },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], pinia, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}

const body = () => document.body;
const q = <T extends Element>(sel: string) => body().querySelector<T>(sel)!;
async function type(sel: string, value: string) {
  const el = q<HTMLInputElement>(sel);
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
const select = (label: string) => w.findAllComponents(GenericSelect).find((c) => c.props('label').startsWith(label))!;
async function openAdd() {
  q<HTMLElement>('[data-testid="add-task-rule"]').click();
  await flushPromises();
}

describe('TaskRulesTab', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
  });

  it('列出規則與警告；模組關閉任務時頂端有提示，規則仍可新增', async () => {
    await mountTab({ module: mod({ tasksEnabled: false }), rules: [ruleWire({ warnings: ['引用的欄位已刪除'] })] });
    expect(body().textContent).toContain('跟進');
    expect(q('[data-testid="task-rule-warnings-follow"]').textContent).toContain('引用的欄位已刪除');
    expect(q('[data-testid="task-rules-module-off"]').textContent).toContain('這個模組未啟用任務，規則不會觸發。到上方開啟「啟用任務」。');
    expect(q('[data-testid="add-task-rule"]').getAttribute('disabled')).toBeNull();
    w.unmount();
  });

  it('已有 20 條規則時新增鈕停用並有提示', async () => {
    await mountTab({ rules: Array.from({ length: 20 }, (_, i) => ruleWire({ key: `r${i}` })) });
    expect(q('[data-testid="add-task-rule"]').hasAttribute('disabled')).toBe(true);
    (w.findComponent(quasar.QTooltip).vm as unknown as { show: () => void }).show();
    await flushPromises();
    expect(body().textContent).toContain('每個模組最多 20 條任務規則');
    w.unmount();
  });

  it('上限依模組生效值', async () => {
    await mountTab({ maxRules: 2, rules: [ruleWire({ key: 'a' }), ruleWire({ key: 'b' })] });
    expect(q('[data-testid="add-task-rule"]').hasAttribute('disabled')).toBe(true);
    (w.findComponent(quasar.QTooltip).vm as unknown as { show: () => void }).show();
    await flushPromises();
    expect(body().textContent).toContain('每個模組最多 2 條任務規則');
    w.unmount();
  });

  it('生效值還沒回來時新增鈕停用，載入失敗顯示提示', async () => {
    await mountTab({ effectiveHangs: true, rules: [] });
    expect(q('[data-testid="add-task-rule"]').hasAttribute('disabled')).toBe(true);
    expect(body().textContent).toContain('政策載入失敗');
    w.unmount();
  });

  it('既有規則任務數超過上限：新增任務鈕停用，不增加任務時仍可改標題存檔', async () => {
    const tasks = ['a', 'b', 'c'].map((code) => ({ code, title: { 'zh-TW': code }, assigneeFieldId: 1, dueOffsetDays: 0 }));
    await mountTab({ maxTasks: 2, rules: [ruleWire({ tasks })] });
    q<HTMLElement>('[data-testid="task-rule-edit-follow"]').click();
    await flushPromises();
    expect(q('[data-testid="task-add"]').hasAttribute('disabled')).toBe(true);
    q<HTMLElement>('[data-testid="task-rule-save"]').click();
    await flushPromises();
    expect(calls.find((c) => c.method === 'put')?.url).toBe('/modules/m1/task-rules/follow');
    w.unmount();
  });

  it('新規則的新增任務鈕依生效值上限停用（上限 2：加到第 2 個就停用）', async () => {
    await mountTab({ maxTasks: 2, rules: [] });
    await openAdd();
    expect(q('[data-testid="task-add"]').hasAttribute('disabled')).toBe(false);
    q<HTMLElement>('[data-testid="task-add"]').click();
    await flushPromises();
    expect(q('[data-testid="task-add"]').hasAttribute('disabled')).toBe(true);
    w.unmount();
  });

  it('19 條規則時新增鈕仍可用', async () => {
    await mountTab({ rules: Array.from({ length: 19 }, (_, i) => ruleWire({ key: `r${i}` })) });
    expect(q('[data-testid="add-task-rule"]').hasAttribute('disabled')).toBe(false);
    expect(w.findComponent(quasar.QTooltip).exists()).toBe(false);
    w.unmount();
  });

  it('對話框捲動區不折欄：內容超過 70vh 時直向捲動，不往右溢出', async () => {
    await mountTab({ rules: [] });
    await openAdd();
    const section = q('[data-testid="task-rule-dialog-body"]');
    expect(section.classList.contains('column')).toBe(true);
    expect(section.classList.contains('no-wrap')).toBe(true);
    w.unmount();
  });

  it('啟用任務時沒有提示', async () => {
    await mountTab();
    expect(body().querySelector('[data-testid="task-rules-module-off"]')).toBeNull();
    w.unmount();
  });

  it('非設計器使用者是唯讀', async () => {
    await mountTab({ canEdit: false });
    for (const id of ['add-task-rule', 'task-rule-edit-follow', 'task-rule-delete-follow', 'task-rule-toggle-follow']) {
      expect(q(`[data-testid="${id}"]`).hasAttribute('disabled'), id).toBe(true);
    }
    w.unmount();
  });

  it('負責人選單只列 user 欄位，到期基準只列 date／datetime 欄位', async () => {
    await mountTab({ rules: [] });
    await openAdd();
    expect(select('負責人').props('options').map((o: { label: string }) => o.label)).toEqual(['業務', '經理']);
    expect(select('到期基準').props('options').map((o: { label: string }) => o.label)).toEqual(['簽約日', '建立時間']);
    w.unmount();
  });

  it('選 onCreate 時 everyTime 停用且強制 once', async () => {
    await mountTab({ rules: [] });
    await openAdd();
    expect(q('[data-testid="repeat-everyTime"]').getAttribute('aria-disabled')).toBe('true');
    q<HTMLElement>('[data-testid="trigger-manual"]').click();
    await flushPromises();
    expect(q('[data-testid="repeat-everyTime"]').getAttribute('aria-disabled')).not.toBe('true');
    q<HTMLElement>('[data-testid="repeat-everyTime"]').click();
    await flushPromises();
    expect(q('[data-testid="repeat-everyTime"]').getAttribute('aria-checked')).toBe('true');
    q<HTMLElement>('[data-testid="trigger-onCreate"]').click();
    await flushPromises();
    expect(q('[data-testid="repeat-once"]').getAttribute('aria-checked')).toBe('true');
    w.unmount();
  });

  it('onUpdateMatch 沒條件不能存，不送出請求', async () => {
    await mountTab({ rules: [] });
    await openAdd();
    await type('input[data-testid="task-rule-key"]', 'follow-up');
    await type('[data-testid="i18n-zh-TW"] input', '跟進');
    q<HTMLElement>('[data-testid="trigger-onUpdateMatch"]').click();
    await flushPromises();
    q<HTMLElement>('[data-testid="task-rule-save"]').click();
    await flushPromises();
    expect(q('[data-testid="task-rule-errors"]').textContent).toContain('必須設定條件');
    expect(calls.some((c) => c.method === 'post')).toBe(false);
    w.unmount();
  });

  it('存檔送出 POST /modules/m1/task-rules，body 含 repeat 與多語系 title', async () => {
    await mountTab({ rules: [] });
    await openAdd();
    await type('input[data-testid="task-rule-key"]', 'follow-up');
    await type('[data-testid="i18n-zh-TW"] input', '跟進');
    q<HTMLElement>('[data-testid="trigger-manual"]').click();
    await flushPromises();
    q<HTMLElement>('[data-testid="repeat-everyTime"]').click();
    await flushPromises();
    await type('input[data-testid="task-code-0"]', 'call');
    await type('[data-testid="task-title-0"] [data-testid="i18n-zh-TW"] input', '回電');
    const en = body().querySelectorAll<HTMLInputElement>('[data-testid="task-title-0"] .q-expansion-item input');
    expect(en.length).toBe(3);
    en[0]!.value = 'Call';
    en[0]!.dispatchEvent(new Event('input'));
    select('負責人').vm.$emit('update:modelValue', '2');
    select('到期基準').vm.$emit('update:modelValue', '3');
    await flushPromises();
    await type('input[data-testid="task-offset-0"]', '-2');
    q<HTMLElement>('[data-testid="task-rule-save"]').click();
    await flushPromises();
    const post = calls.find((c) => c.method === 'post');
    expect(post?.url).toBe('/modules/m1/task-rules');
    expect(bodyOf(post)).toEqual({
      key: 'follow-up', name: { 'zh-TW': '跟進' }, active: true, repeat: 'everyTime', trigger: { on: 'manual' },
      tasks: [{ code: 'call', title: { 'zh-TW': '回電', 'en-US': 'Call' }, assigneeFieldId: 2, dueFieldId: 3, dueOffsetDays: -2 }],
    });
    w.unmount();
  });

  it('編輯送 PUT 且規則代碼不能改；伺服器錯誤顯示在對話框', async () => {
    await mountTab({ saveFails: true });
    q<HTMLElement>('[data-testid="task-rule-edit-follow"]').click();
    await flushPromises();
    expect(q<HTMLInputElement>('input[data-testid="task-rule-key"]').disabled).toBe(true);
    q<HTMLElement>('[data-testid="task-rule-save"]').click();
    await flushPromises();
    expect(calls.find((c) => c.method === 'put')?.url).toBe('/modules/m1/task-rules/follow');
    expect(q('[data-testid="task-rule-errors"]').textContent).toContain('負責人必須是這個模組的人員欄位');
    w.unmount();
  });

  it('停用開關送 PUT，active 反轉', async () => {
    await mountTab();
    q<HTMLElement>('[data-testid="task-rule-toggle-follow"]').click();
    await flushPromises();
    const put = calls.find((c) => c.method === 'put');
    expect(put?.url).toBe('/modules/m1/task-rules/follow');
    expect(bodyOf(put)).toMatchObject({ key: 'follow', active: false });
    w.unmount();
  });

  it('刪除要先確認，確認後送 DELETE', async () => {
    await mountTab();
    q<HTMLElement>('[data-testid="task-rule-delete-follow"]').click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'delete')).toBe(false);
    q<HTMLElement>('[data-testid="task-rule-delete-confirm"]').click();
    await flushPromises();
    expect(calls.find((c) => c.method === 'delete')?.url).toBe('/modules/m1/task-rules/follow');
    w.unmount();
  });
});
