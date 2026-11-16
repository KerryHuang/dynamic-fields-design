import type { ApprovalFlowWarning, GraphDefinition } from './approval-flow-types';

// 後端回的錯誤與警告鍵（J77）對到畫布上的節點、線與流程層。乾跑驗證與發布的 400 共用。
export interface GraphIssue {
  /** 原始鍵（含 `definition.` 前綴）。 */
  key: string;
  /** 節點／線底下的欄位（`approvers`、`when`…），整個節點或線時為 null；流程層是去掉 `definition.` 的鍵。 */
  field: string | null;
  message: string;
  /** 警告才有（W1–W3）。 */
  code?: string;
}

export interface MappedIssues {
  nodes: Record<string, GraphIssue[]>;
  edges: Record<string, GraphIssue[]>;
  flow: GraphIssue[];
}

export interface GraphIssueMap { errors: MappedIssues; warnings: MappedIssues }

const empty = (): MappedIssues => ({ nodes: {}, edges: {}, flow: [] });
// definition.nodes.<id>[.field]、definition.nodes[<i>][.field]；代號不含 . 與 [
const BY_ID = /^definition\.(nodes|edges)\.([^.[\]]+)(?:\.(.+))?$/;
const BY_INDEX = /^definition\.(nodes|edges)\[(\d+)\](?:\.(.+))?$/;

function place(into: MappedIssues, key: string, message: string, sent: GraphDefinition, code?: string) {
  const issue = (field: string | null): GraphIssue => ({ key, field, message, ...(code ? { code } : {}) });
  const id = BY_ID.exec(key);
  if (id) {
    const [, kind, target, field] = id;
    const list = kind === 'nodes' ? sent.nodes : sent.edges;
    if (list.some((x) => x.id === target)) {
      const bucket = kind === 'nodes' ? into.nodes : into.edges;
      (bucket[target] ??= []).push(issue(field ?? null));
      return;
    }
  }
  const idx = BY_INDEX.exec(key);
  if (idx) {
    const [, kind, i, field] = idx;
    const target = (kind === 'nodes' ? sent.nodes : sent.edges)[Number(i)]?.id;
    if (target !== undefined) {
      const bucket = kind === 'nodes' ? into.nodes : into.edges;
      (bucket[target] ??= []).push(issue(field ?? null));
      return;
    }
  }
  into.flow.push(issue(key.replace(/^definition\./, '')));
}

/**
 * `sent` 是送出的那一份定義：以索引為鍵的錯誤依它的陣列對回節點與線；對不到節點與線的鍵（流程層欄位、`definition.graph`、`definition.when` 等）歸到 flow。
 */
export function mapGraphIssues(
  errors: Record<string, string[]> | undefined, warnings: ApprovalFlowWarning[] | undefined, sent: GraphDefinition,
): GraphIssueMap {
  const out: GraphIssueMap = { errors: empty(), warnings: empty() };
  for (const [key, messages] of Object.entries(errors ?? {})) for (const m of messages) place(out.errors, key, m, sent);
  for (const w of warnings ?? []) place(out.warnings, w.key, w.message, sent, w.code);
  return out;
}
