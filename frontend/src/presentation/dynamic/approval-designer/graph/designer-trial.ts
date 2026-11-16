import type { GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { ApprovalPreview } from 'src/domain/dynamic/approval-types';

// 試算結果（J80）對回畫布：`path.nodeIds`／`path.edgeIds` 是預計會走到的，`stages`（碼 = 節點代號）補上簽核人與問題。純函式，不碰 Vue Flow。

/** path：預計會走到；skipped：在路徑上但不需簽核（略過條件不成立或沒有人）；undecided：停在這個條件節點，依條件決定；off：不會走到。 */
export type NodeTrialState = 'path' | 'skipped' | 'undecided' | 'off';
export type EdgeTrialState = 'path' | 'off';

export interface NodeTrial {
  state: NodeTrialState;
  /** 簽核人顯示名稱（簽核節點，略過、自選時為空）。 */
  approvers: string[];
  /** 簽核人是發起人送出時選的。 */
  pick: boolean;
  /** 用了備援簽核人。 */
  fallback: boolean;
  problem: 'noApprover' | 'tooManyApprovers' | null;
  /** 並行分支裡「任一分支完成即可」的匯合代號（U10）。 */
  anyGroup: string | null;
}

export interface TrialOverlay { nodes: Record<string, NodeTrial>; edges: Record<string, EdgeTrialState> }

/**
 * 只在這次結果真的是這張圖的路徑時才疊：命中的流程是正在編輯的那條（`flowKey`）、有走到流程（不是擋下或直接轉移）、回應帶 `path`。
 * 否則回 null：畫布維持原樣，結果只列在清單。
 */
export function trialOverlay(def: GraphDefinition, result: ApprovalPreview | null, flowKey: string): TrialOverlay | null {
  if (!result || result.outcome !== 'flow' || result.flowKey !== flowKey || !result.path) return null;
  const onPath = new Set(result.path.nodeIds);
  const onEdge = new Set(result.path.edgeIds);
  const stages = new Map(result.stages.flatMap((s) => (s.code ? [[s.code, s] as const] : [])));
  const nodes: Record<string, NodeTrial> = {};
  for (const n of def.nodes) {
    const s = stages.get(n.id);
    const state: NodeTrialState = !onPath.has(n.id) ? 'off' : s?.status === 'skip' ? 'skipped' : s?.status === 'conditional' ? 'undecided' : 'path';
    nodes[n.id] = {
      state, approvers: s?.status === 'enter' ? s.approvers.map((a) => a.displayName) : [], pick: s?.status === 'pick', fallback: !!s?.usesFallback,
      problem: s?.problem ?? null, anyGroup: s?.anyGroup ?? null,
    };
  }
  return { nodes, edges: Object.fromEntries(def.edges.map((e) => [e.id, onEdge.has(e.id) ? 'path' : 'off'])) };
}
