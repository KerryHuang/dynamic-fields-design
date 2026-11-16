// 簽核進度圖：API 的圖 → 排版輸入 → Vue Flow 的節點與線，以及初始視角。純函式，不 import Vue Flow 的 runtime
// （只有 ProgressGraphCanvas.vue、ProgressGraphNode.vue 可以，見 fg4 計畫 J3）。
// 狀態只在後端算；這裡只做「狀態 → 顏色、圖示、文字」的對照，顏色與圖示和時間軸共用 approval-display.ts 那一份。
import type { Edge, Node } from '@vue-flow/core';
import type { ApprovalProgressGraph, ApprovalProgressNode, ApprovalProgressState, ApprovalStageView } from 'src/domain/dynamic/approval-types';
import { ADDSIGN_H, ADDSIGN_W, COL_GAP, edgeBendX, layoutLeftToRight, NODE_H, NODE_W, type LayoutResult } from 'src/domain/dynamic/approval-graph-layout';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { stageColor, stageIcon } from './approval-display';

type Translate = (key: string, params?: Record<string, unknown>) => string;
export type GraphMode = 'inline' | 'thumb' | 'full';

/** 畫面比例與高度（畫面呈現，不是業務數字：fg4 計畫 U2、U4）。 */
export const MIN_READABLE_ZOOM = 0.8;
export const MIN_ZOOM_FLOOR = 0.2;
export const MAX_ZOOM = 1.5;
export const GRAPH_PAD = 16;
export const GRAPH_MIN_HEIGHT = 160;
export const GRAPH_MAX_HEIGHT_RATIO = 0.6;
export const THUMB_HEIGHT = 180;

export interface StateView {
  /** Quasar 語意色；null = 中性（淡灰或灰框）。 */
  color: string | null;
  icon: string | null;
  /** 狀態字的 i18n 鍵；null = 畫面不顯示。 */
  textKey: string | null;
  frame: 'solid' | 'dashed' | 'neutral';
  strike: boolean;
  dim: boolean;
}

const NEUTRAL: StateView = { color: null, icon: null, textKey: null, frame: 'neutral', strike: false, dim: false };

/** J16：節點狀態 → 樣式。進行中沿用時間軸的 warning＋hourglass_top（U12）；已結束與已取消用時間軸「已結束」的同一個圖示。 */
export function stateView(state: ApprovalProgressState | string, kind: string): StateView {
  const approvalLike = kind === 'approval' || kind === 'addSign';
  switch (state) {
    case 'passed':
      return { ...NEUTRAL, color: stageColor('approved'), icon: stageIcon('approved'), frame: 'solid', textKey: approvalLike ? 'approval.stageStatus.approved' : 'approval.graph.state.passed' };
    case 'active':
      return { ...NEUTRAL, color: stageColor('active'), icon: stageIcon('active'), frame: 'solid', textKey: kind === 'join' ? 'approval.graph.state.waiting' : 'approval.stageStatus.active' };
    case 'returned':
      return { ...NEUTRAL, color: stageColor('returned'), icon: stageIcon('returned'), frame: 'solid', textKey: 'approval.stageStatus.returned' };
    case 'skipped':
      return { ...NEUTRAL, color: stageColor('skipped'), icon: stageIcon('skipped'), frame: 'dashed', textKey: 'approval.stageStatus.skipped' };
    case 'cancelled':
      return { ...NEUTRAL, color: stageColor('closed'), icon: stageIcon('closed'), frame: 'solid', strike: true, textKey: 'approval.graph.state.cancelled' };
    case 'ended':
      return { ...NEUTRAL, color: stageColor('closed'), icon: stageIcon('closed'), frame: 'solid', textKey: 'approval.stageStatus.closed' };
    case 'notReached':
      // 排隊中的加簽顯示「等待中」；其他沒走到的節點畫面不寫字（只在 aria-label 與提示念「未走到」）。
      return { ...NEUTRAL, dim: true, textKey: kind === 'addSign' ? 'approval.stageStatus.queued' : null };
    case 'changed':
      return { ...NEUTRAL, color: 'muted', icon: 'history', textKey: 'approval.graph.state.changed' };
    default: // hidden，以及較新的後端送來、這裡不認得的狀態：中性灰框、不寫字
      return NEUTRAL;
  }
}

