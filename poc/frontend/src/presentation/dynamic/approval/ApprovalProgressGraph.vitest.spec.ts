import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalProgressGraph, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import ApprovalProgressGraphView from './ApprovalProgressGraph.vue';

// 畫布只有在需要畫圖時才會被動態載入並掛載；替身記下掛載次數，沒畫圖的情況要斷言它是 0。
const loads = vi.hoisted(() => ({ mounted: 0 }));
// 真的畫布會載入 Vue Flow；這裡換成會把收到的屬性寫出來的替身。
vi.mock('./ProgressGraphCanvas.vue', async () => {
  const { defineComponent, h } = await import('vue');
  return { __esModule: true, default: defineComponent({ name: 'CanvasStub', props: ['instanceId', 'mode', 'nodes', 'edges', 'plan', 'width', 'height'], setup: (p) => { loads.mounted++; return () => h('div', { 'data-testid': 'canvas-stub', 'data-height': p.height, 'data-width': p.width, 'data-mode': p.mode }); } }) };
});

const stage = (id: number, seq: number, p: Partial<ApprovalStageView> = {}): ApprovalStageView => ({
  id, seq, stageCode: null, name: { 'zh-TW': 's' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, tasks: [], editableFieldKeys: [], ...p,
});
const line = (n: number): ApprovalProgressGraph => ({
  nodes: Array.from({ length: n }, (_, i) => ({ id: `n${i}`, kind: i === 0 ? 'start' : 'approval', state: 'passed' as const })),
  edges: Array.from({ length: n - 1 }, (_, i) => ({ id: `e${i}`, from: `n${i}`, to: `n${i + 1}`, traversed: true })),
});
/** 一個起點分出 k 條並行分支的圖，高度約 k * 108。 */
const fan = (k: number): ApprovalProgressGraph => ({
  nodes: [{ id: 's', kind: 'start', state: 'passed' }, { id: 'f', kind: 'fork', state: 'passed' }, ...Array.from({ length: k }, (_, i) => ({ id: `b${i}`, kind: 'approval' as const, state: 'active' as const })), { id: 'j', kind: 'join', state: 'active', joinMode: 'all' }],
  edges: [{ id: 'e0', from: 's', to: 'f', traversed: true }, ...Array.from({ length: k }, (_, i) => ({ id: `a${i}`, from: 'f', to: `b${i}`, traversed: true })), ...Array.from({ length: k }, (_, i) => ({ id: `c${i}`, from: `b${i}`, to: 'j', traversed: false }))],
});

async function setWidth(width: number) {
  window.innerWidth = width;
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 80)); // Quasar 的 Screen 有 50 毫秒節流
}

function mountGraph(graph: ApprovalProgressGraph, boxWidth: number) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  // jsdom 不排版：讓量到的框寬等於指定值
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(boxWidth);
  return mount(ApprovalProgressGraphView, {
    props: { graph, stages: [stage(1, 1)], instanceId: 7 },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

describe('ApprovalProgressGraph', () => {
  const original = { w: window.innerWidth, h: window.innerHeight };
  beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); loads.mounted = 0; });
  afterEach(async () => { vi.restoreAllMocks(); window.innerWidth = original.w; window.innerHeight = original.h; await setWidth(original.w); });

  it('renders nothing below 600px in this segment', async () => {
    await setWidth(390);
    const w = mountGraph(line(3), 358);
    await flushPromises();
    expect(w.find('[data-testid="approval-progress-graph"]').exists()).toBe(false);
    expect(w.find('[data-testid="canvas-stub"]').exists()).toBe(false);
    expect(loads.mounted).toBe(0); // 畫布根本沒被載入
  });

  it('draws at 600px and wider', async () => {
    await setWidth(820);
    const w = mountGraph(line(3), 780);
    await flushPromises();
    expect(w.find('[data-testid="approval-progress-graph"]').exists()).toBe(true);
    expect(w.get('[data-testid="canvas-stub"]').attributes('data-mode')).toBe('inline');
    expect(w.get('[data-testid="canvas-stub"]').attributes('data-width')).toBe('780');
    expect(loads.mounted).toBe(1);
  });

  it('height is clamped between 160px and 60% of the viewport', async () => {
    window.innerHeight = 1000;
    await setWidth(1366);
    const short = mountGraph(line(3), 1000);
    await flushPromises();
    expect(short.get('.pg-frame').attributes('style')).toContain('height: 160px'); // 84 + 32 = 116 → 下限
    const tall = mountGraph(fan(10), 1000); // 內容約 1200 高
    await flushPromises();
    expect(tall.get('.pg-frame').attributes('style')).toContain('height: 600px'); // 上限 60%
    expect(tall.get('[data-testid="canvas-stub"]').attributes('data-height')).toBe('600');
  });

  it('shows a skeleton of the same height until the box has a width', async () => {
    await setWidth(1366);
    const w = mountGraph(line(3), 0);
    await flushPromises();
    expect(w.find('.q-skeleton').exists()).toBe(true);
    expect(w.find('[data-testid="canvas-stub"]').exists()).toBe(false);
    expect(w.get('.pg-frame').attributes('style')).toContain('height: 160px');
  });

  it('shows a skeleton while the canvas module is loading, then the canvas', async () => {
    await setWidth(1366);
    const w = mountGraph(line(3), 1000);
    expect(w.find('.q-skeleton').exists()).toBe(true);
    await flushPromises();
    expect(w.find('.q-skeleton').exists()).toBe(false);
    expect(w.find('[data-testid="canvas-stub"]').exists()).toBe(true);
  });
});
