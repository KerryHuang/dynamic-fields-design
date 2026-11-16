import { computed, onBeforeUnmount, ref, watch } from 'vue';
import {
  useCreateFlowDraft, useDiscardFlowDraft, usePublishFlow, useSaveFlowDraft, useValidateFlow, useDeleteApprovalFlow,
} from 'src/application/dynamic/queries/approval-flow-queries';
import { nextPriority } from 'src/domain/dynamic/approval-flow-draft';
import type { ApprovalFlow, ApprovalFlowDraftInput, ApprovalFlowPublishResult, ApprovalFlowValidation, ApprovalFlowWarning, DraftConflictInfo } from 'src/domain/dynamic/approval-flow-types';
import { mapGraphIssues } from 'src/domain/dynamic/approval-graph-errors';
import type { Translate } from 'src/i18n/translate';
import type { ApiError } from 'src/services/api/http';
import type { GraphDesigner } from './use-graph-designer';

// 設計頁的伺服器動作（Task 10）：存草稿、乾跑驗證、發布、捨棄、刪除，以及 409 衝突與 U54 的即時驗證。
// 結構規則只在後端（J68）；這裡只決定什麼時候問、怎麼把回應對回畫面。

/** 即時驗證的防抖（U54）。 */
export const REVALIDATE_MS = 800;

export type ConflictAction = 'save' | 'publish' | 'discard';
export interface Conflict { action: ConflictAction; info: DraftConflictInfo; message?: string }
export interface PublishInfo { warnings: ApprovalFlowWarning[]; newVersion: boolean; nextVersion?: number | null; pendingCount: number }

export interface ActionContext {
  moduleKey: () => string;
  /** 同模組的流程（新流程與換目標狀態時排優先序）。 */
  flows: () => ApprovalFlow[];
  /** 重抓這條流程（含 graph 與草稿）。 */
  reload: () => Promise<ApprovalFlow | undefined>;
  /** 新流程第一次存成功後：換到 `…/:flowKey`。 */
  onCreated: (key: string) => void;
  onDeleted: () => void;
  notify: (type: 'positive' | 'negative' | 'info', message: string) => void;
  t: Translate;
}

const asApiError = (e: unknown) => e as ApiError;

