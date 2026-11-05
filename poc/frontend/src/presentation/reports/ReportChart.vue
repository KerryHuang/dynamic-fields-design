<script setup lang="ts">
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { ReportColumn, ReportGroupResult } from 'src/domain/dynamic/report-types';
import { groupName } from './report-format';

echarts.use([BarChart, LineChart, PieChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer]);

const props = defineProps<{ viz: 'line' | 'bar' | 'donut'; groups: ReportGroupResult[]; measures: ReportColumn[] }>();
const emit = defineEmits<{ pick: [index: number] }>();
const el = ref<HTMLElement>();
let chart: echarts.ECharts | undefined;

// 比例以百分比數字畫，tooltip 與座標才讀得懂
const num = (g: ReportGroupResult, m: ReportColumn) => {
  const v = g.values[m.key];
  return v !== null && v !== undefined && m.valueType === 'ratio' ? v * 100 : v;
};

function option() {
  const names = props.groups.map(groupName);
  if (props.viz === 'donut') {
    const m = props.measures[0];
    return {
      tooltip: { trigger: 'item' },
      legend: { type: 'scroll' },
      series: [{ type: 'pie', radius: ['45%', '70%'], data: props.groups.map((g, i) => ({ name: names[i], value: m ? num(g, m) : null })) }],
    };
  }
  return {
    tooltip: { trigger: 'axis' },
    legend: { type: 'scroll' },
    grid: { containLabel: true },
    xAxis: { type: 'category', data: names },
    yAxis: { type: 'value' },
    series: props.measures.map((m) => ({
      name: m.label, type: props.viz === 'line' ? 'line' : 'bar', data: props.groups.map((g) => num(g, m)),
    })),
  };
}

function render() {
  if (!el.value) return;
  chart ??= echarts.init(el.value);
  chart.setOption(option(), true);
  chart.off('click');
  chart.on('click', (p) => { if (typeof p.dataIndex === 'number') emit('pick', p.dataIndex); });
}

onMounted(render);
watch(() => [props.viz, props.groups, props.measures], render, { deep: true });
onBeforeUnmount(() => chart?.dispose());
</script>

<template>
  <div ref="el" style="height: 360px; width: 100%" data-testid="report-chart" />
</template>
