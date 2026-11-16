import { mount } from '@vue/test-utils';
import { VueFlow, type NodeChange } from '@vue-flow/core';
import { describe, expect, it, vi } from 'vitest';
import { defineComponent, h, nextTick } from 'vue';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

// 真的 Vue Flow：證明 DesignerCanvas 用的 disable-keyboard-a11y 讓方向鍵不再移動已選取的節點
// （內建的方向鍵移動只發 nodesChange、不發 nodeDragStop，設計頁不會把它寫回工作副本）。
const nodes = [{ id: 'a', position: { x: 100, y: 50 }, selected: true, data: {} }];

async function arrowRightChanges(flags: Record<string, boolean>) {
  const changes: NodeChange[] = [];
  const Host = defineComponent({
    setup: () => () => h(VueFlow, { id: `kb-${Math.random()}`, nodes, edges: [], nodesDraggable: true, ...flags, onNodesChange: (c: NodeChange[]) => changes.push(...c) }),
  });
  const w = mount(Host, { attachTo: document.body });
  await nextTick();
  const el = w.get('.vue-flow__node').element;
  el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
  await nextTick();
  const moved = changes.filter((c) => c.type === 'position');
  w.unmount();
  return moved;
}

describe('Vue Flow keyboard node move', () => {
  it('moves a selected node with the arrow keys by default (control)', async () => {
    expect((await arrowRightChanges({})).length).toBeGreaterThan(0);
  });
  it('does not move it with disable-keyboard-a11y (what DesignerCanvas sets)', async () => {
    expect(await arrowRightChanges({ disableKeyboardA11y: true })).toHaveLength(0);
  });
});
