import type {
  AdminGroup, AdminUser, AttachmentSettings, PrintTemplateDefinition, PrintTemplateOption, UploadedAttachment, DeleteCheck, DeleteTargetRef, AdminUserCreate, AdminUserUpdate, AuthOptions, AuthUser, ConditionNode, ConvertBody, Conversion, DesignerField, DetailRowPayload, DynamicRecord, EntitySummary, FieldConfig, FieldType, Group, GroupImpact, I18nText, LightPage, ListView, Me, MenuItem, ModuleDetail, ModulePermissions, ModuleSchema, RecordConversion,
  ImportPreview, ImportResult, PagedResponse, RecordHistoryEntry, RelatedTab, Rule, RollupSource, SystemPrivileges, User, ViewSort, Workflow, WorkflowResponse,
} from 'src/domain/dynamic/types';
import type {
  AdminDashboardDetail, AdminDashboardItem, CardResult, DashboardCard, DashboardDetail, DashboardLayout, DashboardList,
} from 'src/domain/dynamic/dashboard-types';
import type {
  CatalogModule, DrillResult, ModuleCatalog, ReportDatasetItem, ReportDateRange, ReportDefinition, ReportDetail, ReportItem, ReportResult, ReportRunFilter, ReportShareTarget,
} from 'src/domain/dynamic/report-types';
import type { MeTheme, ThemeChoice, ThemeDefaults } from 'src/domain/theme/theme-model';
import type { Department, DepartmentInput } from 'src/domain/dynamic/organization';
import type { CheckDefinition, LedgerBody, LedgerConfig, ModuleCheck, ModuleEvent, RecordEffects, RecordLedger } from 'src/domain/dynamic/events-ledger';
import type {
  NotificationList, RecordTasks, TaskCapabilities, TaskComment, TaskCounts, TaskDetail, TaskInput, TaskListFilter, TaskListTab, TaskPage, TaskPatch,
  TaskUpdateResult,
} from 'src/domain/dynamic/task-types';
import type {
  ApprovalActionResult, ApprovalBox, ApprovalInboxFilter, ApprovalListFilter, ApprovalInboxItem, ApprovalPreview, ApproveBody, RecordApproval, RejectBody,
  SubmitApprovalBody, SubmitApprovalResult, TransferBody, WithdrawBody, CancelBody, ReassignBody, AddSignBody, ApprovalDelegate, ApprovalDelegateBody, MyApprovalDelegate,
} from 'src/domain/dynamic/approval-types';
import type { ApprovalDraftPreviewBody, ApprovalFlow, ApprovalFlowDraftInput, ApprovalFlowInput, ApprovalFlowPublishResult, ApprovalFlowValidation } from 'src/domain/dynamic/approval-flow-types';
import type {
  BoardColumnPage, BoardSource, GanttSource, TaskBoardConfig, TaskBoardData, TaskGanttData, TaskMoveBody, TaskMoveResult, TaskScheduleBody,
  TaskScheduleResult, TaskViewConfig, TaskViewDetail, TaskViewKind, TaskViewList, TaskViewShareKind, TaskViewShareTargets,
} from 'src/domain/dynamic/task-board-types';
import type { TaskRule, TaskRuleInput } from 'src/domain/dynamic/task-rule-draft';
import type { ApplyLocalePackResult, LocaleAdminList, LocalePackReportData } from 'src/domain/dynamic/locale-pack-types';
import type { EffectivePolicies, ModulePolicy, PolicyValue, SystemPolicy } from 'src/domain/dynamic/policy-types';
import type {
  AuditAction, AuditDetail, AuditFilter, AuditList, HealthCheckConfig, HealthCheckState, HealthCheckUpdate, HealthResults, IncidentDetail, IncidentFilter,
  IncidentList, IncidentSummary, MailSettings, MailSettingsInput, MailTestResult, OpsOverview, SmtpChannel, SmtpChannelInput,
} from 'src/domain/ops/ops-types';
import { handleAuthFailure, http, toApiError } from './http';
import {
  normalizeBoard, normalizeBoardPage, normalizeGantt, normalizeMoveResult, normalizeScheduleResult, normalizeShareTargets, normalizeViewDetail,
  normalizeViewList, normalizeNotificationList, normalizeRecordTasks, normalizeTaskCapabilities, normalizeTaskComment, normalizeTaskDetail, normalizeTaskPage,
  normalizeTaskRule, normalizeTaskUpdate,
} from './task-normalize';

/** Request config for write calls: the key makes a retried request take effect once (server: Dynamic_Operation). */
const idem = (key?: string) => (key ? { headers: { 'Idempotency-Key': key } } : undefined);

const deletePath = (t: DeleteTargetRef) =>
  t.kind === 'module' ? `/modules/${encodeURIComponent(t.key)}` : `/${t.kind}s/${t.id}`;

export interface ModuleSummary {
  id: number;
  key: string;
  programCode: string;
  name: I18nText;
  icon?: string;
  section?: I18nText;
  titleFieldId?: number;
  status: string;
  version: number;
  kind?: 'standalone' | 'extension';
  hostEntityKey?: string;
  detailOf?: string;
}

export interface TemplateFieldSummary {
  code: string;
  label: I18nText;
  type: string;
  locked: boolean;
  /** Qualified `<module>.<field>`. */
  dependsOn: string[];
}
export interface TemplateModuleSummary {
  code: string;
  key: string;
  name: I18nText;
  detailOf?: string;
  fields: TemplateFieldSummary[];
}
export interface TemplateSummary {
  key: string;
  name: I18nText;
  description?: I18nText;
  source: 'builtIn' | 'imported';
  modules: TemplateModuleSummary[];
}
export interface AppRequirement { app: string; modules: string[]; entities: string[] }
export interface AppPackageSummary {
  id: string;
  version: string;
  name: I18nText;
  description?: I18nText;
  icon?: string;
  vendor?: string;
  source: 'builtIn' | 'imported';
  requires: { apps: AppRequirement[] };
  groups: { code: string; name: I18nText }[];
  modules: TemplateModuleSummary[];
  /** Status of the opened app made from this package; null when never opened. */
  status: 'Open' | 'Closed' | null;
  /** Import only: re-importing a shared master merges into it; warnings list what was kept, renamed or not merged. */
  merged?: boolean;
  warnings?: string[];
}
export interface AppModuleInfo { code: string; key: string; id: number; name: I18nText; status: string }
export interface AppInfo {
  key: string;
  name: I18nText;
  icon?: string;
  packageVersion: string;
  status: 'Open' | 'Closed';
  rowVersion: string;
  modules: AppModuleInfo[];
  /** Open apps that depend on it. */
  dependents: string[];
  /** Self-built modules whose fields reference it (warning only). */
  unaffiliatedReferrers: string[];
  warnings: string[];
  /** Open response only: approval sources replaced because their group was skipped (also listed in warnings). */
  approvalSubstitutions?: ApprovalSubstitution[];
}
/** A skipped group in an approval flow, now covered by the flow's fallback approvers or the policy default. */
export interface ApprovalSubstitution {
  flow: string;
  flowName: I18nText;
  /** Null when the skipped group was in the flow's fallback approvers. */
  stage?: string | null;
  stageName?: I18nText | null;
  group: string;
  groupName: I18nText;
  kind: 'approvers' | 'candidates' | 'fallback';
  replacedBy: 'flowFallback' | 'policyDefault';
}
export interface CloseAppResult { app: AppInfo; closed: string[]; warnings: string[] }
export interface MissingDependency { kind: string; appId?: string; moduleCode?: string; entity?: string; reason: string }
export interface OpenPreview {
  errors: string[];
  keyConflicts: { moduleCode: string; key: string }[];
  hostEntityConflicts: string[];
  missingDependencies: MissingDependency[];
  groups: GroupSuggestion[];
  warnings: string[];
  canOpen: boolean;
  /** Shared masters handled along with it, in dependency order; only `open` ones bring groups to map. */
  masters?: { id: string; name: I18nText; action: 'open' | 'reopen' | 'bind'; groups: GroupSuggestion[] }[];
  /** Master symbols the package uses that the master no longer has (blocks opening). */
  unbound?: string[];
  /** Approval sources whose group would be skipped and replaced by fallback approvers (warning only). */
  approvalSubstitutions?: ApprovalSubstitution[];
}
export interface GroupSuggestion { code: string; name: I18nText; suggestion: 'map' | 'create' | 'skip'; suggestedGroupId?: number }
export type GroupMap = Record<string, { mode: 'map' | 'create' | 'skip'; groupId?: number }>;
export interface OpenAppBody {
  moduleKeys?: Record<string, string>;
  excludeFields?: string[];
  groupMap?: GroupMap;
  /** Group mapping for each master opened along with it (master id → its group mapping). */
  masters?: Record<string, GroupMap>;
  locale?: string;
}

