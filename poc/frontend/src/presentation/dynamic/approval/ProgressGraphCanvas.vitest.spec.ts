import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { camelize, defineComponent, h } from 'vue';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { Edge, Node } from '@vue-flow/core';
import zhTW from 'src/i18n/zh-TW.json';
import ProgressGraphCanvas from './ProgressGraphCanvas.vue';
import type { ProgressNodeData, ViewportPlan } from './progress-graph';

// 把傳給 VueFlow 的屬性寫進 data-props，直接斷言唯讀設定（真實的平移、縮放只能在瀏覽器驗）。
const VueFlowStub = defineComponent({
  name: 'VueFlow', inheritAttrs: false,
  setup: (_, { attrs }) => () => h('div', { 'data-testid': 'vf', 'data-props': JSON.stringify(Object.fromEntries(Object.entries(attrs).map(([k, v]) => [camelize(k), v]))) }),
});
const plan = (p: Partial<ViewportPlan> = {}): ViewportPlan => ({ zoom: 0.8, center: { x: 400, y: 42 }, minZoom: 0.2, fit: 0.5, ...p });

function mountCanvas(p: { instanceId?: number; mode?: 'inline' | 'thumb' | 'full'; plan?: ViewportPlan } = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(ProgressGraphCanvas, {
    props: { instanceId: p.instanceId ?? 7, mode: p.mode ?? 'inline', nodes: [] as Node<ProgressNodeData>[], edges: [] as Edge[], plan: p.plan ?? plan(), width: 800, height: 200 },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n], stubs: { VueFlow: VueFlowStub } },
  });
}
const flowProps = (w: ReturnType<typeof mountCanvas>) => JSON.parse(w.get('[data-testid="vf"]').attributes('data-props')!) as Record<string, unknown>;

describe('ProgressGraphCanvas', () => {
  it('passes readonly props: no drag, connect, select, focus, keyboard a11y, wheel zoom or scroll capture', () => {
    const f = flowProps(mountCanvas());
    expect(f).toMatchObject({
      nodesDraggable: false, nodesConnectable: false, elementsSelectable: false, nodesFocusable: false, edgesFocusable: false, disableKeyboardA11y: true,
      zoomOnScroll: false, panOnScroll: false, zoomOnDoubleClick: false, preventScrolling: false, zoomOnPinch: true, panOnDrag: true, maxZoom: 1.5,
      deleteKeyCode: null, selectionKeyCode: null, multiSelectionKeyCode: null, fitViewOnInit: false,
    });
  });

  it('uses a unique id per instance and mode', () => {
    expect(flowProps(mountCanvas({ instanceId: 7, mode: 'inline' })).id).toBe('progress-7-inline');
    expect(flowProps(mountCanvas({ instanceId: 7, mode: 'full' })).id).toBe('progress-7-full');
    expect(flowProps(mountCanvas({ instanceId: 8, mode: 'inline' })).id).toBe('progress-8-inline');
  });

  it('binds minZoom from the initial viewport and starts at its transform', () => {
    const f = flowProps(mountCanvas({ plan: plan({ minZoom: 0.09, zoom: 0.8, center: { x: 1000, y: 42 } }) }));
    expect(f.minZoom).toBe(0.09);
    expect(f.defaultViewport).toEqual({ x: 400 - 1000 * 0.8, y: 100 - 42 * 0.8, zoom: 0.8 });
  });

  it('a thumbnail cannot be panned or pinched and has no zoom buttons', () => {
    const w = mountCanvas({ mode: 'thumb' });
    expect(flowProps(w)).toMatchObject({ panOnDrag: false, zoomOnPinch: false });
    expect(w.find('[data-testid="pg-zoom-in"]').exists()).toBe(false);
  });

  it('has three labelled zoom buttons and a labelled group', () => {
    const w = mountCanvas();
    expect(w.get('[data-testid="pg-zoom-in"]').attributes('aria-label')).toBe('放大');
    expect(w.get('[data-testid="pg-zoom-out"]').attributes('aria-label')).toBe('縮小');
    expect(w.get('[data-testid="pg-fit"]').attributes('aria-label')).toBe('全圖');
    expect(w.get('[data-testid="pg-canvas"]').attributes('aria-label')).toBe('簽核進度圖');
  });
});
