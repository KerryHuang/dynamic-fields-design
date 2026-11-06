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

const props = defineProps<{ viz: 'line' | 'bar' | 'donut'; groups: ReportGroupResult[]; measures: ReportColumn[]; locale: string }>();
const emit = defineEmits<{ pick: [index: number] }>();
const theme = useThemeStore();
const el = ref<HTMLElement>();
let chart: echarts.ECharts | undefined;
let observer: ResizeObserver | undefined;

const fmt = (m: ReportColumn) => (v: unknown) => formatValue(v as number | null, m.valueType, props.locale);
// 比例、百分比、日期、其他數值的尺度不同，各用一條 y 軸；刻度與 tooltip 都用表格同一套格式
const axisKind = (m: ReportColumn) => (m.valueType === 'ratio' || m.valueType === 'percent' || m.valueType === 'dayNumber' ? m.valueType : 'other');

function option() {
  const names = props.groups.map(groupName);
  if (props.viz === 'donut') {
    const m = props.measures[0];
    return {
      tooltip: { trigger: 'item', ...(m ? { valueFormatter: fmt(m) } : {}) },
      legend: { type: 'scroll' },
      series: [{ type: 'pie', radius: ['45%', '70%'], data: props.groups.map((g, i) => ({ name: names[i], value: m ? g.values[m.key] : null })) }],
    };
  }
  const kinds = [...new Set(props.measures.map(axisKind))];
  return {
    tooltip: { trigger: 'axis' },
    legend: { type: 'scroll' },
    grid: { containLabel: true },
    xAxis: { type: 'category', data: names },
    yAxis: kinds.map((k, i) => ({
      type: 'value', position: i === 0 ? 'left' : 'right', offset: i > 1 ? 60 * (i - 1) : 0,
      // 日期的天數從 0 起算會得到西元 1 年的刻度，只有日期軸改成依資料範圍
      ...(k === 'dayNumber' ? { scale: true } : {}),
      axisLabel: { formatter: fmt(props.measures.find((m) => axisKind(m) === k)!) },
    })),
    series: props.measures.map((m) => ({
      name: m.label, type: props.viz === 'line' ? 'line' : 'bar', data: props.groups.map((g) => g.values[m.key]),
      yAxisIndex: kinds.indexOf(axisKind(m)), tooltip: { valueFormatter: fmt(m) },
    })),
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
watch(() => [props.viz, props.groups, props.measures], render, { deep: true });
// 換主題或列印切換時只重新套色，不重新查詢；sync 讓 beforeprint 當下就重畫
watch(() => theme.appliedKey, render, { flush: 'sync' });
onBeforeUnmount(() => { observer?.disconnect(); chart?.dispose(); });
</script>

<template>
  <div ref="el" style="height: 360px; width: 100%" data-testid="report-chart" />
</template>
