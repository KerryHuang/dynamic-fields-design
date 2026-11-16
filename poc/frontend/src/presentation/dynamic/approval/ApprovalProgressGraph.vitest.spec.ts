import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalActionView, ApprovalProgressGraph, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import zhTW from 'src/i18n/zh-TW.json';
import ApprovalProgressGraphView from './ApprovalProgressGraph.vue';

// 畫布只有在需要畫圖時才會被動態載入並掛載；替身記下掛載次數，沒畫圖的情況要斷言它是 0。
const loads = vi.hoisted(() => ({ mounted: 0 }));
// 真的畫布會載入 Vue Flow；這裡換成會把收到的屬性寫出來的替身。
vi.mock('./ProgressGraphCanvas.vue', async () => {
  const { defineComponent, h } = await import('vue');
  return {
    __esModule: true,
    default: defineComponent({
      name: 'CanvasStub', props: ['instanceId', 'mode', 'nodes', 'edges', 'plan', 'width', 'height'], emits: ['node-open'],
      // 每個節點一顆按鈕：點了就等於點畫布上的那個節點（真的點擊與鍵盤由 ProgressGraphNode 的 spec 驗）。
      setup: (p, { emit }) => {
        loads.mounted++;
        return () => h('div', {
          'data-testid': 'canvas-stub', 'data-height': p.height, 'data-width': p.width, 'data-mode': p.mode, 'data-instance': p.instanceId,
          'data-zoom': p.plan.zoom, 'data-fit': p.plan.fit, 'data-minzoom': p.plan.minZoom,
        }, (p.nodes as { id: string }[]).map((n) => h('button', { 'data-testid': `open-${n.id}`, onClick: () => emit('node-open', n.id) })));
      },
    }),
  };
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

const action = (stageRunId: number | null, comment: string): ApprovalActionView => ({
  action: 'approve', actorUserId: 'u1', actorName: '王小華', onBehalfOfUserId: null, targetUserIds: [], comment, data: null, createdTime: '2026-11-15T10:32:00', stageRunId,
});

let mounted: ReturnType<typeof mount> | null = null;
function mountGraph(graph: ApprovalProgressGraph, boxWidth: number, p: { stages?: ApprovalStageView[]; actions?: ApprovalActionView[]; boxHeight?: number } = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  // jsdom 不排版：讓量到的框寬、框高等於指定值
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(boxWidth);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(p.boxHeight ?? 700);
  mounted = mount(ApprovalProgressGraphView, {
    props: { graph, stages: p.stages ?? [stage(1, 1)], actions: p.actions ?? [], schema: null, instanceId: 7 },
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n] },
    attachTo: document.body,
  });
  return mounted;
}