export function useDesignerActions(designer: GraphDesigner, ctx: ActionContext) {
  const mk = () => ctx.moduleKey();
  const createDraft = useCreateFlowDraft(mk);
  const saveDraftM = useSaveFlowDraft(mk);
  const discardM = useDiscardFlowDraft(mk);
  const publishM = usePublishFlow(mk);
  const validateM = useValidateFlow(mk);
  const deleteM = useDeleteApprovalFlow(mk);

  const busy = computed(() => [createDraft, saveDraftM, discardM, publishM, deleteM].some((m) => m.isPending.value));
  /** 按過一次「驗證」或「發布」之後才即時標錯（U54）。 */
  const validated = ref(false);
  const issuesOpen = ref(false);
  const conflict = ref<Conflict | null>(null);
  const publishInfo = ref<PublishInfo | null>(null);
  let seq = 0;

  const errorCount = computed(() => {
    const e = designer.issues.value?.errors;
    return e ? Object.values(e.nodes).flat().length + Object.values(e.edges).flat().length + e.flow.length : 0;
  });

  function setFlowIssue(errors: Record<string, string[]>) {
    const def = designer.state.value?.definition;
    if (!def) return;
    designer.issues.value = mapGraphIssues(errors, [], def);
    designer.select({ kind: 'flow' });
    issuesOpen.value = true;
  }

  /** 送出的內容；目標狀態沒選就不送（回 null，標在流程層）。優先序：目標狀態沒變用目前的，換了排到那個目標狀態的最後。 */
  function buildInput(force = false): { input: ApprovalFlowDraftInput; sent: NonNullable<ReturnType<GraphDesigner['output']>>; sentJson: string } | null {
    const out = designer.output();
    const m = designer.meta.value;
    if (!out || !m) return null;
    if (m.toOptionId == null) { setFlowIssue({ toOptionId: [ctx.t('approval.designer.basic.targetRequired')] }); return null; }
    const s = designer.server.value;
    const priority = s.isNew ? nextPriority(ctx.flows(), m.toOptionId)
      : m.toOptionId === s.toOptionId ? s.priority : nextPriority(ctx.flows(), m.toOptionId, s.key);
    const input: ApprovalFlowDraftInput = {
      key: s.isNew ? m.key : s.key, name: m.name, toOptionId: m.toOptionId, priority, definition: out.definition, layout: out.layout,
      baseRev: s.draftRev, baseVersionId: s.versionId, ...(force ? { force: true } : {}),
    };
    return { input, sent: out, sentJson: designer.snapshotJson() };
  }

  /** 400 的 errors 與驗證回應用同一套對照（J77），以送出的那一份定義對回節點與線。 */
  function showErrors(e: ApiError, sent: { definition: ApprovalFlowDraftInput['definition'] }) {
    designer.issues.value = mapGraphIssues(e.errors, [], sent.definition);
    issuesOpen.value = true;
  }

  /** 處理失敗：409 草稿衝突開對話框、409 代碼重複標在流程層、400 展開清單，其餘跳通知。 */
  function fail(err: unknown, action: ConflictAction, sent?: { definition: ApprovalFlowDraftInput['definition'] }): void {
    const e = asApiError(err);
    if (e.status === 409 && e.code === 'DraftConflict') { conflict.value = { action, info: (e.params ?? {}) as DraftConflictInfo, message: e.message }; return; }
    if (e.status === 409 && e.code === 'FlowKeyTaken') { setFlowIssue({ key: [e.message ?? ctx.t('approval.designer.graph.keyTaken')] }); return; }
    if (e.status === 400 && e.errors && sent) { showErrors(e, sent); return; }
    ctx.notify('negative', e.message ?? ctx.t('designer.saveFailed'));
  }

  /** 乾跑驗證；回應序號過期（又有新的請求）就丟掉。`quiet` 不開錯誤清單（即時驗證）。 */
  async function validate(quiet = false): Promise<ApprovalFlowValidation | null> {
    const built = buildInput();
    if (!built) return null;
    const mine = ++seq;
    try {
      const v = await validateM.mutateAsync({ key: designer.server.value.isNew ? null : designer.server.value.key, input: built.input });
      if (mine !== seq) return null;
      designer.issues.value = mapGraphIssues(v.errors, v.warnings, built.sent.definition);
      if (!quiet && (Object.keys(v.errors).length || v.warnings.length)) issuesOpen.value = true;
      return v;
    } catch (err) {
      if (mine !== seq) return null;
      if (!quiet) fail(err, 'save', built.sent);
      return null;
    }
  }

  /** 按「驗證」。 */
  async function validateNow() {
    validated.value = true;
    const v = await validate();
    if (v && !Object.keys(v.errors).length && !v.warnings.length) ctx.notify('positive', ctx.t('approval.designer.graph.validateOk'));
  }

  // ---- U54：按過驗證或發布後，每次修改停 0.8 秒重新驗證 ----
  let timer: ReturnType<typeof setTimeout> | undefined;
  watch(() => designer.snapshot.value, () => {
    if (!validated.value || !designer.canEdit.value) return;
    clearTimeout(timer);
    timer = setTimeout(() => { void validate(true); }, REVALIDATE_MS);
  });
  onBeforeUnmount(() => { clearTimeout(timer); seq++; });

  // ---- 存草稿 ----
  /** 回傳存好的流程；失敗回 undefined。 */
  async function saveDraft(opts: { force?: boolean } = {}): Promise<ApprovalFlow | undefined> {
    const built = buildInput(opts.force);
    if (!built || !designer.canEdit.value) return undefined;
    const s = designer.server.value;
    try {
      const flow = (s.isNew ? await createDraft.mutateAsync(built.input) : await saveDraftM.mutateAsync({ key: s.key, input: built.input })) as ApprovalFlow;
      designer.replaceServer(flow);
      designer.markSaved(built.sentJson); // 請求期間的修改仍算未存
      if (s.isNew) ctx.onCreated(flow.key);
      ctx.notify('positive', ctx.t('approval.designer.graph.draftSaved'));
      return flow;
    } catch (err) {
      fail(err, 'save', built.sent);
      return undefined;
    }
  }

  // ---- 發布（U55）：先乾跑驗證；有錯展開清單，沒錯開確認對話框 ----
  async function requestPublish() {
    if (!designer.canEdit.value) return;
    validated.value = true;
    const v = await validate();
    if (!v || Object.keys(v.errors).length) return;
    publishInfo.value = { warnings: v.warnings, newVersion: v.newVersion, nextVersion: v.nextVersion, pendingCount: v.pendingCount };
  }

  async function confirmPublish(opts: { force?: boolean } = {}) {
    const wasNew = designer.server.value.isNew;
    publishInfo.value = null;
    if (wasNew) { // 新流程：先建列（POST draft）、換網址，再發布
      const created = await saveDraft();
      if (!created) return;
    }
    const built = buildInput(opts.force);
    if (!built) return;
    try {
      const r = (await publishM.mutateAsync({ key: designer.server.value.key, input: built.input })) as ApprovalFlowPublishResult;
      designer.replaceServer(r.flow);
      designer.markSaved(built.sentJson);
      designer.clearHistory();
      ctx.notify('positive', ctx.t('approval.designer.graph.published'));
    } catch (err) {
      const e = asApiError(err);
      fail(err, 'publish', built.sent);
      if (wasNew && !(e.status === 409 && e.code === 'DraftConflict')) ctx.notify('info', ctx.t('approval.designer.graph.firstPublishFailed'));
    }
  }

  // ---- 捨棄草稿、刪除流程（只有草稿的流程用刪除，U64）----
  async function discard(opts: { force?: boolean } = {}) {
    const s = designer.server.value;
    if (!designer.canEdit.value || s.isNew || !s.published) return;
    try {
      await discardM.mutateAsync({ key: s.key, baseRev: s.draftRev, ...(opts.force ? { force: true } : {}) });
      const fresh = await ctx.reload();
      if (fresh) designer.load(fresh);
      designer.clearHistory();
      ctx.notify('positive', ctx.t('approval.designer.graph.discarded'));
    } catch (err) { fail(err, 'discard'); }
  }

  async function deleteFlow() {
    const s = designer.server.value;
    if (!designer.canEdit.value || s.isNew || s.published) return;
    try {
      await deleteM.mutateAsync(s.key);
      designer.markSaved(); // 離開不再問未儲存
      ctx.notify('positive', ctx.t('approval.designer.list.deleted'));
      ctx.onDeleted();
    } catch (err) { fail(err, 'discard'); }
  }

  // ---- 409：覆蓋（用回應給的權杖與版本、force）、重新載入（放棄我的修改）、取消 ----
  async function resolveConflict(choice: 'overwrite' | 'reload' | 'cancel') {
    const c = conflict.value;
    conflict.value = null;
    if (!c || choice === 'cancel') return;
    if (choice === 'reload') {
      const fresh = await ctx.reload();
      if (fresh) designer.load(fresh);
      return;
    }
    designer.setTokens({ draftRev: c.info.draftRev ?? null, versionId: c.info.versionId ?? designer.server.value.versionId });
    if (c.action === 'save') await saveDraft({ force: true });
    else if (c.action === 'publish') await confirmPublish({ force: true });
    else await discard({ force: true });
  }

  return {
    busy, validated, issuesOpen, conflict, publishInfo, errorCount,
    validate: validateNow, saveDraft, requestPublish, confirmPublish, discard, deleteFlow, resolveConflict,
    cancelPublish: () => { publishInfo.value = null; },
  };
}

export type DesignerActions = ReturnType<typeof useDesignerActions>;
