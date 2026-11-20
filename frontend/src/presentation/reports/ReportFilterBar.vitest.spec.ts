import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { NO_RUN_FILTER, type ReportRunFilter } from 'src/domain/dynamic/report-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
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

  describe('期間 N', () => {
    const nInput = () => mounted!.findAllComponents(quasar.QInput).find((c) => c.element.contains(q('filter-range-n')))!;
    const nNative = () => q('filter-range-n') as HTMLInputElement;
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());
    const mountN = () => mountBar({ modelValue: { ...NO_RUN_FILTER, range: { kind: 'lastDays', n: 30 } } });

    it('typing only edits the box; the filter applies after 500ms without further typing, once', async () => {
      await mountN();
      nInput().vm.$emit('update:modelValue', '7');
      nInput().vm.$emit('update:modelValue', '75');
      vi.advanceTimersByTime(499);
      expect(emitted()).toBeUndefined();
      vi.advanceTimersByTime(2);
      expect(emitted()).toHaveLength(1);
      expect(emitted()[0]![0]).toMatchObject({ range: { kind: 'lastDays', n: 75 } });
    });

    it('blur and Enter apply immediately', async () => {
      await mountN();
      nInput().vm.$emit('update:modelValue', '12');
      nInput().vm.$emit('blur');
      expect(emitted()[0]![0]).toMatchObject({ range: { kind: 'lastDays', n: 12 } });
      nInput().vm.$emit('update:modelValue', '8');
      nNative().dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));
      expect(emitted()[1]![0]).toMatchObject({ range: { kind: 'lastDays', n: 8 } });
      vi.advanceTimersByTime(1000);
      expect(emitted()).toHaveLength(2);
    });

    it('out-of-range values are clamped to 1..3650 instead of being sent', async () => {
      await mountN();
      expect(nNative().max).toBe('3650');
      nInput().vm.$emit('update:modelValue', '99999');
      nInput().vm.$emit('blur');
      expect(emitted()[0]![0]).toMatchObject({ range: { kind: 'lastDays', n: 3650 } });
      nInput().vm.$emit('update:modelValue', '0');
      nInput().vm.$emit('blur');
      expect(emitted()[1]![0]).toMatchObject({ range: { kind: 'lastDays', n: 1 } });
    });

    it('an empty or fractional N reverts to the applied value and sends nothing', async () => {
      await mountN();
      nInput().vm.$emit('update:modelValue', '');
      nInput().vm.$emit('blur');
      nInput().vm.$emit('update:modelValue', '2.5');
      vi.advanceTimersByTime(600);
      expect(emitted()).toBeUndefined();
      expect(nNative().value).toBe('30');
    });

    it('a pending edit is dropped when the bar goes away', async () => {
      await mountN();
      nInput().vm.$emit('update:modelValue', '9');
      mounted!.unmount();
      vi.advanceTimersByTime(1000);
      expect(emitted()).toBeUndefined();
    });
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
    // tick-strategy 不是 strict 時 q-tree 不畫勾選框（單元測試直接 emit 事件抓不到），每個節點都要有自己的勾選框
    expect(q('dept-filter-tree')!.querySelectorAll('.q-checkbox')).toHaveLength(3);
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
