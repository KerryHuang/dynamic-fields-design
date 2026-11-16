import type { Edge, Node } from '@vue-flow/core';
import type { ApprovalEdgeDef, ApprovalFlow, ApprovalFlowLayout, ApprovalNodeDef, ApprovalNodeKind, ApproverSource, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { relayout, type GraphState, type Position } from 'src/domain/dynamic/approval-graph-edit';
import type { GraphIssue, GraphIssueMap } from 'src/domain/dynamic/approval-graph-errors';
import { COL_GAP, NODE_H, NODE_W, edgeBendX, manualGapAfter } from 'src/domain/dynamic/approval-graph-layout';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { SchemaField } from 'src/domain/dynamic/types';
import { truncateEdgeLabel } from 'src/presentation/dynamic/approval/progress-graph';
import { conditionSummary } from 'src/presentation/dynamic/rules/condition-summary';
import { tFor, type Translate } from 'src/i18n/translate';

// 工作副本 → Vue Flow 的節點與線（J70、J74、J76）。只 import type Vue Flow，不碰 runtime。

export type Selection = { kind: 'flow' } | { kind: 'node'; id: string } | { kind: 'edge'; id: string };
export const FLOW_SELECTION: Selection = { kind: 'flow' };

/** 手排時標籤寬度上限（U59）。 */
export const MANUAL_LABEL_MAX_PX = 160;

const KIND_ICON: Record<ApprovalNodeKind, string> = {
  start: 'play_arrow', end: 'flag', condition: 'alt_route', fork: 'call_split', join: 'call_merge', notify: 'notifications', approval: 'person',
};

export interface DesignerNodeData {
  kind: ApprovalNodeKind;
  label: string;
  summary: string;
  icon: string;
  width: number;
  height: number;
  /** 簽核節點有略過條件。 */
  skip: boolean;
  errors: number;
  warnings: number;
  selected: boolean;
  ariaLabel: string;
}

export interface DesignerEdgeData {
  bendX: number;
  /** 沒被截斷的完整標籤（滑鼠提示）。 */
  fullLabel: string;
  /** 條件出線還沒設條件時的灰色提示（U49：驗證前不標紅）。 */
  hint: boolean;
  errors: number;
  warnings: number;
  selected: boolean;
}

export interface GraphContext {
  locale: string;
  t: Translate;
  fields: SchemaField[];
  issues: GraphIssueMap | null;
  selection: Selection;
}

export const kindIcon = (k: ApprovalNodeKind) => KIND_ICON[k] ?? 'help_outline';

/** 流程名稱的 i18n 預設：所有語系各一份（新節點「簽核 n」，U51）。 */
export const LOCALES = ['zh-TW', 'en-US', 'th', 'vi'] as const;
export const approvalNames = (n: number) => Object.fromEntries(LOCALES.map((l) => [l, tFor(l)('approval.designer.graph.newApproval', { n })]));
export const conditionLabels = (n: number) => Object.fromEntries(LOCALES.map((l) => [l, tFor(l)('approval.designer.graph.newCondition', { n })]));

/** 載入：座標取版面，沒有座標的節點用由左到右的自動排版補（J71）。 */
export function initialState(def: GraphDefinition, layout?: ApprovalFlowLayout | null): GraphState {
  const saved = layout?.nodes ?? {};
  const positions: Record<string, Position> = {};
  const auto = def.nodes.some((n) => !saved[n.id]) ? relayout({ definition: def, positions: {} }).positions : {};
  for (const n of def.nodes) positions[n.id] = { ...(saved[n.id] ?? auto[n.id] ?? { x: 0, y: 0 }) };
  return { definition: JSON.parse(JSON.stringify(def)) as GraphDefinition, positions };
}

/** 設計頁要編輯的圖：草稿優先；有草稿才算有草稿（`draft != null`，不看 `draftRev`），否則轉換後的圖，再不然已發布的 graph 定義。 */
export function sourceOf(flow: ApprovalFlow): { definition: GraphDefinition; layout: ApprovalFlowLayout | null } | null {
  if (flow.draft) return { definition: flow.draft.definition, layout: flow.draft.layout ?? flow.layout ?? null };
  const def = flow.graph ?? (flow.definition?.shape === 'graph' ? flow.definition : null);
  return def ? { definition: def, layout: flow.layout ?? null } : null;
}

/** 新流程的起始圖（U51）：開始 → 簽核 1（直屬主管第 1 層、或簽）→ 結束。 */
export function newFlowState(): GraphState {
  const definition: GraphDefinition = {
    shape: 'graph', stages: [],
    nodes: [
      { id: 'start-1', kind: 'start' },
      { id: 'approval-1', kind: 'approval', name: approvalNames(1), approvers: { source: 'manager', level: 1 }, mode: 'any', editableFields: [], onEmpty: 'fallback' },
      { id: 'end-1', kind: 'end' },
    ],
    edges: [{ id: 'e-1', from: 'start-1', to: 'approval-1' }, { id: 'e-2', from: 'approval-1', to: 'end-1' }],
  };
  return initialState(definition, null);
}

const count = (list: GraphIssue[] | undefined) => list?.length ?? 0;

export function approverText(a: ApproverSource | null | undefined, t: Translate): string {
  if (!a) return '';
  const kind = t(`approval.designer.source.kind.${a.source}`);
  if (a.source === 'manager') return `${kind} · ${t('approval.designer.source.levelOption', { n: a.level ?? 1 })}`;
  if (a.source === 'users') return a.userIds?.length ? `${kind} · ${t('approval.designer.graph.people', { n: a.userIds.length })}` : kind;
  if (a.source === 'group') return a.groupIds?.length ? `${kind} · ${a.groupIds.length}` : kind;
  return kind;
}

export function nodeLabel(n: ApprovalNodeDef, locale: string, t: Translate): string {
  return resolveI18n(n.name ?? undefined, locale) || t(`approval.graph.kind.${n.kind}`);
}

function summaryOf(n: ApprovalNodeDef, def: GraphDefinition, t: Translate): string {
  switch (n.kind) {
    case 'approval': return [approverText(n.approvers, t), n.mode ? t(n.mode === 'all' ? 'approval.designer.graph.modeAllShort' : 'approval.designer.graph.modeAnyShort') : ''].filter(Boolean).join(' · ');
    case 'join': return n.mode ? t(`approval.graph.joinMode.${n.mode}`) : '';
    case 'condition': return t('approval.designer.graph.outCount', { n: def.edges.filter((e) => e.from === n.id).length });
    case 'notify': return t('approval.designer.graph.notifyNotYet');
    default: return '';
  }
}

/** 線上的文字：有標籤用標籤；條件出線沒標籤時用條件摘要（沒設條件是灰色提示），預設線寫「其他」（§10.3）。 */
export function edgeText(e: ApprovalEdgeDef, def: GraphDefinition, ctx: Pick<GraphContext, 'locale' | 't' | 'fields'>): { text: string; hint: boolean } {
  const label = resolveI18n(e.label ?? undefined, ctx.locale);
  if (label) return { text: label, hint: false };
  const from = def.nodes.find((n) => n.id === e.from);
  if (from?.kind !== 'condition') return { text: '', hint: false };
  if (e.default) return { text: ctx.t('approval.designer.graph.edgeDefault'), hint: false };
  const empty = !e.when || (typeof e.when === 'object' && ('all' in e.when ? e.when.all.length === 0 : 'any' in e.when ? e.when.any.length === 0 : false));
  return empty ? { text: ctx.t('approval.designer.graph.edgeNoCondition'), hint: true } : { text: conditionSummary(e.when, ctx.fields, ctx.t, ctx.locale), hint: false };
}

export function toVueFlow(state: GraphState, ctx: GraphContext, opts: { draggable: boolean }): { nodes: Node<DesignerNodeData>[]; edges: Edge<DesignerEdgeData>[] } {
  const { definition: def, positions } = state;
  const { t, locale, issues, selection } = ctx;
  const nodes: Node<DesignerNodeData>[] = def.nodes.map((n) => {
    const label = nodeLabel(n, locale, t);
    const errors = count(issues?.errors.nodes[n.id]);
    const warnings = count(issues?.warnings.nodes[n.id]);
    const summary = summaryOf(n, def, t);
    const data: DesignerNodeData = {
      kind: n.kind, label, summary, icon: kindIcon(n.kind), width: NODE_W, height: NODE_H, skip: n.kind === 'approval' && !!n.when,
      errors, warnings, selected: selection.kind === 'node' && selection.id === n.id,
      ariaLabel: [label, t(`approval.graph.kind.${n.kind}`), summary, errors ? t('approval.designer.graph.errorCount', { n: errors }) : ''].filter(Boolean).join(' · '),
    };
    const p = positions[n.id] ?? { x: 0, y: 0 };
    return { id: n.id, type: 'designer', position: { x: p.x, y: p.y }, data, draggable: opts.draggable, style: { width: `${NODE_W}px`, height: `${NODE_H}px` } };
  });
  const outs = new Map<string, ApprovalEdgeDef[]>();
  for (const e of def.edges) outs.set(e.from, [...(outs.get(e.from) ?? []), e]);
  const box = (id: string) => ({ x: positions[id]?.x ?? 0 });
  const edges: Edge<DesignerEdgeData>[] = def.edges.map((e) => {
    const siblings = outs.get(e.from) ?? [];
    const branching = siblings.length >= 2;
    const gap = manualGapAfter(box(e.from), siblings.map((s) => box(s.to)));
    const { text, hint } = edgeText(e, def, ctx);
    const maxPx = Math.min(MANUAL_LABEL_MAX_PX, Math.max(COL_GAP, gap) - 8);
    const errors = count(issues?.errors.edges[e.id]);
    const warnings = count(issues?.warnings.edges[e.id]);
    const selected = selection.kind === 'edge' && selection.id === e.id;
    return {
      id: e.id, source: e.from, target: e.to, type: 'designer', ...(text ? { label: truncateEdgeLabel(text, maxPx) } : {}),
      class: ['de-edge', selected && 'de-edge-selected', errors && 'de-edge-error', warnings && 'de-edge-warn', hint && 'de-edge-hint'].filter(Boolean).join(' '),
      data: { bendX: edgeBendX(box(e.from), box(e.to), branching, gap), fullLabel: text, hint, errors, warnings, selected },
    };
  });
  return { nodes, edges };
}
