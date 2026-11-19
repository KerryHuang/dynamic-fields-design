<script setup lang="ts">
import { LineChart } from 'echarts/charts';
import { GridComponent, TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { themedChartOption } from 'src/presentation/theme/chart-theme';
import { useThemeStore } from 'src/stores/use-theme-store';

// 健康檢查趨勢折線（維運設計 §17.3 第 3 項）；echarts 載入方式比照 ReportChart.vue，寬度 100%
echarts.use([LineChart, GridComponent, TooltipComponent, CanvasRenderer]);

const props = defineProps<{ points: { time: number; value: number | null }[]; format: (v: number) => string; label: string }>();
const theme = useThemeStore();
const el = ref<HTMLElement>();
let chart: echarts.ECharts | undefined;
let observer: ResizeObserver | undefined;

function option() {
  return {
    tooltip: { trigger: 'axis', valueFormatter: (v: unknown) => (typeof v === 'number' ? props.format(v) : '—') },
    grid: { containLabel: true, top: 16, right: 12, bottom: 8, left: 8 },
    xAxis: { type: 'time' },
    yAxis: { type: 'value', scale: true, axisLabel: { formatter: (v: number) => props.format(v) } },
    series: [{ name: props.label, type: 'line', showSymbol: false, data: props.points.map((p) => [p.time, p.value]) }],
  };
}
function render() {
  if (!el.value) return;
  chart ??= echarts.init(el.value);
  chart.setOption(themedChartOption(option()), true);
}
onMounted(() => {
  render();
  if (el.value && typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(() => chart?.resize());
    observer.observe(el.value);
  }
});
watch(() => props.points, render, { deep: true });
watch(() => theme.appliedKey, render, { flush: 'sync' });
onBeforeUnmount(() => { observer?.disconnect(); chart?.dispose(); });
</script>

<template>
  <div ref="el" style="height: 200px; width: 100%" data-testid="health-trend-chart" />
</template>
