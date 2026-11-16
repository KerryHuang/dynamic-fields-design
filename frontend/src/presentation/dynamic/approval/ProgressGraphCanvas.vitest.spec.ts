import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { camelize, defineComponent, h } from 'vue';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { Edge, Node } from '@vue-flow/core';
import zhTW from 'src/i18n/zh-TW.json';
import ProgressGraphCanvas from './ProgressGraphCanvas.vue';
import canvasSource from './ProgressGraphCanvas.vue?raw';
import type { ProgressNodeData, ViewportPlan } from './progress-graph';

// 真正的 store 只在瀏覽器裡驗；這裡換成會記錄呼叫的替身（其餘匯出，例如 BaseEdge、getSmoothStepPath，仍用真的）。
const flowFns = vi.hoisted(() => ({ setViewport: vi.fn(), fitView: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn() }));
vi.mock('@vue-flow/core', async (orig) => ({ ...(await orig<typeof import('@vue-flow/core')>()), useVueFlow: () => flowFns }));

const edgeProps = {
  id: 'e1', sourceX: 100, sourceY: 50, targetX: 400, targetY: 140, sourcePosition: 'right', targetPosition: 'left',
  label: '金額大…', data: { bendX: 350, fullLabel: '金額大於十萬' },
};
// 把傳給 VueFlow 的屬性寫進 data-props，直接斷言唯讀設定（真實的平移、縮放只能在瀏覽器驗）。
const nodeData = { kind: 'approval', runIds: [1], clickable: true, small: false, width: 168, height: 84, label: '主管', kindIcon: 'person', stateIcon: null, color: null, frame: 'solid', strike: false, dim: false, statusText: '', joinModeText: null, overdue: false, overdueText: '', ariaLabel: '主管' };
const VueFlowStub = defineComponent({
  name: 'VueFlow', inheritAttrs: false,
  // 線的插槽放在 <svg> 裡渲染，才會建立 SVG 元素（BaseEdge 會量文字框）。
  setup: (_, { attrs, slots }) => () => h('div', { 'data-testid': 'vf', 'data-props': JSON.stringify(Object.fromEntries(Object.entries(attrs).map(([k, v]) => [camelize(k), v]))) }, [
    h('svg', slots['edge-progress']?.(edgeProps)), slots['node-progress']?.({ id: 'n1', data: nodeData }), slots['node-addSign']?.({ id: 'as-9', data: { ...nodeData, kind: 'addSign', small: true } }),
  ]),
});
const plan = (p: Partial<ViewportPlan> = {}): ViewportPlan => ({ zoom: 0.8, center: { x: 400, y: 42 }, minZoom: 0.2, fit: 0.5, ...p });

function mountCanvas(p: { instanceId?: number; mode?: 'inline' | 'thumb' | 'full'; plan?: ViewportPlan } = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(ProgressGraphCanvas, {
    props: { instanceId: p.instanceId ?? 7, mode: p.mode ?? 'inline', nodes: [] as Node<ProgressNodeData>[], edges: [] as Edge[], plan: p.plan ?? plan(), width: 800, height: 200 },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n], stubs: { VueFlow: VueFlowStub, Handle: true } },
  });
}
const flowProps = (w: ReturnType<typeof mountCanvas>) => JSON.parse(w.get('[data-testid="vf"]').attributes('data-props')!) as Record<string, unknown>;

beforeAll(() => { (SVGElement.prototype as unknown as { getBBox: () => object }).getBBox = () => ({ x: 0, y: 0, width: 40, height: 12 }); });
beforeEach(() => { Object.values(flowFns).forEach((f) => f.mockClear()); });

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

  it('draws edges with the bend point from the layout (in a column gap), not the midpoint', () => {
    const d = (w: ReturnType<typeof mountCanvas>) => w.get('path.vue-flow__edge-path').attributes('d');
    expect(d(mountCanvas())).toMatch(/350,/);
    expect(d(mountCanvas())).not.toMatch(/250,/); // 兩端的中點 (100 + 400) / 2
  });

  it('a cut edge label keeps the full text in an SVG title', () => {
    const w = mountCanvas();
    expect(w.get('text.vue-flow__edge-text').text()).toContain('金額大…');
    expect(w.get('text.vue-flow__edge-text title').text()).toBe('金額大於十萬');
  });

  it('re-applies the viewport when the size changes but not when the plan is recomputed with the same numbers', async () => {
    const w = mountCanvas();
    await w.setProps({ plan: plan({ minZoom: 0.15 }) });
    await w.setProps({ plan: plan() }); // 新物件、數字一樣（例如只有視窗高度的事件讓 plan 重算）
    expect(flowFns.setViewport).not.toHaveBeenCalled();
    await w.setProps({ width: 900 });
    expect(flowFns.setViewport).toHaveBeenCalledTimes(1);
    await w.setProps({ plan: plan({ zoom: 0.5 }) });
    expect(flowFns.setViewport).toHaveBeenCalledTimes(2);
    expect(flowFns.setViewport).toHaveBeenLastCalledWith({ x: 450 - 400 * 0.5, y: 100 - 42 * 0.5, zoom: 0.5 });
  });
  it('opening a node reports its id, for approval and add-sign nodes alike', async () => {
    const w = mountCanvas();
    const nodes = w.findAll('[data-testid="pg-node"]');
    await nodes[0].trigger('click');
    await nodes[1].trigger('keydown', { key: 'Enter' });
    expect(w.emitted('node-open')).toEqual([['n1'], ['as-9']]);
  });

  it('a thumbnail is hidden from keyboard and screen readers, and its nodes do nothing', async () => {
    const w = mountCanvas({ mode: 'thumb' });
    const root = w.get('[data-testid="pg-canvas"]');
    expect(root.attributes('aria-hidden')).toBe('true');
    expect(root.attributes('inert')).toBeDefined();
    expect(root.classes()).toContain('pg-thumb');
    expect(w.get('[data-testid="pg-node"]').attributes('tabindex')).toBeUndefined();
    await w.get('[data-testid="pg-node"]').trigger('click');
    expect(w.emitted('node-open')).toBeUndefined();
    const live = mountCanvas({ mode: 'full' }).get('[data-testid="pg-canvas"]');
    expect(live.attributes('aria-hidden')).toBeUndefined();
    expect(live.attributes('inert')).toBeUndefined();
  });

  // 同 ProgressGraphNode：縮圖的畫布要吃掉所有指標事件，手勢才到不了畫布；jsdom 看不到 CSS，所以守原始碼。
  it('a thumbnail canvas ignores pointer events in CSS', () => {
    expect(canvasSource).toMatch(/\.pg-canvas\.pg-thumb\s*\{[^}]*pointer-events:\s*none/);
  });
});