export interface InstantiateBody {
  modules: { code: string; key?: string; name?: I18nText }[];
  excludeFields: string[];
}
export interface InstantiateResult {
  modules: { code: string; key: string; id: number }[];
  warnings: string[];
}

export interface DemoDataStatus {
  state: 'notGenerated' | 'running' | 'generated' | 'conflict' | 'failed';
  conflicts: string[];
  message?: string;
  startedAt?: string;
  finishedAt?: string;
  done: number;
  total: number;
}

export interface PagedRecordsQuery {
  currentPage: number;
  pagesSize: number;
  /** Serialized ConditionNode (see serializeFilter). */
  filter?: string;
  sort?: ViewSort[];
  q?: string;
  /** System filter of the list (approval design §13.4.1): has an open approval instance / has none. Absent = no filter. */
  approval?: ApprovalListFilter;
}

export interface ViewBody {
  name: I18nText;
  columns: number[];
  filter?: ConditionNode | null;
  sort: ViewSort[];
  isDefault: boolean;
}

export type ExportQuery = Omit<PagedRecordsQuery, 'currentPage' | 'pagesSize'>;

/** Content-Disposition file name; filename* (RFC 5987) wins over the plain one. */
function dispositionFileName(header: string | undefined): string | undefined {
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(header ?? '')?.[1];
  if (star) {
    try { return decodeURIComponent(star.trim()); } catch { /* fall through to the plain name */ }
  }
  return /filename\s*=\s*"?([^";]+)"?/.exec(header ?? '')?.[1]?.trim();
}

const importForm = (file: File) => {
  const body = new FormData();
  body.append('file', file);
  return body;
};

export type RecordValues = Record<string, unknown>;
export type DetailsPayload = Record<string, DetailRowPayload[]>;
const data = <T>(r: { data: T }) => r.data;

/** 部門篩選只在有選部門時送；includeSubDepartments 跟著部門一起送。 */
const departmentBody = (f?: ReportRunFilter) =>
  f && f.departmentIds.length ? { departmentIds: f.departmentIds, includeSubDepartments: f.includeSubDepartments } : {};
/** 匯出是 GET：期間用 rangeKind、rangeN，部門用重複的 departmentIds（paramsSerializer indexes: null）。 */
const exportParams = (f?: ReportRunFilter) => ({
  ...(f?.range ? { rangeKind: f.range.kind, ...(f.range.n != null ? { rangeN: f.range.n } : {}) } : {}),
  ...(f && f.departmentIds.length ? { departmentIds: f.departmentIds, includeSubDepartments: f.includeSubDepartments } : {}),
});

const boardSourceBody = (s: BoardSource) => ({ viewId: s.viewId ?? null, config: s.config ?? null });
/** The server omits null properties; roots and vacant managers come back without parentId / managerUserId. */
const withNulls = (d: Department): Department => ({ ...d, parentId: d.parentId ?? null, managerUserId: d.managerUserId ?? null });

