/** 圖表要用的顏色，來自 <html> 上目前主題的 --t-* token。 */
export interface ChartTheme { text: string; muted: string; grid: string; panel: string; palette: string[] }

export function readChartTheme(): ChartTheme {
  const style = window.getComputedStyle(document.documentElement);
  const get = (name: string) => style.getPropertyValue(name).trim();
  return {
    text: get('--t-text'), muted: get('--t-muted'), grid: get('--t-border'), panel: get('--t-panel'),
    palette: [1, 2, 3, 4, 5, 6].map((i) => get(`--t-chart-${i}`)),
  };
}

type Obj = Record<string, any>;

const withColor = (o: Obj | undefined, color: string): Obj => ({ ...o, color });

function themedAxis(axis: Obj, t: ChartTheme): Obj {
  return {
    ...axis,
    axisLabel: withColor(axis.axisLabel, t.muted),
    axisLine: { ...axis.axisLine, lineStyle: withColor(axis.axisLine?.lineStyle, t.grid) },
    splitLine: { ...axis.splitLine, lineStyle: withColor(axis.splitLine?.lineStyle, t.grid) },
  };
}

/**
 * 回傳套上主題色的新 option，不改 base，保留 formatter、data、yAxisIndex；軸是物件或陣列都維持原本的形狀。
 * `color` 給完整 6 色（第一色是主色），多個量值與環圈圖才不會每一塊同色。
 */
export function themedChartOption<T extends object>(base: T, theme: ChartTheme = readChartTheme()): T {
  const o: Obj = { ...base };
  o.color = theme.palette;
  o.backgroundColor = 'transparent';
  o.textStyle = withColor(o.textStyle, theme.text);
  if (o.legend) {
    o.legend = {
      ...o.legend,
      textStyle: withColor(o.legend.textStyle, theme.text),
      pageTextStyle: withColor(o.legend.pageTextStyle, theme.text),
      pageIconColor: theme.text,
    };
  }
  if (o.tooltip) {
    o.tooltip = {
      ...o.tooltip,
      backgroundColor: theme.panel,
      borderColor: theme.grid,
      textStyle: withColor(o.tooltip.textStyle, theme.text),
    };
  }
  for (const key of ['xAxis', 'yAxis']) {
    if (o[key]) o[key] = Array.isArray(o[key]) ? o[key].map((a: Obj) => themedAxis(a, theme)) : themedAxis(o[key], theme);
  }
  if (Array.isArray(o.series)) {
    o.series = o.series.map((s: Obj) => (s.type === 'pie' ? { ...s, itemStyle: { ...s.itemStyle, borderColor: theme.panel } } : s));
  }
  return o as T;
}