const KIND_ICON: Record<string, string> = {
  start: 'play_arrow', end: 'flag', condition: 'alt_route', fork: 'call_split', join: 'call_merge', notify: 'notifications', approval: 'person', addSign: 'person_add',
};
const KNOWN_KIND = new Set(Object.keys(KIND_ICON));

export interface ProgressNodeData {
  kind: string;
  small: boolean;
  width: number;
  height: number;
  label: string;
  kindIcon: string;
  stateIcon: string | null;
  color: string | null;
  frame: StateView['frame'];
  strike: boolean;
  dim: boolean;
  /** 狀態字，沒有就是空字串。 */
  statusText: string;
  /** 匯合節點名稱下一行的小字（U7）。 */
  joinModeText: string | null;
  overdue: boolean;
  overdueText: string;
  /** 原生提示與 aria-label：名稱＋狀態。 */
  ariaLabel: string;
}

/** 自訂線（type 'progress'）要用的資料：轉折點 x（圖座標）與沒被截斷的完整標籤。 */
export interface ProgressEdgeData { bendX: number; fullLabel: string }

/** 標籤可用寬度：欄距扣掉底色左右內距（每側 4px）。 */
export const EDGE_LABEL_MAX_PX = COL_GAP - 8;
const CJK_PX = 11;
const LATIN_PX = 6.5;
const charPx = (ch: string) => ((ch.codePointAt(0) ?? 0) >= 0x2e80 ? CJK_PX : LATIN_PX);

/** 依欄距估算寬度（11px 字：CJK 約 11px、其他約 6.5px），放不下就截斷並加「…」，讓讀者知道字不完整。 */
export function truncateEdgeLabel(text: string, maxPx: number = EDGE_LABEL_MAX_PX): string {
  const chars = [...text];
  if (chars.reduce((w, c) => w + charPx(c), 0) <= maxPx) return text;
  let w = CJK_PX; // 「…」
  let out = '';
  for (const c of chars) {
    w += charPx(c);
    if (w > maxPx) break;
    out += c;
  }
  return `${out}…`;
}

export interface BuiltProgressGraph {
  nodes: Node<ProgressNodeData>[];
  edges: Edge[];
  layout: LayoutResult;
  /** 初始視角要對準的節點中心；沒有任何走過的節點時為 null。 */
  focus: { x: number; y: number } | null;
}

export interface BuildContext { locale: string; t: Translate; now?: number }

/** 和時間軸同一條規則：進行中、有期限、期限已過。取該節點最新一次執行。 */
function isOverdue(node: ApprovalProgressNode, stagesById: Map<number, ApprovalStageView>, now: number): boolean {
  const runIds = node.runIds ?? [];
  const run = stagesById.get(runIds[runIds.length - 1]);
  return !!run && run.status === 'active' && !!run.dueTime && new Date(run.dueTime).getTime() < now;
}

function nodeData(n: ApprovalProgressNode, stagesById: Map<number, ApprovalStageView>, ctx: BuildContext): ProgressNodeData {
  const { t, locale } = ctx;
  const view = stateView(n.state, n.kind);
  const small = n.kind === 'addSign';
  const kindLabel = KNOWN_KIND.has(n.kind) ? t(`approval.graph.kind.${n.kind}`) : n.kind;
  const label = resolveI18n(n.name ?? undefined, locale) || kindLabel;
  const statusText = view.textKey ? t(view.textKey) : '';
  const joinModeText = n.kind === 'join' && n.joinMode ? t(`approval.graph.joinMode.${n.joinMode}`) : null;
  const overdue = isOverdue(n, stagesById, ctx.now ?? Date.now());
  // 沒走到的節點雖然畫面不寫字，名稱後面仍念「未走到」；hidden 只念名稱。
  const spoken = n.state === 'notReached' ? t('approval.graph.state.notReached') : statusText;
  const ariaLabel = [label, joinModeText, spoken].filter(Boolean).join(' · ');
  return {
    kind: n.kind, small, width: small ? ADDSIGN_W : NODE_W, height: small ? ADDSIGN_H : NODE_H,
    label, kindIcon: KIND_ICON[n.kind] ?? 'help_outline', stateIcon: view.icon, color: view.color, frame: view.frame, strike: view.strike, dim: view.dim,
    statusText, joinModeText, overdue, overdueText: overdue ? t('approval.panel.overdue') : '', ariaLabel,
  };
}