/** One function per backend endpoint; names are the Task 10 Request names without the `Request` suffix. */
export const dynamicApi = {
  getModules: () => http.get<ModuleSummary[]>('/modules').then(data),
  getModule: (key: string) => http.get<ModuleDetail>(`/modules/${key}`).then(data),
  createModule: (body: { key: string; name: I18nText; icon?: string; section?: I18nText; kind?: 'standalone' | 'extension'; hostEntityKey?: string }) =>
    http.post<ModuleSummary>('/modules', body).then(data),
  updateModule: (key: string, body: { name: I18nText; icon?: string; section?: I18nText; titleFieldId?: number; titleTemplate?: string; tasksEnabled?: boolean }) =>
    http.put<ModuleSummary>(`/modules/${key}`, body).then(data),
  activateModule: (key: string) => http.post<void>(`/modules/${key}/activate`).then(data),
  deactivateModule: (key: string) => http.post<void>(`/modules/${key}/deactivate`).then(data),

  deleteCheck: (t: DeleteTargetRef) => http.get<DeleteCheck>(`${deletePath(t)}/delete-check`).then(data),
  /** purged：預檢回報、使用者已確認的軟刪除記錄筆數；與交易內重算的不符時後端回 409。 */
  deleteTarget: (t: DeleteTargetRef, purged: number) => http.delete(deletePath(t), { params: { purged } }).then(data),
  addBlock: (key: string, label: I18nText) => http.post(`/modules/${key}/blocks`, { label }).then(data),
  updateBlock: (id: number, body: { label: I18nText; collapsed: boolean; columns: number }) => http.put(`/blocks/${id}`, body).then(data),
  addField: (key: string, body: { blockId: number; type: FieldType; label: I18nText; config: FieldConfig }) =>
    http.post<DesignerField>(`/modules/${key}/fields`, body).then(data),
  updateField: (id: number, body: { type: FieldType; label: I18nText; config: FieldConfig; blockId: number }) =>
    http.put<DesignerField>(`/fields/${id}`, body).then(data),
  setFieldStatus: (id: number, status: 'active' | 'inactive') => http.put(`/fields/${id}/status`, { status }).then(data),
  reorderFields: (blockId: number, fieldIds: number[]) => http.put(`/blocks/${blockId}/field-order`, { fieldIds }).then(data),
  addOption: (fieldId: number, body: { label: I18nText; color?: string }) =>
    http.post(`/fields/${fieldId}/options`, body).then(data),
  updateOption: (id: number, body: { label: I18nText; color?: string; active: boolean }) =>
    http.put(`/options/${id}`, body).then(data),

  rollupSources: (key: string) => http.get<RollupSource[]>(`/modules/${key}/rollup-sources`).then(data),
  /** `recordId` asks for the temporary read path of an open approval task; the server only uses it when plain Read is missing. */
  getModuleSchema: (key: string, recordId?: number) =>
    http.get<ModuleSchema>(`/modules/${key}/schema`, recordId === undefined ? undefined : { params: { recordId } }).then(data),
  getPreviewSchema: (key: string) => http.get<ModuleSchema>(`/modules/${key}/schema/preview`).then(data),
  getUsers: () => http.get<User[]>('/users').then(data),
  getGroups: () => http.get<Group[]>('/groups').then(data),
  getAdminUsers: () => http.get<AdminUser[]>('/admin/users').then(data),
  createUser: (body: AdminUserCreate) => http.post('/admin/users', body).then(data),
  updateUser: (id: string, body: AdminUserUpdate) => http.put(`/admin/users/${encodeURIComponent(id)}`, body).then(data),
  setUserSystemPrivileges: (id: string, body: SystemPrivileges) =>
    http.put<SystemPrivileges>(`/admin/users/${encodeURIComponent(id)}/system-privileges`, body).then(data),
  resetUserPassword: (id: string, password: string) =>
    http.post(`/admin/users/${encodeURIComponent(id)}/reset-password`, { password }).then(data),
  setUserActive: (id: string, active: boolean) =>
    http.post(`/admin/users/${encodeURIComponent(id)}/${active ? 'activate' : 'deactivate'}`).then(data),
  getAdminGroups: () => http.get<AdminGroup[]>('/admin/groups').then(data),
  createGroup: (body: { name: string; locale: string }) => http.post<AdminGroup>('/admin/groups', body).then(data),
  renameGroup: (id: number, body: { name: string; locale: string }) => http.put<AdminGroup>(`/admin/groups/${id}`, body).then(data),
  setGroupMembers: (id: number, body: { userIds: string[] }) => http.put(`/admin/groups/${id}/members`, body).then(data),
  setGroupSystemPrivileges: (id: number, body: SystemPrivileges) =>
    http.put<SystemPrivileges>(`/admin/groups/${id}/system-privileges`, body).then(data),
  getGroupImpact: (id: number) => http.get<GroupImpact>(`/admin/groups/${id}/impact`).then(data),
  deleteGroup: (id: number) => http.delete(`/admin/groups/${id}`).then(data),
  getDepartments: () => http.get<Department[]>('/departments').then(data).then((list) => list.map(withNulls)),
  createDepartment: (body: DepartmentInput) => http.post<Department>('/departments', body).then(data).then(withNulls),
  updateDepartment: (id: number, body: DepartmentInput) => http.put<Department>(`/departments/${id}`, body).then(data).then(withNulls),
  deleteDepartment: (id: number) => http.delete(`/departments/${id}`).then(data),
  setUserDepartment: (userId: string, departmentId: number | null) =>
    http.put(`/admin/users/${encodeURIComponent(userId)}/department`, { departmentId }).then(data),
  getModulePermissions: (key: string) => http.get<ModulePermissions>(`/modules/${key}/permissions`).then(data),
  saveModulePermissions: (key: string, body: ModulePermissions) =>
    http.put<ModulePermissions>(`/modules/${key}/permissions`, body).then(data),
  getRules: (key: string) => http.get<Rule[]>(`/modules/${key}/rules`).then(data),
  putRules: (key: string, rules: Rule[]) => http.put<Rule[]>(`/modules/${key}/rules`, { rules }).then(data),
  getWorkflow: (key: string) => http.get<WorkflowResponse>(`/modules/${key}/workflow`).then(data),
  putWorkflow: (key: string, body: Workflow) => http.put<WorkflowResponse>(`/modules/${key}/workflow`, body).then(data),
  deleteWorkflow: (key: string) => http.delete(`/modules/${key}/workflow`).then(data),
  getApprovalFlows: (module: string) => http.get<ApprovalFlow[]>('/approval-flows', { params: { module } }).then(data),
  getApprovalFlow: (key: string) => http.get<ApprovalFlow>(`/approval-flows/${encodeURIComponent(key)}`).then(data),
  createApprovalFlow: (module: string, body: ApprovalFlowInput) => http.post<ApprovalFlow>('/approval-flows', body, { params: { module } }).then(data),
  updateApprovalFlow: (key: string, body: ApprovalFlowInput) => http.put<ApprovalFlow>(`/approval-flows/${encodeURIComponent(key)}`, body).then(data),
  deleteApprovalFlow: (key: string) => http.delete(`/approval-flows/${encodeURIComponent(key)}`).then(data),
  /** Draft-only flow (no version yet): the row is created disabled and the body is kept as the draft. */
  createApprovalFlowDraft: (module: string, body: ApprovalFlowDraftInput) =>
    http.post<ApprovalFlow>('/approval-flows/draft', body, { params: { module } }).then(data),
  /** Optimistic lock: the body carries `baseRev` and `baseVersionId` as loaded; 409 `DraftConflict` when they are stale, `force` overwrites. */
  saveApprovalFlowDraft: (key: string, body: ApprovalFlowDraftInput) =>
    http.put<ApprovalFlow>(`/approval-flows/${encodeURIComponent(key)}/draft`, body).then(data),
  discardApprovalFlowDraft: (key: string, baseRev: string | null, force = false) =>
    http.delete<ApprovalFlow>(`/approval-flows/${encodeURIComponent(key)}/draft`, { params: { baseRev: baseRev ?? undefined, force: force || undefined } }).then(data),
  publishApprovalFlow: (key: string, body: ApprovalFlowDraftInput) =>
    http.post<ApprovalFlowPublishResult>(`/approval-flows/${encodeURIComponent(key)}/publish`, body).then(data),
  /** Dry run (nothing is written): `key` is only passed for an existing flow, which makes its current version the "tightened limits only apply later" baseline. */
  validateApprovalFlow: (module: string, key: string | null, body: ApprovalFlowDraftInput) =>
    http.post<ApprovalFlowValidation>('/approval-flows/validate', body, { params: { module, key: key ?? undefined } }).then(data),
  previewApprovalDraft: (module: string, body: ApprovalDraftPreviewBody) =>
    http.post<ApprovalPreview>('/approval-flows/preview', body, { params: { module } }).then(data),
  reorderApprovalFlows: (module: string, toOptionId: number, keys: string[]) =>
    http.put<ApprovalFlow[]>('/approval-flows/order', { module, toOptionId, keys }).then(data),
  getConversions: () => http.get<Conversion[]>('/conversions').then(data),
  createConversion: (body: Conversion) => http.post<Conversion>('/conversions', body).then(data),
  updateConversion: (key: string, body: Conversion) => http.put<Conversion>(`/conversions/${encodeURIComponent(key)}`, body).then(data),
  deleteConversion: (key: string) => http.delete(`/conversions/${encodeURIComponent(key)}`).then(data),
  getRecordConversions: (id: number) => http.get<RecordConversion[]>(`/records/${id}/conversions`).then(data),
  /** Keys of the target's required header fields that the conversion's copy cannot fill; the user fills them in the dialog. */
  getConversionInputs: (id: number, key: string) =>
    http.get<string[]>(`/records/${id}/conversions/${encodeURIComponent(key)}/inputs`).then(data),
  convertRecord: (id: number, key: string, body: ConvertBody, idempotencyKey?: string) =>
    http.post<DynamicRecord>(`/records/${id}/conversions/${encodeURIComponent(key)}/convert`, body, idem(idempotencyKey)).then(data),

  getEvents: (sourceModuleKey: string) => http.get<ModuleEvent[]>('/events', { params: { sourceModuleKey } }).then(data),
  getEvent: (key: string) => http.get<ModuleEvent>(`/events/${encodeURIComponent(key)}`).then(data),
  createEvent: (body: ModuleEvent) => http.post<ModuleEvent>('/events', body).then(data),
  updateEvent: (key: string, body: ModuleEvent) => http.put<ModuleEvent>(`/events/${encodeURIComponent(key)}`, body).then(data),
  deleteEvent: (key: string) => http.delete(`/events/${encodeURIComponent(key)}`).then(data),
  getChecks: (moduleKey: string) => http.get<ModuleCheck[]>('/checks', { params: { moduleKey } }).then(data),
  getCheck: (key: string) => http.get<ModuleCheck>(`/checks/${encodeURIComponent(key)}`).then(data),
  createCheck: (body: ModuleCheck) => http.post<ModuleCheck>('/checks', body).then(data),
  updateCheck: (key: string, body: ModuleCheck) => http.put<ModuleCheck>(`/checks/${encodeURIComponent(key)}`, body).then(data),
  deleteModuleCheck: (key: string) => http.delete(`/checks/${encodeURIComponent(key)}`).then(data),
  getLedger: (moduleKey: string) => http.get<LedgerConfig>(`/modules/${moduleKey}/ledger`).then(data),
  putLedger: (moduleKey: string, body: LedgerBody) => http.put<LedgerConfig>(`/modules/${moduleKey}/ledger`, body).then(data),
  deleteLedger: (moduleKey: string) => http.delete(`/modules/${moduleKey}/ledger`).then(data),
  getRecordLedger: (id: number) => http.get<RecordLedger>(`/records/${id}/ledger`).then(data),
  /** Creates the reversing entry; `values` may only override descriptive fields (the server rejects the rest). */
  reverseRecord: (id: number, values: RecordValues, idempotencyKey?: string) =>
    http.post<DynamicRecord>(`/records/${id}/reverse`, { values }, idem(idempotencyKey)).then(data),
  getRecordEffects: (id: number) => http.get<RecordEffects>(`/records/${id}/effects`).then(data),
  getAuthOptions: () => http.get<AuthOptions>('/auth/options').then(data),
  /** 補齊後的扁平介面文字；hash 取回應標頭 X-UI-Hash（實際內容的雜湊，可能和要求的 v 不同：打到還沒換新的另一台）。 */
  getUiMessages: (code: string, v?: string) =>
    http.get<Record<string, string>>(`/i18n/ui/${encodeURIComponent(code)}`, v ? { params: { v } } : undefined)
      .then((r) => ({ hash: String(r.headers['x-ui-hash'] ?? ''), messages: r.data })),
  login: (body: { userId: string; password: string }) => http.post<AuthUser>('/auth/login', body).then(data),
  demoLogin: (userId: string) => http.post<AuthUser>('/auth/demo-login', { userId }).then(data),
  logout: () => http.post('/auth/logout').then(data),
  changePassword: (body: { oldPassword: string; newPassword: string }) =>
    http.post<AuthUser>('/auth/change-password', body).then(data),
  getMe: () => http.get<Me>('/me').then(data),
  getMenu: () => http.get<MenuItem[]>('/menu').then(data),
  getThemeDefaults: () => http.get<ThemeDefaults>('/theme').then(data),
  saveMyTheme: (c: ThemeChoice) => http.put<MeTheme>('/me/theme', c).then(data),
  saveSiteTheme: (d: ThemeDefaults) => http.put<ThemeDefaults>('/system/theme', d).then(data),

  getPagedRecords: (key: string, { currentPage, pagesSize, filter, sort, q, approval }: PagedRecordsQuery) =>
    http.get<PagedResponse<DynamicRecord>>(`/modules/${key}/records/paged`, {
      params: {
        currentPage, pagesSize, filter, q, approval,
        sortBy: sort?.length ? sort.map((s) => `f_${s.field}`) : undefined,
        sortOrder: sort?.length ? sort.map((s) => s.order) : undefined,
      },
      paramsSerializer: { indexes: null },
    }).then(data),
  createRecord: (key: string, values: RecordValues, hostKey?: string, details?: DetailsPayload, idempotencyKey?: string) =>
    http.post<DynamicRecord>(`/modules/${key}/records`, { values, hostKey, details }, idem(idempotencyKey)).then(data),
  getRecord: (id: number) => http.get<DynamicRecord>(`/records/${id}`).then(data),
  getRecordApproval: (id: number) => http.get<RecordApproval>(`/records/${id}/approval`).then(data),
  previewApproval: (id: number, to: number) => http.post<ApprovalPreview>(`/records/${id}/approval/preview`, { to }).then(data),
  submitApproval: (id: number, body: SubmitApprovalBody, key?: string) =>
    http.post<SubmitApprovalResult>(`/records/${id}/approval/submit`, body, idem(key)).then(data),
  approveTask: (taskId: number, body: ApproveBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-tasks/${taskId}/approve`, body, idem(key)).then(data),
  rejectTask: (taskId: number, body: RejectBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-tasks/${taskId}/reject`, body, idem(key)).then(data),
  addSignTask: (taskId: number, body: AddSignBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-tasks/${taskId}/add-sign`, body, idem(key)).then(data),
  transferTask: (taskId: number, body: TransferBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-tasks/${taskId}/transfer`, body, idem(key)).then(data),
  reassignTask: (taskId: number, body: ReassignBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-tasks/${taskId}/reassign`, body, idem(key)).then(data),
  cancelApproval: (instanceId: number, body: CancelBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-instances/${instanceId}/cancel`, body, idem(key)).then(data),
  /** The setting (no delegate when none) with the server's business day. */
  getMyApprovalDelegate: () => http.get<MyApprovalDelegate>('/me/approval-delegate').then(data),
  saveMyApprovalDelegate: (body: ApprovalDelegateBody) => http.put<ApprovalDelegate>('/me/approval-delegate', body).then(data),
  clearMyApprovalDelegate: () => http.delete('/me/approval-delegate').then(() => undefined),
  withdrawApproval: (instanceId: number, body: WithdrawBody, key?: string) =>
    http.post<ApprovalActionResult>(`/approval-instances/${instanceId}/withdraw`, body, idem(key)).then(data),
  getApprovalInbox: (box: ApprovalBox, filter: ApprovalInboxFilter, page: number, size = 20) =>
    http.get<PagedResponse<ApprovalInboxItem>>('/approval-tasks', {
      params: { box, module: filter.module, status: filter.status, from: filter.from, to: filter.to, currentPage: page, pagesSize: size },
    }).then(data),
  updateRecord: (id: number, values: RecordValues, rowVersion: string, details?: DetailsPayload, idempotencyKey?: string) =>
    http.put<DynamicRecord>(`/records/${id}`, { values, rowVersion, details }, idem(idempotencyKey)).then(data),
  getViews: (key: string) => http.get<ListView[]>(`/modules/${key}/views`).then(data),
  createView: (key: string, body: ViewBody & { shared: boolean }) =>
    http.post<ListView>(`/modules/${key}/views`, body).then(data),
  updateView: (id: number, body: ViewBody) => http.put<ListView>(`/views/${id}`, body).then(data),
  deleteView: (id: number) => http.delete<void>(`/views/${id}`).then(data),
  /** `lightUrl` comes from SchemaReference and is relative to the API base. */
  light: (lightUrl: string, { keyword, page, size }: { keyword?: string; page: number; size: number }) =>
    http.get<LightPage>(lightUrl, { params: { keyword, page, size } }).then(data),
  entities: () => http.get<EntitySummary[]>('/entities').then(data),
  related: (recordId: number) => http.get<RelatedTab[]>(`/records/${recordId}/related`).then(data),
  relatedPaged: (recordId: number, fieldId: number, page: number, size: number) =>
    http.get<PagedResponse<DynamicRecord>>(`/records/${recordId}/related/${fieldId}/paged`, {
      params: { page, size },
    }).then(data),
  recordHistory: (id: number, page: number, size: number, locale: string) =>
    http.get<PagedResponse<RecordHistoryEntry>>(`/records/${id}/history`, {
      params: { currentPage: page, pagesSize: size, locale },
    }).then(data),
  recordByHost: (moduleKey: string, hostKey: string) =>
    http.get<DynamicRecord>(`/modules/${moduleKey}/records/by-host/${encodeURIComponent(hostKey)}`).then(data),
  deleteRecord: (id: number, rowVersion: string, idempotencyKey?: string) =>
    http.delete<void>(`/records/${id}`, { params: { rowVersion }, ...idem(idempotencyKey) }).then(data),
  /** Blob download through axios (session cookie, base URL); validateStatus bypasses the interceptor, so the error body (a Blob) is parsed here and passed to handleAuthFailure by hand. */
  exportRecords: async (key: string, format: 'csv' | 'xlsx', { filter, sort, q, approval }: ExportQuery, locale: string) => {
    const r = await http.get<Blob>(`/modules/${key}/export`, {
      params: {
        format, filter, q, locale, approval,
        sortBy: sort?.length ? sort.map((s) => `f_${s.field}`) : undefined,
        sortOrder: sort?.length ? sort.map((s) => s.order) : undefined,
      },
      paramsSerializer: { indexes: null },
      responseType: 'blob',
      validateStatus: () => true,
    });
    if (r.status >= 400) {
      let body: unknown;
      try { body = JSON.parse(await r.data.text()); } catch { body = undefined; }
      const err = toApiError({ response: { status: r.status, data: body } });
      handleAuthFailure(err);
      throw err;
    }
    return { blob: r.data, fileName: dispositionFileName(r.headers['content-disposition']) ?? `${key}.${format}` };
  },
  getDashboards: () => http.get<DashboardList>('/dashboards').then(data),
  getDashboard: (id: number) => http.get<DashboardDetail>(`/dashboards/${id}`).then(data),
  createDashboard: (name: string, layout: DashboardLayout) => http.post<DashboardDetail>('/dashboards', { name, layout }).then(data),
  deleteDashboard: (id: number) => http.delete(`/dashboards/${id}`).then(data),
  /** version 是畫面載入時的指派版版本：第一次另存個人版時當基準，這樣之後的管理員更新才不會被漏掉。 */
  saveDashboardLayout: (id: number, layout: DashboardLayout, name?: string, version?: number) =>
    http.put<DashboardDetail>(`/dashboards/${id}/layout`, { name, layout, version }).then(data),
  restoreDashboard: (id: number) => http.post<DashboardDetail>(`/dashboards/${id}/restore`).then(data),
  runCard: (id: number, cardId: string, filterRange: ReportDateRange | null, locale: string) =>
    http.post<CardResult>(`/dashboards/${id}/cards/${encodeURIComponent(cardId)}/run`, { filterRange }, { params: { locale } }).then(data),
  drillCard: (id: number, cardId: string, groupKeys: string[], filterRange: ReportDateRange | null, locale: string) =>
    http.post<DrillResult>(`/dashboards/${id}/cards/${encodeURIComponent(cardId)}/drill`, { groupKeys, filterRange }, { params: { locale } }).then(data),
  adminGetDashboards: () => http.get<AdminDashboardItem[]>('/admin/dashboards').then(data),
  adminGetDashboard: (id: number) => http.get<AdminDashboardDetail>(`/admin/dashboards/${id}`).then(data),
  adminCreateDashboard: (name: string, layout: DashboardLayout) => http.post<AdminDashboardDetail>('/admin/dashboards', { name, layout }).then(data),
  adminUpdateDashboard: (id: number, name: string, layout: DashboardLayout, version: number) =>
    http.put<AdminDashboardDetail>(`/admin/dashboards/${id}`, { name, layout, version }).then(data),
  /** 編輯中的卡片（可能還沒存檔）以管理員自己的權限預覽；結果形狀與一般卡片查詢相同。 */
  adminPreviewCard: (card: DashboardCard, locale: string) =>
    http.post<CardResult>('/admin/dashboards/preview-card', { card }, { params: { locale } }).then(data),
  adminDeleteDashboard: (id: number) => http.delete(`/admin/dashboards/${id}`).then(data),
  adminSetDashboardAssignments: (id: number, groupIds: number[]) =>
    http.put<AdminDashboardDetail>(`/admin/dashboards/${id}/assignments`, { groupIds }).then(data),
  adminReorderDashboards: (ids: number[]) => http.put('/admin/dashboards/order', { ids }).then(data),
  getReportShareTargets: () => http.get<ReportShareTarget[]>('/reports/share-targets').then(data),
  getReports: () => http.get<ReportItem[]>('/reports').then(data),
  getReport: (id: number) => http.get<ReportDetail>(`/reports/${id}`).then(data),
  /** filter 是報表頁的暫時篩選；沒有部門時不送部門欄位（模組報表一律不送）。 */
  runReport: (id: number, range: ReportDateRange | null, locale: string, filter?: ReportRunFilter) =>
    http.post<ReportResult>(`/reports/${id}/run`, { range, ...departmentBody(filter) }, { params: { locale } }).then(data),
  drillReport: (id: number, groupKeys: string[], range: ReportDateRange | null, locale: string, filter?: ReportRunFilter) =>
    http.post<DrillResult>(`/reports/${id}/drill`, { groupKeys, range, ...departmentBody(filter) }, { params: { locale } }).then(data),
  getReportDatasets: (locale: string) => http.get<ReportDatasetItem[]>('/reports/datasets', { params: { locale } }).then(data),
  getReportDatasetCatalog: (dataset: string, locale: string) =>
    http.get<ModuleCatalog>(`/reports/datasets/${encodeURIComponent(dataset)}/catalog`, { params: { locale } }).then(data),
  getReportCatalogModules: (locale: string) => http.get<CatalogModule[]>('/reports/catalog', { params: { locale } }).then(data),
  getReportCatalog: (moduleKey: string, locale: string) =>
    http.get<ModuleCatalog>(`/reports/catalog/${encodeURIComponent(moduleKey)}`, { params: { locale } }).then(data),
  previewReport: (definition: ReportDefinition, locale: string) =>
    http.post<ReportResult>('/reports/preview', definition, { params: { locale } }).then(data),
  createReport: (name: string, definition: ReportDefinition) => http.post<ReportDetail>('/reports', { name, definition }).then(data),
  updateReport: (id: number, name: string, definition: ReportDefinition) =>
    http.put<ReportDetail>(`/reports/${id}`, { name, definition }).then(data),
  copyReport: (id: number) => http.post<ReportDetail>(`/reports/${id}/copy`).then(data),
  deleteReport: (id: number) => http.delete(`/reports/${id}`).then(data),
  setReportShares: (id: number, groupIds: number[]) => http.put(`/reports/${id}/shares`, { groupIds }).then(data),
  /** 同 exportRecords：validateStatus 繞過 interceptor，錯誤本文（Blob）在這裡解析。 */
  exportReport: async (id: number, locale: string, filter?: ReportRunFilter) => {
    const r = await http.get<Blob>(`/reports/${id}/export`, {
      params: { locale, ...exportParams(filter) }, paramsSerializer: { indexes: null }, responseType: 'blob', validateStatus: () => true,
    });
    if (r.status >= 400) {
      let body: unknown;
      try { body = JSON.parse(await r.data.text()); } catch { body = undefined; }
      const err = toApiError({ response: { status: r.status, data: body } });
      handleAuthFailure(err);
      throw err;
    }
    return { blob: r.data, fileName: dispositionFileName(r.headers['content-disposition']) ?? `report-${id}.xlsx` };
  },
  getDemoDataStatus: () => http.get<DemoDataStatus>('/demo-data').then(data),
  generateDemoData: () => http.post<DemoDataStatus>('/demo-data/generate').then(data),
  getAppPackages: () => http.get<AppPackageSummary[]>('/apps/packages').then(data),
  importAppPackage: (json: string) =>
    http.post<AppPackageSummary>('/apps/packages/import', json, { headers: { 'Content-Type': 'application/json' } }).then(data),
  previewOpenApp: (id: string, body: OpenAppBody) =>
    http.post<OpenPreview>(`/apps/packages/${encodeURIComponent(id)}/preview`, body).then(data),
  openApp: (id: string, body: OpenAppBody) =>
    http.post<AppInfo>(`/apps/packages/${encodeURIComponent(id)}/open`, body).then(data),
  getApps: () => http.get<AppInfo[]>('/apps').then(data),
  closeApp: (key: string, cascade: boolean) =>
    http.post<CloseAppResult>(`/apps/${encodeURIComponent(key)}/close`, { cascade }).then(data),
  reopenApp: (key: string) => http.post<AppInfo>(`/apps/${encodeURIComponent(key)}/reopen`).then(data),
  getTemplates: () => http.get<TemplateSummary[]>('/templates').then(data),
  instantiateTemplate: (key: string, body: InstantiateBody) =>
    http.post<InstantiateResult>(`/templates/${encodeURIComponent(key)}/instantiate`, body).then(data),
  /** Raw JSON text as the body (the server validates it); Content-Type must stay application/json. */
  importTemplate: (json: string) =>
    http.post<TemplateSummary>('/templates/import', json, { headers: { 'Content-Type': 'application/json' } }).then(data),
  exportTemplate: async (key: string) => {
    const r = await http.get<Blob>(`/modules/${key}/export-template`, { responseType: 'blob', validateStatus: () => true });
    if (r.status >= 400) {
      let body: unknown;
      try { body = JSON.parse(await r.data.text()); } catch { body = undefined; }
      const err = toApiError({ response: { status: r.status, data: body } });
      handleAuthFailure(err);
      throw err;
    }
    return { blob: r.data, fileName: dispositionFileName(r.headers['content-disposition']) ?? `${key}.template.json` };
  },
  previewImport: (key: string, file: File) =>
    http.post<ImportPreview>(`/modules/${key}/import`, importForm(file), { params: { commit: false } }).then(data),
  commitImport: (key: string, file: File, idempotencyKey?: string) =>
    http.post<ImportResult>(`/modules/${key}/import`, importForm(file), { params: { commit: true }, ...idem(idempotencyKey) }).then(data),
  getPrintTemplates: (moduleKey: string, recordId?: number) =>
    http.get<PrintTemplateOption[]>(`/modules/${moduleKey}/print-templates`, { params: recordId === undefined ? undefined : { recordId } }).then(data),
  getPrintTemplateDefinitions: (moduleKey: string) =>
    http.get<PrintTemplateDefinition[]>(`/modules/${moduleKey}/print-templates/definitions`).then(data),
  createPrintTemplate: (moduleKey: string, body: PrintTemplateDefinition) =>
    http.post<PrintTemplateDefinition>(`/modules/${moduleKey}/print-templates`, body).then(data),
  updatePrintTemplate: (moduleKey: string, body: PrintTemplateDefinition) =>
    http.put<PrintTemplateDefinition>(`/modules/${moduleKey}/print-templates/${body.key}`, body).then(data),
  deletePrintTemplate: (moduleKey: string, key: string) => http.delete(`/modules/${moduleKey}/print-templates/${key}`).then(data),
  /** The finished HTML document for one record; shown in a script-less sandbox and printed by the browser. */
  printRecord: (recordId: number, templateKey: string, locale: string) =>
    http.get<string>(`/records/${recordId}/print/${templateKey}`, { params: { locale }, responseType: 'text', transformResponse: (d) => d as string }).then(data),
  /** Task endpoints: the server omits null properties, so every response goes through task-normalize. */
  getMyTasks: (tab: TaskListTab, filter: TaskListFilter, page: number, size = 20) =>
    http.get<TaskPage>('/tasks', { params: { tab, filter, currentPage: page, pagesSize: size } }).then(data).then(normalizeTaskPage),
  /** Designer endpoints for task rules; the server omits null properties, so lists default to empty. */
  getTaskRules: (moduleKey: string) => http.get<TaskRule[]>(`/modules/${encodeURIComponent(moduleKey)}/task-rules`).then(data)
    .then((list) => list.map(normalizeTaskRule)),
  createTaskRule: (moduleKey: string, body: TaskRuleInput) =>
    http.post<TaskRule>(`/modules/${encodeURIComponent(moduleKey)}/task-rules`, body).then(data).then(normalizeTaskRule),
  updateTaskRule: (moduleKey: string, ruleKey: string, body: TaskRuleInput) =>
    http.put<TaskRule>(`/modules/${encodeURIComponent(moduleKey)}/task-rules/${encodeURIComponent(ruleKey)}`, body).then(data).then(normalizeTaskRule),
  deleteTaskRule: (moduleKey: string, ruleKey: string) => http.delete(`/modules/${encodeURIComponent(moduleKey)}/task-rules/${encodeURIComponent(ruleKey)}`).then(data),
  applyTaskRule: (recordId: number, ruleKey: string) =>
    http.post<RecordTasks>(`/records/${recordId}/task-rules/${encodeURIComponent(ruleKey)}/apply`).then(data).then(normalizeRecordTasks),
  resumeTaskRule: (moduleKey: string, ruleKey: string) =>
    http.post<TaskRule>(`/modules/${encodeURIComponent(moduleKey)}/task-rules/${encodeURIComponent(ruleKey)}/resume`).then(data).then(normalizeTaskRule),
  /** 維運監控、稽核、寄信設定（維運設計 §17.5）；全部只給系統管理員，伺服器省略 null 屬性。 */
  getOpsOverview: () => http.get<OpsOverview>('/system/ops/overview').then(data),
  getHealthChecks: () => http.get<HealthCheckConfig[]>('/system/ops/health-checks').then(data),
  updateHealthCheck: (code: string, body: HealthCheckUpdate) =>
    http.put<HealthCheckConfig>(`/system/ops/health-checks/${encodeURIComponent(code)}`, body).then(data),
  runHealthCheck: (code: string) => http.post<HealthCheckState>(`/system/ops/health-checks/${encodeURIComponent(code)}/run`).then(data),
  getHealthResults: (code: string, fromUtc: string, toUtc: string) =>
    http.get<HealthResults>(`/system/ops/health-checks/${encodeURIComponent(code)}/results`, { params: { fromUtc, toUtc } }).then(data),
  getIncidents: (f: IncidentFilter, cursor?: string) =>
    http.get<IncidentList>('/system/ops/incidents', {
      params: { status: f.status.length ? f.status.join(',') : undefined, category: f.category ?? undefined, severity: f.severity ?? undefined, cursor },
    }).then(data),
  getIncident: (id: number) => http.get<IncidentDetail>(`/system/ops/incidents/${id}`).then(data),
  acknowledgeIncident: (id: number, expectedStatus: string) =>
    http.post<IncidentSummary>(`/system/ops/incidents/${id}/acknowledge`, { expectedStatus }).then(data),
  resolveIncident: (id: number, expectedStatus: string, note?: string) =>
    http.post<IncidentSummary>(`/system/ops/incidents/${id}/resolve`, { expectedStatus, note: note || undefined }).then(data),
  getSmtpChannel: () => http.get<SmtpChannel>('/system/ops/channels/smtp').then(data),
  saveSmtpChannel: (body: SmtpChannelInput) => http.put('/system/ops/channels/smtp', body).then(data),
  testSmtpChannel: () => http.post<MailTestResult>('/system/ops/channels/smtp/test').then(data),
  getMailSettings: () => http.get<MailSettings>('/system/mail-settings').then(data),
  saveMailSettings: (body: MailSettingsInput) => http.put('/system/mail-settings', body).then(data),
  testMailSettings: () => http.post<MailTestResult>('/system/mail-settings/test').then(data),
  getAudit: (f: AuditFilter, cursor?: string) =>
    http.get<AuditList>('/system/ops/audit', { params: { ...Object.fromEntries(Object.entries(f).filter(([, v]) => v)), cursor } }).then(data),
  getAuditEntry: (id: number) => http.get<AuditDetail>(`/system/ops/audit/${id}`).then(data),
  getAuditActions: () => http.get<AuditAction[]>('/system/ops/audit/actions').then(data),
  /** Policy endpoints; an absent `value`/`override` in a response means "not set" (the server omits nulls). Sending `null` goes back to the layer above. */
  getSystemPolicies: () => http.get<SystemPolicy[]>('/system/policies').then(data),
  saveSystemPolicy: (code: string, value: PolicyValue | null) =>
    http.put<SystemPolicy>(`/system/policies/${encodeURIComponent(code)}`, { value }).then(data),
  getModulePolicies: (moduleKey: string) => http.get<ModulePolicy[]>(`/modules/${encodeURIComponent(moduleKey)}/policies`).then(data),
  saveModulePolicy: (moduleKey: string, code: string, value: PolicyValue | null) =>
    http.put<ModulePolicy>(`/modules/${encodeURIComponent(moduleKey)}/policies/${encodeURIComponent(code)}`, { value }).then(data),
  getEffectivePolicies: (moduleKey: string | null) =>
    http.get<EffectivePolicies>('/policies/effective', moduleKey ? { params: { module: moduleKey } } : undefined).then(data),
  /** 語系包匯入與語系管理（系統管理員）；預覽的本文是檔案原始位元組。 */
  getLocaleAdmin: () => http.get<LocaleAdminList>('/i18n/locales').then(data),
  previewLocalePack: (bytes: ArrayBuffer, fileName: string) =>
    http.post<LocalePackReportData>('/i18n/packs/preview', bytes, { params: { fileName }, headers: { 'Content-Type': 'application/octet-stream' } }).then(data),
  applyLocalePack: (previewId: string, fileHash: string) =>
    http.post<ApplyLocalePackResult>('/i18n/packs/apply', { previewId, fileHash }).then(data),
  rollbackLocalePack: (code: string) => http.post<{ code: string; packId: number }>(`/i18n/locales/${encodeURIComponent(code)}/rollback`).then(data),
  clearLocaleOverrides: (code: string) => http.post<{ code: string; packId: number }>(`/i18n/locales/${encodeURIComponent(code)}/clear`).then(data),
  removeLocale: (code: string) => http.delete(`/i18n/locales/${encodeURIComponent(code)}`).then(data),
  getTaskSummary: () => http.get<TaskCounts>('/tasks/summary').then(data)
    .then((c) => ({ dueToday: c.dueToday ?? 0, overdue: c.overdue ?? 0 })),
  getNotifications: (take = 20) =>
    http.get<NotificationList>('/notifications', { params: { take } }).then(data).then(normalizeNotificationList),
  markNotificationRead: (id: number) => http.post(`/notifications/${id}/read`).then(data),
  markAllNotificationsRead: () => http.post('/notifications/read-all').then(data),
  getTask: (id: number) => http.get<TaskDetail>(`/tasks/${id}`).then(data).then(normalizeTaskDetail),
  createTask: (body: TaskInput) => http.post<TaskDetail>('/tasks', body).then(data).then(normalizeTaskDetail),
  /** Adds a subtask under `parentId` (record and module come from the parent); the reply is the new subtask. */
  createSubtask: (parentId: number, body: TaskInput) =>
    http.post<TaskDetail>(`/tasks/${parentId}/subtasks`, body).then(data).then(normalizeTaskDetail),
  /** A property left out stays as it is; an explicit null clears it, so the patch is sent as given. The reply is `visible: false` when the writer lost sight of the task. */
  updateTask: (id: number, patch: TaskPatch) => http.patch<TaskDetail>(`/tasks/${id}`, patch).then(data).then(normalizeTaskUpdate),
  getTaskCapabilities: () => http.get<TaskCapabilities>('/tasks/capabilities').then(data).then(normalizeTaskCapabilities),
  /** Undo a manager's reassignment with the token from the PATCH reply (single use, 2 minutes, same user). */
  undoReassign: (id: number, undoToken: string) =>
    http.post<TaskDetail>(`/tasks/${id}/undo-reassign`, { undoToken }).then(data).then(normalizeTaskUpdate),
  deleteTask: (id: number) => http.delete(`/tasks/${id}`).then(data),
  /** Board endpoints: `source` is a saved board (viewId) or an unsaved config; neither means the default board the server decides. */
  getBoardColumns: (source: BoardSource) =>
    http.post<TaskBoardData>('/task-board/columns', boardSourceBody(source)).then(data).then(normalizeBoard),
  getBoardColumnPage: (column: string, source: BoardSource, offset: number) =>
    http.post<BoardColumnPage>(`/task-board/columns/${encodeURIComponent(column)}/page`, { ...boardSourceBody(source), offset }).then(data).then(normalizeBoardPage),
  moveTask: (id: number, body: TaskMoveBody) =>
    http.post<TaskMoveResult>(`/tasks/${id}/move`, body).then(data).then(normalizeMoveResult),
  /** 甘特查詢（看板設計 §11.5.2.5）：甘特 id 是看板、已刪除或不再分享時 404。上一段／下一段把回應的 window 平移後送出。 */
  getGantt: (source: GanttSource = {}) =>
    http.post<TaskGanttData>('/tasks/gantt', { viewId: source.viewId ?? null, config: source.config ?? null, window: source.window ?? null })
      .then(data).then(normalizeGantt),
  /** 甘特圖改日期（看板設計 §11.5.2.6）：兩個日期一律都送，版本不符 409 stale。 */
  scheduleTask: (id: number, body: TaskScheduleBody) =>
    http.post<TaskScheduleResult>(`/tasks/${id}/schedule`, body).then(data).then(normalizeScheduleResult),
  getTaskViews: (kind: TaskViewKind = 'board') => http.get<TaskViewList>('/task-views', { params: { kind } }).then(data).then(normalizeViewList),
  /** 種類照存檔的資料列；甘特檢視請帶型別參數 TaskGanttConfig。 */
  getTaskView: <C extends TaskViewConfig = TaskBoardConfig>(id: number) =>
    http.get<TaskViewDetail<C>>(`/task-views/${id}`).then(data).then((v) => normalizeViewDetail<C>(v)),
  /** 沒給 config 時伺服器存該種的預設（看板：預設看板；甘特：預設甘特圖）。 */
  createTaskView: <C extends TaskViewConfig = TaskBoardConfig>(body: { kind?: TaskViewKind; name: string; config?: C }) =>
    http.post<TaskViewDetail<C>>('/task-views', body).then(data).then((v) => normalizeViewDetail<C>(v)),
  /** `rowVersion` is the version read with the board; a stale one comes back as 409 (code stale). The kind cannot change. */
  updateTaskView: <C extends TaskViewConfig = TaskBoardConfig>(id: number, body: { name?: string; config?: C; rowVersion: string }) =>
    http.put<TaskViewDetail<C>>(`/task-views/${id}`, body).then(data).then((v) => normalizeViewDetail<C>(v)),
  deleteTaskView: (id: number) => http.delete(`/task-views/${id}`).then(data),
  copyTaskView: <C extends TaskViewConfig = TaskBoardConfig>(id: number) =>
    http.post<TaskViewDetail<C>>(`/task-views/${id}/copy`).then(data).then((v) => normalizeViewDetail<C>(v)),
  /** Replaces the whole share list. */
  setTaskViewShares: <C extends TaskViewConfig = TaskBoardConfig>(id: number, shares: { kind: TaskViewShareKind; id: string }[]) =>
    http.put<TaskViewDetail<C>>(`/task-views/${id}/shares`, shares).then(data).then((v) => normalizeViewDetail<C>(v)),
  getTaskViewShareTargets: () => http.get<TaskViewShareTargets>('/task-views/share-targets').then(data).then(normalizeShareTargets),
  getRecordTasks: (recordId: number) => http.get<RecordTasks>(`/records/${recordId}/tasks`).then(data).then(normalizeRecordTasks),
  createRecordTask: (recordId: number, body: TaskInput) =>
    http.post<TaskDetail>(`/records/${recordId}/tasks`, body).then(data).then(normalizeTaskDetail),
  getTaskComments: (id: number) => http.get<TaskComment[]>(`/tasks/${id}/comments`).then(data)
    .then((list) => list.map(normalizeTaskComment)),
  addTaskComment: (id: number, body: string) =>
    http.post<TaskComment>(`/tasks/${id}/comments`, { body }).then(data).then(normalizeTaskComment),
  /** One request carries the text and the files; the server rejects more than 5 files or a disabled upload. */
  addTaskCommentWithFiles: (id: number, body: string, files: File[]) => {
    const form = new FormData();
    form.append('body', body);
    for (const f of files) form.append('files', f);
    return http.post<TaskComment>(`/tasks/${id}/comments`, form).then(data).then(normalizeTaskComment);
  },
  withdrawTaskComment: (id: number, commentId: number) =>
    http.post<TaskComment>(`/tasks/${id}/comments/${commentId}/withdraw`).then(data).then(normalizeTaskComment),
  getMentionCandidates: (keyword: string) =>
    http.get<User[]>('/users/mention-candidates', { params: { keyword } }).then(data),
  getAttachmentSettings: () => http.get<AttachmentSettings>('/attachments/settings').then(data),
  uploadAttachment: (recordId: number, fieldId: number, file: File) => {
    const body = new FormData();
    body.append('file', file);
    return http.post<UploadedAttachment>(`/records/${recordId}/attachments/${fieldId}`, body).then(data);
  },
  /** The blob and the suggested file name; errors (404/403/401) go through the usual ApiError path. */
  downloadAttachment: async (recordId: number, fieldId: number, fileId: string) => {
    const r = await http.get<Blob>(`/records/${recordId}/attachments/${fieldId}/${fileId}`, { responseType: 'blob', validateStatus: () => true });
    if (r.status >= 400) {
      let body: unknown;
      try { body = JSON.parse(await r.data.text()); } catch { body = undefined; }
      const err = toApiError({ response: { status: r.status, data: body } });
      handleAuthFailure(err);
      throw err;
    }
    return r.data;
  },
};
