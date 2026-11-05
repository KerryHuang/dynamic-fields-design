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

const ratio = { key: 'r', label: '達成率', role: 'measure' as const, valueType: 'ratio' };
const day = { key: 'd', label: '最晚交期', role: 'measure' as const, valueType: 'dayNumber' };
const mixedGroups = [{ groupKeys: ['a'], groupLabels: ['甲'], values: { m1: 10, r: 0.07, d: 739000 } }];

describe('ReportChart', () => {
  beforeEach(() => vi.clearAllMocks());

  it('ratio keeps the raw value, formats the tooltip as percent and sits on its own axis next to amounts', () => {
    mount(ReportChart, { props: { viz: 'bar', groups: mixedGroups, measures: [...measures, ratio], locale: 'zh-TW' } });
    const opt = chart.setOption.mock.calls[0]![0];
    expect(opt.series[1].data).toEqual([0.07]);
    expect(opt.series[1].tooltip.valueFormatter(0.07)).toBe('7%');
    expect(opt.series[0].tooltip.valueFormatter(10)).toBe('10');
    expect(opt.yAxis).toHaveLength(2);
    expect(opt.series.map((s: { yAxisIndex: number }) => s.yAxisIndex)).toEqual([0, 1]);
    expect(opt.yAxis[1].axisLabel.formatter(0.5)).toBe('50%');
  });

  it('dayNumber measures show dates on the tooltip and the axis', () => {
    mount(ReportChart, { props: { viz: 'line', groups: mixedGroups, measures: [day], locale: 'zh-TW' } });
    const opt = chart.setOption.mock.calls[0]![0];
    expect(opt.series[0].tooltip.valueFormatter(739000)).toBe('2024-04-25');
    expect(opt.yAxis[0].axisLabel.formatter(739000)).toBe('2024-04-25');
  });

  it('donut tooltip formats the first measure', () => {
    mount(ReportChart, { props: { viz: 'donut', groups: mixedGroups, measures: [ratio], locale: 'zh-TW' } });
    expect(chart.setOption.mock.calls[0]![0].tooltip.valueFormatter(0.07)).toBe('7%');
  });

  it('resizes with its container and stops observing on unmount', () => {
    const observe = vi.fn(); const disconnect = vi.fn();
    vi.stubGlobal('ResizeObserver', class { constructor(public cb: () => void) {} observe = observe; disconnect = disconnect; });
    const w = mount(ReportChart, { props: { viz: 'bar', groups, measures, locale: 'zh-TW' } });
    expect(observe).toHaveBeenCalled();
    w.unmount();
    expect(disconnect).toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it('bar builds a category axis and one series per measure', () => {
    mount(ReportChart, { props: { viz: 'bar', groups, measures, locale: 'zh-TW' } });
    const opt = chart.setOption.mock.calls[0]![0];
    expect(opt.xAxis.data).toEqual(['甲', '乙']);
    expect(opt.series).toMatchObject([{ name: '金額', type: 'bar', data: [10, 20], yAxisIndex: 0 }]);
    expect(opt.yAxis).toHaveLength(1);
  });

  it('donut uses the first measure as pie slices and a clicked slice emits pick', () => {
    const w = mount(ReportChart, { props: { viz: 'donut', groups, measures, locale: 'zh-TW' } });
    const opt = chart.setOption.mock.calls[0]![0];
    expect(opt.series[0].type).toBe('pie');
    expect(opt.series[0].data).toEqual([{ name: '甲', value: 10 }, { name: '乙', value: 20 }]);
    const handler = chart.on.mock.calls.find((c) => c[0] === 'click')![1];
    handler({ dataIndex: 1 });
    expect(w.emitted('pick')).toEqual([[1]]);
  });

  it('disposes the chart on unmount', () => {
    mount(ReportChart, { props: { viz: 'line', groups, measures, locale: 'zh-TW' } }).unmount();
    expect(chart.dispose).toHaveBeenCalled();
  });
});
