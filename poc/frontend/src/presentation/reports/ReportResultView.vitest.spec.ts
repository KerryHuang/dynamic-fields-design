import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ReportResult } from 'src/domain/dynamic/report-types';
import enUS from 'src/i18n/en-US.json';
import zhTW from 'src/i18n/zh-TW.json';
import ReportResultView from './ReportResultView.vue';
import { dayNumberToDate, formatValue } from './report-format';

vi.mock('./ReportChart.vue', () => ({
  default: { name: 'ReportChart', props: ['viz', 'groups', 'measures'], emits: ['pick'], template: '<i data-testid="chart-stub" />' },
}));

const COLS = [
  { key: 'main:1:', label: '客戶', role: 'group' as const, valueType: 'text' },
  { key: 'm1', label: '訂單金額', role: 'measure' as const, valueType: 'number' },
  { key: 'm2', label: '最晚交期', role: 'measure' as const, valueType: 'dayNumber' },
];
const OK: ReportResult = {
  state: 'ok', message: null, rowMeaning: '一張訂單', columns: COLS, dedupedNote: false, basisDate: '2026-11-05',
  appliedRange: { from: '2026-11-01', to: '2026-11-30', kind: 'thisMonth' },
  groups: [
    { groupKeys: ['a'], groupLabels: ['甲客戶'], values: { m1: 1234.5, m2: 739000 } },
    { groupKeys: ['b'], groupLabels: ['乙客戶'], values: { m1: null, m2: null } },
  ],
  total: { m1: 1234.5, m2: 739000 }, detailRows: null,
};

function mountView(result: ReportResult, viz = 'table', locale = 'zh-TW') {
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  return mount(ReportResultView, { props: { result, viz }, global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] } });
}
const has = (w: ReturnType<typeof mount>, id: string) => w.find(`[data-testid="${id}"]`);

describe('ReportResultView', () => {
  it('shows dedupe note when dedupedNote', () => {
    const w = mountView({ ...OK, dedupedNote: true });
    expect(has(w, 'deduped-note').text()).toBe('主模組的金額等欄位每筆只算一次，明細列相加不等於這個數字');
    expect(has(mountView(OK), 'deduped-note').exists()).toBe(false);
  });

  it('shows applied range and row meaning above the table, with formatted values and total', () => {
    const w = mountView(OK);
    expect(has(w, 'applied-range').text()).toContain('2026-11-01');
    expect(has(w, 'applied-range').text()).toContain('2026-11-30');
    expect(has(w, 'row-meaning').text()).toContain('一張訂單');
    const row = has(w, 'group-row-0').text();
    expect(row).toContain('甲客戶');
    expect(row).toContain('1,234.5');
    expect(row).toContain('2024-');
    expect(has(w, 'group-row-1').text()).toContain('—');
    expect(has(w, 'total-row').text()).toContain('1,234.5');
  });

  it('clicking a group row emits drill with its group keys', async () => {
    const w = mountView(OK);
    await has(w, 'group-row-1').trigger('click');
    expect(w.emitted('drill')).toEqual([[['b']]]);
  });

  it.each([
    ['restricted', '後端說明', '後端說明'],
    ['restricted', null, '無權限檢視'],
    ['fieldMissing', '欄位已不存在：金額', '欄位已不存在：金額'],
    ['moduleDisabled', null, '模組已停用'],
    ['tooMany', null, '資料太多，請縮小條件'],
  ] as const)('state %s shows its message and no numbers', (state, message, text) => {
    const w = mountView({ ...OK, state, message, groups: null, total: null });
    expect(has(w, `state-${state}`).text()).toBe(text);
    expect(has(w, 'group-row-0').exists()).toBe(false);
    expect(has(w, 'total-row').exists()).toBe(false);
  });

  it('empty state shows the empty text with the applied range', () => {
    const w = mountView({ ...OK, state: 'empty', groups: [], total: null });
    expect(has(w, 'state-empty').text()).toBe('這個區間沒有資料');
    expect(has(w, 'applied-range').exists()).toBe(true);
  });

  it('indicator viz shows totals as cards', () => {
    const w = mountView(OK, 'indicator');
    expect(has(w, 'report-indicator').text()).toContain('1,234.5');
    expect(has(w, 'group-row-0').exists()).toBe(false);
  });

  it('bar viz with groups renders the chart; without groups falls back to totals', () => {
    expect(has(mountView(OK, 'bar'), 'chart-stub').exists()).toBe(true);
    const w = mountView({ ...OK, groups: [] }, 'bar');
    expect(has(w, 'chart-stub').exists()).toBe(false);
    expect(has(w, 'report-indicator').exists()).toBe(true);
  });

  it('detail rows follow the field columns and entity title (id 0) is translated', () => {
    const w = mountView({
      ...OK, groups: null, total: null,
      columns: [
        { key: 'main:5:', label: '單號', role: 'field', valueType: 'text' },
        { key: 'main:0:7', label: '客戶．標題', role: 'field', valueType: 'text' },
        { key: 'child:9:', label: '折扣', role: 'field', valueType: 'ratio' },
      ],
      detailRows: [['SO-1', '甲', 0.125]],
    });
    const t = has(w, 'report-detail-table');
    expect(t.text()).toContain('客戶．標題');
    expect(t.text()).toContain('SO-1');
    expect(t.text()).toContain('12.5%');
  });

  it('translates the entity title column label by locale', () => {
    const result = { ...OK, groups: null, total: null, columns: [{ key: 'main:0:7', label: '客戶．標題', role: 'field' as const, valueType: 'text' }], detailRows: [['甲']] };
    expect(has(mountView(result, 'table', 'en-US'), 'report-detail-table').text()).toContain('客戶．Title');
  });
});

describe('report-format', () => {
  it('converts DayNumber (0001-01-01 = 0) to a date', () => {
    expect(dayNumberToDate(0)).toBe('0001-01-01');
    expect(dayNumberToDate(719162)).toBe('1970-01-01');
    expect(formatValue(739000, 'dayNumber', 'zh-TW')).toBe(dayNumberToDate(739000));
  });

  it('formats ratio as percent and keeps sum of percent fields as a plain number', () => {
    expect(formatValue(0.5, 'ratio', 'zh-TW')).toBe('50%');
    expect(formatValue(12.5, 'number', 'zh-TW')).toBe('12.5');
    expect(formatValue(null, 'number', 'zh-TW')).toBe('—');
  });
});