/** API 的圖 → Vue Flow 的節點與線（含排版）。節點與線的順序照 API；沒有名稱的節點用型別文字。 */
export function buildProgressGraph(graph: ApprovalProgressGraph, stages: ApprovalStageView[], ctx: BuildContext): BuiltProgressGraph {
  const stagesById = new Map(stages.map((s) => [s.id, s]));
  const layout = layoutLeftToRight(graph.nodes.map((n) => ({ id: n.id, kind: n.kind })), graph.edges.map((e) => ({ id: e.id, from: e.from, to: e.to })));
  const nodes: Node<ProgressNodeData>[] = graph.nodes.map((n) => {
    const data = nodeData(n, stagesById, ctx);
    const pos = layout.positions[n.id] ?? { x: 0, y: 0 };
    return {
      id: n.id, type: data.small ? 'addSign' : 'progress', position: { x: pos.x, y: pos.y }, data,
      draggable: false, selectable: false, connectable: false, focusable: false,
      style: { width: `${data.width}px`, height: `${data.height}px` },
    };
  });
  const outDegree = new Map<string, number>();
  for (const e of graph.edges) outDegree.set(e.from, (outDegree.get(e.from) ?? 0) + 1);
  const edges: Edge<ProgressEdgeData>[] = graph.edges.map((e) => {
    const full = resolveI18n(e.label ?? undefined, ctx.locale);
    const from = layout.positions[e.from] ?? { x: 0, y: 0 };
    const to = layout.positions[e.to] ?? { x: 0, y: 0 };
    return {
      id: e.id, source: e.from, target: e.to, type: 'progress', selectable: false, focusable: false, interactionWidth: 0,
      // 線的資訊由節點的 aria-label 表達；Vue Flow 預設會念「Edge from <內部代號> to <內部代號>」，所以整條線從可及性樹拿掉。
      ariaLabel: null, domAttributes: { 'aria-hidden': 'true' },
      class: e.traversed ? 'pg-edge pg-edge-on' : 'pg-edge pg-edge-off', ...(full ? { label: truncateEdgeLabel(full) } : {}),
      data: { bendX: edgeBendX(from, to, (outDegree.get(e.from) ?? 0) >= 2), fullLabel: full },
    };
  });
  return { nodes, edges, layout, focus: focusPoint(graph, stages, layout) };
}

const REACHED = new Set<string>(['passed', 'returned', 'skipped', 'ended', 'changed']);

/**
 * 視角要對準的節點：最早進入的進行中節點；沒有進行中就是最後走到的節點（被退回或結束的那個）。
 * 進入先後看節點最新一次執行（目前這一次，被退回重跑的節點看重跑那次）的 `seq`（匯合等待沒有執行，排在有執行的後面）；沒有走到的與被取消的不算「走到」。
 */
export function focusNodeId(graph: ApprovalProgressGraph, stages: ApprovalStageView[], layout: LayoutResult): string | null {
  const seqOf = new Map(stages.map((s) => [s.id, s.seq]));
  const active = graph.nodes
    .map((n, i) => ({ n, i, seq: seqOf.get((n.runIds ?? []).at(-1) as number) ?? Infinity }))
    .filter((x) => x.n.state === 'active')
    .sort((a, b) => a.seq - b.seq || a.i - b.i);
  if (active.length) return active[0].n.id;
  const reached = graph.nodes.map((n, i) => ({ n, i, x: layout.positions[n.id]?.x ?? 0 })).filter((x) => REACHED.has(x.n.state));
  if (!reached.length) return null;
  reached.sort((a, b) => b.x - a.x || b.i - a.i);
  return reached[0].n.id;
}

