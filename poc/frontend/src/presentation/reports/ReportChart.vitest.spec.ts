import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const chart = vi.hoisted(() => ({ setOption: vi.fn(), on: vi.fn(), off: vi.fn(), dispose: vi.fn() }));
vi.mock('echarts/core', () => ({ use: vi.fn(), init: vi.fn(() => chart) }));
vi.mock('echarts/charts', () => ({ BarChart: {}, LineChart: {}, PieChart: {} }));
vi.mock('echarts/components', () => ({ GridComponent: {}, LegendComponent: {}, TooltipComponent: {} }));
vi.mock('echarts/renderers', () => ({ CanvasRenderer: {} }));

import ReportChart from './ReportChart.vue';

const measures = [{ key: 'm1', label: '金額', role: 'measure' as const, valueType: 'number' }];
const groups = [
  { groupKeys: ['a'], groupLabels: ['甲'], values: { m1: 10 } },
  { groupKeys: ['b'], groupLabels: ['乙'], values: { m1: 20 } },
];

describe('ReportChart', () => {
  beforeEach(() => vi.clearAllMocks());

  it('bar builds a category axis and one series per measure', () => {
    mount(ReportChart, { props: { viz: 'bar', groups, measures } });
    const opt = chart.setOption.mock.calls[0]![0];
    expect(opt.xAxis.data).toEqual(['甲', '乙']);
    expect(opt.series).toEqual([{ name: '金額', type: 'bar', data: [10, 20] }]);
  });

  it('donut uses the first measure as pie slices and a clicked slice emits pick', () => {
    const w = mount(ReportChart, { props: { viz: 'donut', groups, measures } });
    const opt = chart.setOption.mock.calls[0]![0];
    expect(opt.series[0].type).toBe('pie');
    expect(opt.series[0].data).toEqual([{ name: '甲', value: 10 }, { name: '乙', value: 20 }]);
    const handler = chart.on.mock.calls.find((c) => c[0] === 'click')![1];
    handler({ dataIndex: 1 });
    expect(w.emitted('pick')).toEqual([[1]]);
  });

  it('disposes the chart on unmount', () => {
    mount(ReportChart, { props: { viz: 'line', groups, measures } }).unmount();
    expect(chart.dispose).toHaveBeenCalled();
  });
});
