import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { DynamicRecord, SchemaDetail, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'src/i18n/zh-TW.json';
import DetailTables from './DetailTables.vue';

const field = (id: number, type: SchemaField['type'], label: string, extra: Partial<SchemaField> = {}): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, ...extra,
});
const product = field(3, 'reference', '產品', { reference: { kind: 'module', key: 'product', name: { 'zh-TW': '產品' }, lightUrl: '' } });
const detail = (name: string, key = 'line'): SchemaDetail => ({
  moduleKey: key, name: { 'zh-TW': name }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_1',
  fields: [field(9, 'reference', '表頭'), field(1, 'text', '品名'), field(2, 'decimal', '數量', { config: { ...field(2, 'decimal', '').config, scale: 2 } }), product],
  rules: [],
});
const row = (id: number, values: Record<string, unknown>, references: DynamicRecord['references'] = {}): DynamicRecord => ({
  id, moduleKey: 'line', title: '', values, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: 'v', references, hostKey: null,
});

async function mountTables(details: SchemaDetail[], rows: Record<string, DynamicRecord[]>) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push('/');
  return mount(DetailTables, {
    props: { details, rows },
    global: { plugins: [[quasar.Quasar, { components: quasar }], router, i18n] },
  });
}

describe('DetailTables', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('renders one table per schema.details with record.details rows', async () => {
    const w = await mountTables([detail('訂單明細'), detail('出貨明細', 'ship')], {
      line: [row(1, { f_1: '螺絲' }), row(2, { f_1: '螺帽' })],
      ship: [row(3, { f_1: '箱' })],
    });
    expect(w.findAll('table')).toHaveLength(2);
    expect(w.text()).toContain('訂單明細');
    expect(w.text()).toContain('出貨明細');
    expect(w.findAll('table')[0]!.findAll('th').map((th) => th.text()).filter(Boolean)).toEqual(['品名', '數量', '產品']);
    expect(w.findAll('table')[0]!.findAll('tbody tr')).toHaveLength(2);
    expect(w.find('[data-testid="add-row"]').exists()).toBe(false);
  });

  it('cells formatted by registry, references by referenceView', async () => {
    const w = await mountTables([detail('訂單明細')], {
      line: [
        row(1, { f_1: '螺絲', f_2: 3 }, { f_3: { state: 'ok', key: '7', title: '大螺絲' } } as never),
        row(2, { f_1: '壞', f_2: 1 }, { f_3: { state: 'missing', key: '8' } } as never),
      ],
    });
    const cells = w.findAll('tbody tr')[0]!.findAll('td').map((td) => td.text());
    expect(cells[0]).toBe('螺絲');
    expect(cells[1]).toBe('3');
    expect(cells[2]).toBe('大螺絲');
    expect(w.find('tbody tr a').attributes('href')).toBe('/dynamic/product/7');
    expect(w.findAll('tbody tr')[1]!.text()).toContain(zhTW.dynamic.reference.missing);
  });

  it('empty shows noData', async () => {
    const w = await mountTables([detail('訂單明細')], {});
    expect(w.findAll('tbody tr')).toHaveLength(1);
    expect(w.text()).toContain(zhTW.dynamic.noData);
  });
});
