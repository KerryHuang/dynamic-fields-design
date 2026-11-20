import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { camelize, defineComponent, h, ref } from 'vue';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { Edge, Node } from '@vue-flow/core';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerCanvas from './DesignerCanvas.vue';
import type { DesignerEdgeData, DesignerNodeData } from './designer-graph';

// 真實的拖拉、縮放只能在瀏覽器驗；這裡換成會記錄呼叫的替身（其餘匯出仍用真的）。
const flowFns = vi.hoisted(() => ({ fitView: vi.fn(), setCenter: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn() }));
vi.mock('@vue-flow/core', async (orig) => ({ ...(await orig<typeof import('@vue-flow/core')>()), useVueFlow: () => ({ ...flowFns, viewport: ref({ x: 0, y: 0, zoom: 1 }) }) }));

const VueFlowStub = defineComponent({
  name: 'VueFlow', inheritAttrs: false,
  setup: (_, { attrs }) => () => h('div', { 'data-testid': 'vf', 'data-props': JSON.stringify(Object.fromEntries(Object.entries(attrs).filter(([k]) => !/^on[A-Z]/.test(k)).map(([k, v]) => [camelize(k), v]))), onClick: () => (attrs.onNodeClick as (e: unknown) => void)({ node: { id: 'a' } }), onDblclick: () => (attrs.onNodesInitialized as () => void)() }),
});
const nodes = [{ id: 'a', position: { x: 100, y: 50 }, data: {} }] as unknown as Node<DesignerNodeData>[];
function mountCanvas(draggable = true, initialNodes?: string[]) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DesignerCanvas, {
    props: { canvasId: 'designer-po', nodes, edges: [] as Edge<DesignerEdgeData>[], draggable, initialNodes },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n], stubs: { VueFlow: VueFlowStub } },
  });
}
const props = (w: ReturnType<typeof mountCanvas>) => JSON.parse(w.get('[data-testid="vf"]').attributes('data-props')!) as Record<string, unknown>;

beforeEach(() => { Object.values(flowFns).forEach((f) => f.mockClear()); });

describe('DesignerCanvas', () => {
  it('passes the designer settings (J75, U56 a): wheel zooms, drag pans, pinch zooms, snap 8px, no delete key, no connecting yet', () => {
    expect(props(mountCanvas())).toMatchObject({
      id: 'designer-po', nodesDraggable: true, nodesConnectable: false, elementsSelectable: true, nodesFocusable: false, edgesFocusable: false, disableKeyboardA11y: true,
      zoomOnScroll: true, panOnScroll: false, zoomOnPinch: true, panOnDrag: true, snapToGrid: true, snapGrid: [8, 8], deleteKeyCode: null, fitViewOnInit: true,
    });
  });

  it('a read-only or phone canvas cannot drag nodes but can still pan, zoom and select', () => {
    expect(props(mountCanvas(false))).toMatchObject({ nodesDraggable: false, panOnDrag: true, zoomOnPinch: true, elementsSelectable: true });
  });

  it('exposes fitAll and focusNode (the composable never touches Vue Flow)', () => {
    const w = mountCanvas();
    const api = w.vm as unknown as { fitAll(): void; focusNode(id: string, inset?: number): void };
    api.fitAll();
    expect(flowFns.fitView).toHaveBeenCalled();
    api.focusNode('a');
    expect(flowFns.setCenter).toHaveBeenLastCalledWith(100 + 84, 50 + 42, expect.objectContaining({ zoom: 1 }));
    api.focusNode('a', 360); // 右側抽屜擋住 360px：中心往右移半個抽屜寬，節點落在可見區中央
    expect(flowFns.setCenter).toHaveBeenLastCalledWith(100 + 84 + 180, 50 + 42, expect.anything());
    api.focusNode('missing');
    expect(flowFns.setCenter).toHaveBeenCalledTimes(2);
  });

  it('phone first screen: with initialNodes it fits only those nodes (zoom 0.6–1) once nodes are measured, instead of the whole graph', async () => {
    const w = mountCanvas(false, ['a', 'b', 'c']);
    expect(props(w)).toMatchObject({ fitViewOnInit: false });
    expect(flowFns.fitView).not.toHaveBeenCalled(); // 節點還沒量好
    await w.get('[data-testid="vf"]').trigger('dblclick'); // nodesInitialized
    expect(flowFns.fitView).toHaveBeenCalledWith({ nodes: ['a', 'b', 'c'], padding: 0.2, minZoom: 0.6, maxZoom: 1 });
    await w.get('[data-testid="vf"]').trigger('dblclick'); // 之後加節點再觸發也不重框
    expect(flowFns.fitView).toHaveBeenCalledTimes(1);
    (w.vm as unknown as { fitAll(): void }).fitAll(); // 「全圖」鈕不變
    expect(flowFns.fitView).toHaveBeenLastCalledWith({ padding: 0.12, duration: 200 });
  });

  it('without initialNodes (tablet and up) the whole graph is fitted on init as before', async () => {
    const w = mountCanvas();
    expect(props(w)).toMatchObject({ fitViewOnInit: true });
    await w.get('[data-testid="vf"]').trigger('dblclick');
    expect(flowFns.fitView).not.toHaveBeenCalled();
  });

  it('a node click reports the node id', async () => {
    const w = mountCanvas();
    await w.get('[data-testid="vf"]').trigger('click');
    expect(w.emitted('node-open')![0]).toEqual(['a']);
  });

  it('has a labelled group and zoom buttons', () => {
    const w = mountCanvas();
    expect(w.get('[data-testid="de-canvas"]').attributes('aria-label')).toBe('流程圖設計畫布');
    expect(w.get('[data-testid="de-zoom-in"]').attributes('aria-label')).toBe('放大');
  });
});
