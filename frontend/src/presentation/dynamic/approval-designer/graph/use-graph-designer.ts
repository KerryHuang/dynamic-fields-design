import { computed, ref, shallowRef } from 'vue';
import { pruneCondition } from 'src/domain/dynamic/condition';
import type { GraphIssueMap } from 'src/domain/dynamic/approval-graph-errors';
import type { ApprovalFlow, ApprovalFlowLayout, ApproverSource, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { moveNode, type EditOptions, type GraphState, type Position } from 'src/domain/dynamic/approval-graph-edit';
import { canRedo, canUndo, clear, createHistory, push, redo, undo, type History } from 'src/domain/dynamic/approval-graph-history';
import type { I18nText } from 'src/domain/dynamic/types';
import { FLOW_SELECTION, approvalNames, conditionLabels, initialState, newFlowState, sourceOf, type Selection } from './designer-graph';

// 設計頁的編輯狀態（J71）：持有一份工作副本，所有修改走 approval-graph-edit 的純函式並進復原堆疊。
// 伺服器狀態（TanStack Query）與工作副本分開；這裡不 import Vue Flow（平移、fitView 由 DesignerCanvas 提供）。

/** 流程層欄位（名稱、目標狀態、代碼）：立即生效、隨儲存草稿或發布一起送（U53），也進復原堆疊。 */
export interface FlowMeta { key: string; name: I18nText; toOptionId: number | null }
export interface Snapshot { graph: GraphState; meta: FlowMeta }

/** 伺服器端的狀態：樂觀鎖基準（`draftRev`、`versionId`）與有沒有草稿、有沒有已發布版本。 */
export interface ServerInfo {
  isNew: boolean;
  key: string;
  /** 回應 `draft != null`；不看 `draftRev`（發布與捨棄後 `draftRev` 仍有值）。 */
  hasDraft: boolean;
  published: boolean;
  versionId: number | null;
  draftRev: string | null;
  /** 已發布版本用過的節點代號：新節點不重用（J72）。 */
  reserved: string[];
  /** 已發布版本裡各簽核節點的簽核人（「沿用舊值」的主管層級判斷用）。 */
  publishedApprovers: Record<string, ApproverSource>;
  /** 被用過的流程不能換目標狀態。 */
  used: boolean;
  priority: number;
  toOptionId: number | null;
}

const blank = (): ServerInfo => ({ isNew: true, key: '', hasDraft: false, published: false, versionId: null, draftRev: null, reserved: [], publishedApprovers: {}, used: false, priority: 1, toOptionId: null });

/** 新節點的預設名稱（U51、U49）：四語各一份。 */
export const editOptions = (reserved: string[]): EditOptions => ({ reserved, names: { approval: approvalNames, conditionLabel: conditionLabels } });

export function useGraphDesigner(canEdit: () => boolean) {
  const history = shallowRef<History<Snapshot> | null>(null);
  const savedJson = ref('');
  const server = ref<ServerInfo>(blank());
  const selection = ref<Selection>(FLOW_SELECTION);
  /** 後端驗證的結果（乾跑、發布或存檔的 400）；null = 還沒驗過，不標錯（U54）。 */
  const issues = shallowRef<GraphIssueMap | null>(null);
  let lastKey: string | null = null;

  const loaded = computed(() => history.value !== null);
  const snapshot = computed<Snapshot | null>(() => history.value?.present ?? null);
  const state = computed<GraphState | null>(() => snapshot.value?.graph ?? null);
  const meta = computed<FlowMeta | null>(() => snapshot.value?.meta ?? null);
  const dirty = computed(() => !!snapshot.value && JSON.stringify(snapshot.value) !== savedJson.value);
  const undoable = computed(() => !!history.value && canUndo(history.value));
  const redoable = computed(() => !!history.value && canRedo(history.value));

  function replaceServer(flow: ApprovalFlow) {
    // 已發布版本的圖：graph 定義本身，或 sequence 版本轉換後的 graph（只有單筆 GET 帶；寫入回應沒有，沿用原本的）
    const pub = flow.definition ? (flow.definition.shape === 'graph' ? flow.definition : flow.graph ?? null) : null;
    const reserved = pub ? pub.nodes.map((n) => n.id) : flow.version != null ? server.value.reserved : [];
    const publishedApprovers: Record<string, ApproverSource> = pub ? Object.fromEntries(pub.nodes.filter((n) => n.approvers).map((n) => [n.id, n.approvers!])) : flow.version != null ? server.value.publishedApprovers : {};
    server.value = {
      isNew: false, used: flow.used, publishedApprovers, key: flow.key, hasDraft: flow.draft != null, published: flow.version != null, versionId: flow.versionId ?? null,
      draftRev: flow.draftRev ?? null, reserved, priority: flow.priority, toOptionId: flow.toOptionId,
    };
  }

  /** 載入：`flow` 為 null 是新流程（起始圖，U51）。選取歸零、復原堆疊清空。 */
  function load(flow: ApprovalFlow | null, target: number | null = null): boolean {
    let graph: GraphState;
    let m: FlowMeta;
    if (!flow) {
      graph = newFlowState();
      m = { key: '', name: {}, toOptionId: target };
      server.value = { ...blank(), toOptionId: target };
    } else {
      const src = sourceOf(flow);
      if (!src) return false;
      graph = initialState(src.definition, src.layout);
      m = { key: flow.key, name: { ...flow.name }, toOptionId: flow.toOptionId };
      replaceServer(flow);
    }
    const snap: Snapshot = { graph, meta: m };
    history.value = createHistory(snap);
    savedJson.value = JSON.stringify(snap);
    selection.value = FLOW_SELECTION;
    issues.value = null;
    lastKey = null;
    return true;
  }

  /** 一次修改一步；同一個 `coalesce` 鍵連續修改（同一個文字欄打字）合成一步，其他動作截斷它（U60）。唯讀時不執行。 */
  function apply(next: (s: Snapshot) => Snapshot, coalesce?: string) {
    const h = history.value;
    if (!h || !canEdit()) return;
    const n = next(h.present);
    if (Object.is(n, h.present)) return;
    history.value = coalesce && coalesce === lastKey ? { ...h, present: n, future: [] } : push(h, n);
    lastKey = coalesce ?? null;
  }
  const edit = (fn: (s: GraphState) => GraphState, coalesce?: string) => apply((s) => { const g = fn(s.graph); return g === s.graph ? s : { ...s, graph: g }; }, coalesce);
  const editMeta = (patch: Partial<FlowMeta>, coalesce?: string) => apply((s) => ({ ...s, meta: { ...s.meta, ...patch } }), coalesce);
  const move = (id: string, to: Position) => edit((s) => moveNode(s, id, to));

  function undoStep() { if (history.value && canEdit()) { history.value = undo(history.value); lastKey = null; } }
  function redoStep() { if (history.value && canEdit()) { history.value = redo(history.value); lastKey = null; } }
  /** 發布或捨棄草稿後清空（U60）；存草稿不清。 */
  function clearHistory() { if (history.value) history.value = clear(history.value); lastKey = null; }
  /** 存檔成功：目前內容就是已存的內容（復原堆疊保留，U60）。 */
  function markSaved() { if (snapshot.value) savedJson.value = JSON.stringify(snapshot.value); lastKey = null; }

  const select = (s: Selection) => { selection.value = s; };
  /** 409 之後「覆蓋」要用的新基準（回應 params 帶的權杖與版本）。 */
  function setTokens(t: { draftRev?: string | null; versionId?: number | null }) {
    server.value = { ...server.value, ...(t.draftRev !== undefined ? { draftRev: t.draftRev } : {}), ...(t.versionId !== undefined ? { versionId: t.versionId } : {}) };
  }

  /** 送出的定義與版面（所見即所得：全部節點的座標都送，J73）；編輯中還沒填完的條件列在這裡剪掉，空條件送 null。 */
  function output(): { definition: GraphDefinition; layout: ApprovalFlowLayout } | null {
    const g = state.value;
    if (!g) return null;
    const d = g.definition;
    const definition: GraphDefinition = {
      ...d, when: pruneCondition(d.when ?? undefined) ?? null,
      nodes: d.nodes.map((n) => (n.when === undefined ? n : { ...n, when: pruneCondition(n.when ?? undefined) ?? null })),
      edges: d.edges.map((e) => (e.when === undefined ? e : { ...e, when: pruneCondition(e.when ?? undefined) ?? null })),
    };
    return { definition, layout: { nodes: Object.fromEntries(Object.entries(g.positions).map(([id, p]) => [id, { x: p.x, y: p.y }])) } };
  }

  return {
    loaded, snapshot, state, meta, dirty, undoable, redoable, server, selection, issues, canEdit: computed(canEdit),
    load, apply, edit, editMeta, move, undo: undoStep, redo: redoStep, clearHistory, markSaved, select, output, replaceServer, setTokens,
  };
}

export type GraphDesigner = ReturnType<typeof useGraphDesigner>;