function focusPoint(graph: ApprovalProgressGraph, stages: ApprovalStageView[], layout: LayoutResult): { x: number; y: number } | null {
  const id = focusNodeId(graph, stages, layout);
  const n = graph.nodes.find((x) => x.id === id);
  const pos = id ? layout.positions[id] : undefined;
  if (!n || !pos) return null;
  const small = n.kind === 'addSign';
  return { x: pos.x + (small ? ADDSIGN_W : NODE_W) / 2, y: pos.y + (small ? ADDSIGN_H : NODE_H) / 2 };
}

export interface ViewportPlan {
  zoom: number;
  /** 要放在畫面正中央的圖座標。 */
  center: { x: number; y: number };
  /** 畫布最小縮放：min(0.2, 整張放得進來的比例)，所以「全圖」不論流程多長都縮得到（M4）。 */
  minZoom: number;
  /** 整張放進去的比例（上限 1，不放大）。 */
  fit: number;
}

export interface ViewportOptions { focus?: { x: number; y: number } | null; /** 畫布可用的最大高度；整張放進去的比例也要考慮它。 */ maxHeight?: number }

/**
 * U2、U4：整張放得進寬度而且比例不小於 0.8 就整張顯示；否則固定 0.8 並對準 focus。
 * 縮圖（thumb）一律縮到整張放進 180px 高的框，不受 0.8 限制。
 */
export function initialViewport(layout: Pick<LayoutResult, 'width' | 'height'>, width: number, mode: GraphMode = 'inline', opts: ViewportOptions = {}): ViewportPlan {
  const inner = Math.max(1, width - 2 * GRAPH_PAD);
  const fitW = layout.width > 0 ? inner / layout.width : 1;
  const boxH = mode === 'thumb' ? THUMB_HEIGHT : opts.maxHeight;
  const fitH = boxH !== undefined && layout.height > 0 ? Math.max(1, boxH - 2 * GRAPH_PAD) / layout.height : Infinity;
  const fitAll = Math.min(1, fitW, fitH);
  const minZoom = Math.min(MIN_ZOOM_FLOOR, fitAll);
  const middle = { x: layout.width / 2, y: layout.height / 2 };
  if (mode === 'thumb' || Math.min(1, fitW) >= MIN_READABLE_ZOOM) {
    return { zoom: mode === 'thumb' ? fitAll : Math.min(1, fitW), center: middle, minZoom, fit: fitAll };
  }
  // 放不下：固定 0.8，對準 focus；沒有可對準的節點就從最左邊開始看。
  // 水平方向不讓視角超出圖的左右邊距太多（第一個節點進行中時，從最左邊看起）。
  const zoom = MIN_READABLE_ZOOM;
  const half = width / 2 / zoom;
  const lo = half - GRAPH_PAD / zoom;
  const hi = layout.width + GRAPH_PAD / zoom - half;
  const x = opts.focus ? Math.min(Math.max(opts.focus.x, lo), Math.max(lo, hi)) : lo;
  // 垂直方向：內容放得進框就置中（並行分支不被切掉）；放不進才對準節點。
  const tooTall = opts.maxHeight !== undefined && layout.height * zoom + 2 * GRAPH_PAD > opts.maxHeight;
  return { zoom, center: { x, y: tooTall && opts.focus ? opts.focus.y : middle.y }, minZoom, fit: fitAll };
}

/** 內嵌圖的高度（px）：內容高度 × 初始比例 + 上下邊距，下限 160、上限視窗高的 60%（U2）；縮圖固定 180。 */
export function graphHeight(layout: Pick<LayoutResult, 'width' | 'height'>, width: number, mode: GraphMode, viewportHeight: number): number {
  if (mode === 'thumb') return THUMB_HEIGHT;
  const max = Math.max(GRAPH_MIN_HEIGHT, viewportHeight * GRAPH_MAX_HEIGHT_RATIO);
  const zoom = initialViewport(layout, width, mode).zoom;
  return Math.min(max, Math.max(GRAPH_MIN_HEIGHT, layout.height * zoom + 2 * GRAPH_PAD));
}

/** 把「中心＋比例」換成 Vue Flow 的視角平移（x、y）。 */
export function viewportTransform(plan: ViewportPlan, width: number, height: number): { x: number; y: number; zoom: number } {
  return { x: width / 2 - plan.center.x * plan.zoom, y: height / 2 - plan.center.y * plan.zoom, zoom: plan.zoom };
}