describe('ApprovalProgressGraph', () => {
  const original = { w: window.innerWidth, h: window.innerHeight };
  beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); loads.mounted = 0; });
  afterEach(async () => { mounted?.unmount(); mounted = null; document.body.innerHTML = ''; vi.restoreAllMocks(); window.innerWidth = original.w; window.innerHeight = original.h; await setWidth(original.w); });

  it('below 600px shows a 180px thumbnail without gestures', async () => {
    await setWidth(390);
    const w = mountGraph(line(3), 358);
    await flushPromises();
    expect(w.get('.pg-frame').attributes('style')).toContain('height: 180px');
    const canvas = w.get('[data-testid="canvas-stub"]');
    expect(canvas.attributes('data-mode')).toBe('thumb'); // 畫布在 thumb 模式不能拖、不能縮放、節點不可點（ProgressGraphCanvas、ProgressGraphNode 的 spec）
    expect(canvas.attributes('data-height')).toBe('180');
    // 縮圖整張被一個點擊區蓋住，手勢到不了畫布，頁面照常捲動
    expect(w.get('[data-testid="pg-thumb"]').attributes('aria-hidden')).toBe('true');
    expect(w.get('[data-testid="pg-expand"]').text()).toContain('放大檢視');
    expect(loads.mounted).toBe(1);
  });

  it('the thumbnail always fits the whole graph, even a 14-stage flow', async () => {
    await setWidth(390);
    const w = mountGraph(line(16), 358); // 14 關：開始＋14 個簽核＋結束，約 3500px 寬
    await flushPromises();
    const c = w.get('[data-testid="canvas-stub"]');
    const zoom = Number(c.attributes('data-zoom'));
    expect(zoom).toBeCloseTo(Number(c.attributes('data-fit')), 5); // 縮到整張放進去，不是 0.8
    expect(zoom).toBeLessThan(0.2);
    expect(Number(c.attributes('data-minzoom'))).toBeLessThanOrEqual(zoom); // 最小縮放不會夾住它
  });

  it('tapping the thumbnail or the expand button opens a maximized dialog with an interactive canvas', async () => {
    await setWidth(390);
    const w = mountGraph(line(3), 358);
    await flushPromises();
    expect(document.querySelector('[data-testid="pg-full"]')).toBeNull();
    await w.get('[data-testid="pg-thumb"]').trigger('click');
    await flushPromises();
    expect(document.querySelector('.q-dialog__inner--maximized [data-testid="pg-full"]')).not.toBeNull();
    (document.querySelector('[data-testid="pg-full-close"]') as HTMLElement).click();
    await new Promise((r) => setTimeout(r, 500));
    expect(document.querySelector('[data-testid="pg-full"]')).toBeNull();
    await w.get('[data-testid="pg-expand"]').trigger('click');
    await flushPromises();
    expect(document.querySelector('[data-testid="pg-full"]')).not.toBeNull();
    await new Promise((r) => setTimeout(r, 500));
    const full = document.querySelector('[data-testid="pg-full-frame"] [data-testid="canvas-stub"]');
    expect(full?.getAttribute('data-mode')).toBe('full'); // full = 可平移、可雙指縮放、有縮放按鈕與可點的節點
  });

  it('the dialog applies the initial viewport only after it is shown', async () => {
    await setWidth(390);
    const w = mountGraph(line(3), 358);
    await flushPromises();
    await w.get('[data-testid="pg-expand"]').trigger('click');
    await flushPromises();
    // 對話框已開、轉場還沒結束（@show 還沒來）：裡面還沒有畫布，視角不會套在沒有尺寸的容器上
    expect(document.querySelector('[data-testid="pg-full"]')).not.toBeNull();
    expect(document.querySelector('[data-testid="pg-full-frame"] [data-testid="canvas-stub"]')).toBeNull();
    await new Promise((r) => setTimeout(r, 500));
    expect(document.querySelector('[data-testid="pg-full-frame"] [data-testid="canvas-stub"]')).not.toBeNull();
  });

  it('thumbnail and dialog canvases have different ids', async () => {
    await setWidth(390);
    const w = mountGraph(line(3), 358);
    await flushPromises();
    await w.get('[data-testid="pg-expand"]').trigger('click');
    await new Promise((r) => setTimeout(r, 500));
    const canvases = [...document.querySelectorAll('[data-testid="canvas-stub"]')];
    expect(canvases).toHaveLength(2);
    // 畫布 id = progress-{instanceId}-{mode}：同一個實例、模式不同，所以兩個 store 不會共用
    expect(canvases.map((c) => c.getAttribute('data-instance'))).toEqual(['7', '7']);
    expect(new Set(canvases.map((c) => c.getAttribute('data-mode')))).toEqual(new Set(['thumb', 'full']));
  });

  it('the full-screen dialog closes with its close button and with Escape', async () => {
    await setWidth(390);
    const w = mountGraph(line(3), 358);
    await flushPromises();
    await w.get('[data-testid="pg-expand"]').trigger('click');
    await new Promise((r) => setTimeout(r, 500));
    expect(document.querySelector('[data-testid="pg-full-close"]')?.getAttribute('aria-label')).toBe('關閉');
    // Quasar 對話框的 Esc：window 收到 keydown 再收到 keyup 才關
    for (const type of ['keydown', 'keyup']) window.dispatchEvent(new KeyboardEvent(type, { key: 'Escape', keyCode: 27 }));
    await new Promise((r) => setTimeout(r, 500));
    expect(document.querySelector('[data-testid="pg-full"]')).toBeNull();
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

  describe('node detail', () => {
    // start → 主管（兩次執行）→ 加簽 → 條件 → 結束
    const flow = (): ApprovalProgressGraph => ({
      nodes: [
        { id: 's', kind: 'start', state: 'passed' }, { id: 'm', kind: 'approval', state: 'active', name: { 'zh-TW': '財務二' }, runIds: [2, 5] },
        { id: 'as-9', kind: 'addSign', state: 'notReached', runIds: [9], hostId: 'm' }, { id: 'c', kind: 'condition', state: 'passed' },
        { id: 'h', kind: 'approval', state: 'hidden' }, { id: 'e', kind: 'end', state: 'notReached' },
      ],
      edges: [{ id: 'e1', from: 's', to: 'm', traversed: true }],
    });
    const stages = [stage(2, 2, { status: 'returned', name: { 'zh-TW': '財務二' } }), stage(5, 5, { name: { 'zh-TW': '財務二' } }), stage(9, 9, { name: { 'zh-TW': '加簽' }, status: 'queued' })];
    const actions = [action(2, '附件不齊'), action(9, '加簽的意見'), action(null, '送出時的備註')];
    const detailIds = () => [...document.querySelectorAll('[data-testid="pg-detail"] [data-testid^="stage-"]')].map((e) => e.getAttribute('data-testid'));

    it('clicking an approval node opens its runs, oldest first, with their comments and the node name and state in the title', async () => {
      await setWidth(1366);
      const w = mountGraph(flow(), 1000, { stages, actions });
      await flushPromises();
      expect(document.querySelector('[data-testid="pg-detail"]')).toBeNull();
      await w.get('[data-testid="open-m"]').trigger('click');
      await flushPromises();
      expect(detailIds()).toEqual(['stage-2', 'stage-5']);
      expect(document.querySelector('[data-testid="pg-detail-title"]')?.textContent).toBe('財務二 · 簽核中');
      expect([...document.querySelectorAll('[data-testid="pg-detail"] [data-testid="log-comment"]')].map((e) => e.textContent)).toEqual(['附件不齊']);
    });

    it('add-sign nodes open their own runs', async () => {
      await setWidth(1366);
      const w = mountGraph(flow(), 1000, { stages, actions });
      await flushPromises();
      await w.get('[data-testid="open-as-9"]').trigger('click');
      await flushPromises();
      expect(detailIds()).toEqual(['stage-9']);
      expect(document.querySelector('[data-testid="pg-detail-title"]')?.textContent).toBe('加簽 · 等待中');
      expect([...document.querySelectorAll('[data-testid="pg-detail"] [data-testid="log-comment"]')].map((e) => e.textContent)).toEqual(['加簽的意見']);
    });

    it('start, condition, hidden and end nodes open nothing', async () => {
      await setWidth(1366);
      const w = mountGraph(flow(), 1000, { stages, actions });
      await flushPromises();
      for (const id of ['s', 'c', 'h', 'e']) await w.get(`[data-testid="open-${id}"]`).trigger('click');
      await flushPromises();
      expect(document.querySelector('[data-testid="pg-detail"]')).toBeNull();
    });

    it('on a phone the detail is a bottom sheet over the full-screen view', async () => {
      await setWidth(390);
      const w = mountGraph(flow(), 358, { stages, actions });
      await flushPromises();
      await w.get('[data-testid="pg-expand"]').trigger('click');
      await new Promise((r) => setTimeout(r, 500));
      (document.querySelector('[data-testid="pg-full"] [data-testid="open-m"]') as HTMLElement).click();
      await flushPromises();
      expect(document.querySelector('.q-dialog__inner--bottom [data-testid="pg-detail"]')).not.toBeNull();
      expect(detailIds()).toEqual(['stage-2', 'stage-5']);
    });
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
