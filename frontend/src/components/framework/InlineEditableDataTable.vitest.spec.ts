import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'src/i18n/zh-TW.json';
import InlineEditableDataTable from './InlineEditableDataTable.vue';

type Row = { id: string; name: string };

function mountTable(readonly = false) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(InlineEditableDataTable, {
    props: {
      columns: [{ name: 'name', label: '品名' }, { name: 'qty', label: '數量' }],
      rows: [{ id: 'a', name: '螺絲' }, { id: 'b', name: '螺帽' }] as Row[],
      rowKey: (r: unknown) => (r as Row).id, // mount() does not infer the generic T
      readonly,
    },
    slots: {
      'cell-name': ({ row, index }: { row: unknown; index: number }) => h('span', { class: 'cell' }, `${index}:${(row as Row).name}`),
      'row-actions': () => h('button', { class: 'act' }, 'x'),
    },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

describe('InlineEditableDataTable', () => {
  it('renders columns and cell slots', () => {
    const w = mountTable();
    expect(w.findAll('th').map((th) => th.text()).slice(0, 2)).toEqual(['品名', '數量']);
    expect(w.findAll('.cell').map((c) => c.text())).toEqual(['0:螺絲', '1:螺帽']);
    expect(w.findAll('.act')).toHaveLength(2);
    expect(w.find('[data-testid="add-row"]').exists()).toBe(true);
    w.find('[data-testid="add-row"]').trigger('click');
    expect(w.emitted('add')).toHaveLength(1);
  });

  it('add button hidden when readonly', () => {
    expect(mountTable(true).find('[data-testid="add-row"]').exists()).toBe(false);
  });
});
