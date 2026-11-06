<script setup lang="ts">
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ReportColumn, ReportGroupResult } from 'src/domain/dynamic/report-types';
import { themedChartOption } from 'src/presentation/theme/chart-theme';
import { useThemeStore } from 'src/stores/use-theme-store';
import { formatValue, groupName } from './report-format';

echarts.use([BarChart, LineChart, PieChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer]);

const props = withDefaults(defineProps<{
  viz: 'line' | 'bar' | 'donut'; groups: ReportGroupResult[]; measures: ReportColumn[]; locale: string;
  /** 長條圖的累計占比（百分比，對齊 groups），畫成折線掛在右側 0–100% 的軸。 */
  cumulative?: (number | null)[] | null; cumulativeLabel?: string; height?: string;
  /** 儀表板卡片用：圖表不高，縮小 echarts 預設的上下各 60px 邊界，圖例放底部。 */
  compact?: boolean;
}>(), { cumulative: null, cumulativeLabel: '', height: '360px', compact: false });
const emit = defineEmits<{ pick: [index: number] }>();
const theme = useThemeStore();
const el = ref<HTMLElement>();
let chart: echarts.ECharts | undefined;
let observer: ResizeObserver | undefined;

const fmt = (m: ReportColumn) => (v: unknown) => formatValue(v as number | null, m.valueType, props.locale);
// 比例、百分比、日期、其他數值的尺度不同，各用一條 y 軸；刻度與 tooltip 都用表格同一套格式
const axisKind = (m: ReportColumn) => (m.valueType === 'ratio' || m.valueType === 'percent' || m.valueType === 'dayNumber' ? m.valueType : 'other');

function option() {
  const legendPlace = props.compact ? { bottom: 0 } : {};
  const names = props.groups.map(groupName);
  if (props.viz === 'donut') {
    const m = props.measures[0];
    return {
      tooltip: { trigger: 'item', ...(m ? { valueFormatter: fmt(m) } : {}) },
      legend: { type: 'scroll', ...legendPlace },
      series: [{ type: 'pie', radius: ['45%', '70%'], ...(props.compact ? { center: ['50%', '42%'], radius: ['40%', '62%'] } : {}), data: props.groups.map((g, i) => ({ name: names[i], value: m ? g.values[m.key] : null })) }],
    };
  }
  const kinds = [...new Set(props.measures.map(axisKind))];
  const cumulative = props.viz === 'bar' ? props.cumulative : null;
  const percent = (v: unknown) => `${(v as number).toLocaleString(props.locale, { maximumFractionDigits: 1 })}%`;
  return {
    tooltip: { trigger: 'axis' },
    legend: { type: 'scroll', ...legendPlace },
    grid: props.compact ? { containLabel: true, top: 12, right: 8, bottom: 36, left: 8 } : { containLabel: true },
    xAxis: { type: 'category', data: names },
    yAxis: [...kinds.map((k, i) => ({
      type: 'value', position: i === 0 ? 'left' : 'right', offset: i > 1 ? 60 * (i - 1) : 0,
      // 日期的天數從 0 起算會得到西元 1 年的刻度，只有日期軸改成依資料範圍
      ...(k === 'dayNumber' ? { scale: true } : {}),
      axisLabel: { formatter: fmt(props.measures.find((m) => axisKind(m) === k)!) },
    })), ...(cumulative ? [{ type: 'value', position: 'right', min: 0, max: 100, offset: kinds.length > 1 ? 60 * (kinds.length - 1) : 0, axisLabel: { formatter: percent } }] : [])],
    series: [
      ...props.measures.map((m) => ({
        name: m.label, type: props.viz === 'line' ? 'line' : 'bar', data: props.groups.map((g) => g.values[m.key]),
        yAxisIndex: kinds.indexOf(axisKind(m)), tooltip: { valueFormatter: fmt(m) },
      })),
      ...(cumulative ? [{ name: props.cumulativeLabel, type: 'line', data: cumulative, yAxisIndex: kinds.length, tooltip: { valueFormatter: percent } }] : []),
    ],
  };
}

function render() {
  if (!el.value) return;
  chart ??= echarts.init(el.value);
  chart.setOption(themedChartOption(option()), true);
  chart.off('click');
  chart.on('click', (p) => { if (typeof p.dataIndex === 'number') emit('pick', p.dataIndex); });
}

onMounted(() => {
  render();
  if (el.value && typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(() => chart?.resize());
    observer.observe(el.value);
  }
});
watch(() => [props.viz, props.groups, props.measures, props.cumulative, props.compact], render, { deep: true });
// 換主題或列印切換時只重新套色，不重新查詢；sync 讓 beforeprint 當下就重畫
watch(() => theme.appliedKey, render, { flush: 'sync' });
onBeforeUnmount(() => { observer?.disconnect(); chart?.dispose(); });
</script>

<template>
  <div ref="el" :style="{ height, width: '100%' }" data-testid="report-chart" />
</template>
