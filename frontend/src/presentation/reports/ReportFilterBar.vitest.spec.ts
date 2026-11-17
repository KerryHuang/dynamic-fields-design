import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { NO_RUN_FILTER, type ReportRunFilter } from 'src/domain/dynamic/report-types';
import zhTW from 'src/i18n/zh-TW.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ReportFilterBar from './ReportFilterBar.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getDepartments: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const DEPT = (id: number, code: string, name: string, parentId: number | null, active = true) =>
  ({ id, code, name: { 'zh-TW': name }, parentId, managerUserId: null, sortOrder: id, active, memberUserIds: [] });
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;

async function mountBar(props: { modelValue?: ReportRunFilter; showDepartment?: boolean; rangeAvailable?: boolean } = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(ReportFilterBar, {
    props: { modelValue: { ...NO_RUN_FILTER }, showDepartment: true, rangeAvailable: true, ...props },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
}
const emitted = () => mounted!.emitted('update:modelValue') as ReportRunFilter[][];
const select = () => mounted!.findAllComponents(quasar.QSelect).find((c) => c.element.contains(q('filter-range')))!;

describe('ReportFilterBar', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getDepartments.mockResolvedValue([DEPT(1, 'hq', '總部', null), DEPT(2, 'sales', '業務部', 1, false), DEPT(3, 'sales1', '業務一課', 2)]);
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('picking a period emits a range, last-N kinds carry a default N, "as set" clears it', async () => {
    await mountBar();
    select().vm.$emit('update:modelValue', 'lastMonth');
    expect(emitted()[0]![0]).toMatchObject({ range: { kind: 'lastMonth', n: null } });
    select().vm.$emit('update:modelValue', 'lastDays');
    expect(emitted()[1]![0]).toMatchObject({ range: { kind: 'lastDays', n: 30 } });
    await mounted!.setProps({ modelValue: { ...NO_RUN_FILTER, range: { kind: 'lastDays', n: 30 } } });
    select().vm.$emit('update:modelValue', '');
    expect(emitted()[2]![0]).toMatchObject({ range: null });
  });

  it('N applies only when it is a positive integer', async () => {
    await mountBar({ modelValue: { ...NO_RUN_FILTER, range: { kind: 'lastDays', n: 30 } } });
    const input = mounted!.findAllComponents(quasar.QInput).find((c) => c.element.contains(q('filter-range-n')))!;
    input.vm.$emit('update:modelValue', '0');
    input.vm.$emit('update:modelValue', '2.5');
    input.vm.$emit('update:modelValue', '');
    expect(emitted()).toBeUndefined();
    input.vm.$emit('update:modelValue', '7');
    expect(emitted()[0]![0]).toMatchObject({ range: { kind: 'lastDays', n: 7 } });
  });

  it('is disabled with a hint when the report has no primary date field', async () => {
    await mountBar({ rangeAvailable: false });
    expect(select().props('disable')).toBe(true);
    expect(q('filter-range-na')!.textContent).toContain('沒有主要日期欄位');
  });

  it('the department button only shows for task sources', async () => {
    await mountBar({ showDepartment: false });
    expect(q('filter-department')).toBeNull();
    mounted!.unmount();
    await mountBar({ showDepartment: true });
    expect(q('filter-department')!.textContent).toContain('全部部門');
  });

  it('department dialog lists the whole tree including inactive departments and applies ticked ids with include-sub on by default', async () => {
    await mountBar();
    q('filter-department')!.click();
    await flushPromises();
    const tree = q('dept-filter-tree')!.textContent!;
    expect(tree).toContain('總部');
    expect(tree).toContain('業務部（已停用）');
    expect(tree).toContain('業務一課');
    const treeComp = mounted!.findComponent(quasar.QTree);
    treeComp.vm.$emit('update:ticked', [2]);
    await flushPromises();
    q('dept-filter-apply')!.click();
    await flushPromises();
    expect(emitted()[0]![0]).toEqual({ range: null, departmentIds: [2], includeSubDepartments: true });
  });

  it('include-sub can be switched off, and the summary shows the selection and the mode', async () => {
    await mountBar();
    q('filter-department')!.click();
    await flushPromises();
    mounted!.findComponent(quasar.QTree).vm.$emit('update:ticked', [1, 3]);
    mounted!.findAllComponents(quasar.QCheckbox).find((c) => c.element.contains(q('dept-filter-sub')))!.vm.$emit('update:modelValue', false);
    await flushPromises();
    q('dept-filter-apply')!.click();
    await flushPromises();
    expect(emitted()[0]![0]).toEqual({ range: null, departmentIds: [1, 3], includeSubDepartments: false });
    await mounted!.setProps({ modelValue: emitted()[0]![0]! });
    expect(q('filter-department')!.textContent).toContain('已選 2 個部門');
    expect(q('filter-department-note')!.textContent).toContain('僅所選部門');
  });

  it('cancel leaves the filter untouched and clear-all resets both', async () => {
    await mountBar({ modelValue: { range: { kind: 'thisMonth', n: null }, departmentIds: [1], includeSubDepartments: false } });
    q('filter-department')!.click();
    await flushPromises();
    q('dept-filter-cancel')!.click();
    await flushPromises();
    expect(emitted()).toBeUndefined();
    q('filter-clear')!.click();
    expect(emitted()[0]![0]).toEqual({ range: null, departmentIds: [], includeSubDepartments: true });
  });
});
