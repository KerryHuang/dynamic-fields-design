import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { readChartTheme, themedChartOption } from './chart-theme';
import type { ChartTheme } from './chart-theme';

const TOKENS: Record<string, string> = {
  '--t-text': '  #111111 ', '--t-muted': '#222222', '--t-border': '#333333', '--t-panel': '#444444',
  '--t-chart-1': '#a00001', '--t-chart-2': '#a00002', '--t-chart-3': '#a00003',
  '--t-chart-4': '#a00004', '--t-chart-5': '#a00005', '--t-chart-6': '#a00006',
};

const theme: ChartTheme = {
  text: '#111111', muted: '#222222', grid: '#333333', panel: '#444444',
  palette: ['#a00001', '#a00002', '#a00003', '#a00004', '#a00005', '#a00006'],
};

describe('chart-theme', () => {
  beforeEach(() => {
    vi.spyOn(window, 'getComputedStyle').mockReturnValue({ getPropertyValue: (k: string) => TOKENS[k] ?? '' } as CSSStyleDeclaration);
  });
  afterEach(() => vi.restoreAllMocks());

  it('readChartTheme 讀 token 並去掉空白', () => {
    expect(readChartTheme()).toEqual(theme);
  });

  it('color 第一色是 --t-chart-1，共 6 色', () => {
    const out = themedChartOption({}, theme) as { color: string[] };
    expect(out.color).toHaveLength(6);
    expect(out.color[0]).toBe('#a00001');
  });

  it('不帶 theme 參數時讀目前的 token', () => {
    expect((themedChartOption({}) as { color: string[] }).color[0]).toBe('#a00001');
  });

  it('保留 formatter 與資料，物件軸仍是物件、陣列軸仍是陣列', () => {
    const formatter = (v: number) => `${v}%`;
    const base = {
      tooltip: { trigger: 'axis' },
      legend: { type: 'scroll' },
      xAxis: { type: 'category', data: ['a', 'b'] },
      yAxis: [{ type: 'value', axisLabel: { formatter } }, { type: 'value', position: 'right' }],
      series: [{ type: 'bar', data: [1, 2], yAxisIndex: 1, tooltip: { valueFormatter: formatter } }],
    };
    const out = themedChartOption(base, theme) as Record<string, any>;
    expect(Array.isArray(out.xAxis)).toBe(false);
    expect(out.xAxis.data).toEqual(['a', 'b']);
    expect(Array.isArray(out.yAxis)).toBe(true);
    expect(out.yAxis).toHaveLength(2);
    expect(out.yAxis[0].axisLabel.formatter).toBe(formatter);
    expect(out.yAxis[0].axisLabel.color).toBe('#222222');
    expect(out.yAxis[1].position).toBe('right');
    expect(out.xAxis.axisLine.lineStyle.color).toBe('#333333');
    expect(out.yAxis[0].splitLine.lineStyle.color).toBe('#333333');
    expect(out.series[0].data).toEqual([1, 2]);
    expect(out.series[0].yAxisIndex).toBe(1);
    expect(out.series[0].tooltip.valueFormatter).toBe(formatter);
    expect(out.tooltip.trigger).toBe('axis');
    expect(out.legend.type).toBe('scroll');
  });

  it('設定頂層、legend、tooltip 的顏色', () => {
    const out = themedChartOption({ legend: {}, tooltip: {} }, theme) as Record<string, any>;
    expect(out.backgroundColor).toBe('transparent');
    expect(out.textStyle.color).toBe('#111111');
    expect(out.legend.textStyle.color).toBe('#111111');
    expect(out.legend.pageTextStyle.color).toBe('#111111');
    expect(out.legend.pageIconColor).toBe('#111111');
    expect(out.tooltip.backgroundColor).toBe('#444444');
    expect(out.tooltip.borderColor).toBe('#333333');
    expect(out.tooltip.textStyle.color).toBe('#111111');
  });

  it('pie 的扇形邊框用面板色，其他 series 不動', () => {
    const out = themedChartOption({ series: [{ type: 'pie', data: [] }, { type: 'bar' }] }, theme) as { series: Record<string, any>[] };
    expect(out.series[0].itemStyle.borderColor).toBe('#444444');
    expect(out.series[1].itemStyle).toBeUndefined();
  });

  it('沒有軸或 series 的 option 不會憑空長出它們', () => {
    const out = themedChartOption({ tooltip: {} }, theme) as Record<string, unknown>;
    expect(out.xAxis).toBeUndefined();
    expect(out.yAxis).toBeUndefined();
    expect(out.series).toBeUndefined();
  });

  it('不改動傳入的 base', () => {
    const base = { yAxis: [{ type: 'value' }], series: [{ type: 'pie' }], legend: { type: 'scroll' } };
    const snapshot = JSON.parse(JSON.stringify(base));
    themedChartOption(base, theme);
    expect(base).toEqual(snapshot);
  });
});
