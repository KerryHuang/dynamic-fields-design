import type {
  AdminGroup, AdminUser, AttachmentSettings, PrintTemplateDefinition, PrintTemplateOption, UploadedAttachment, DeleteCheck, DeleteTargetRef, AdminUserCreate, AdminUserUpdate, AuthOptions, AuthUser, ConditionNode, ConvertBody, Conversion, DesignerField, DetailRowPayload, DynamicRecord, EntitySummary, FieldConfig, FieldType, Group, GroupImpact, I18nText, LightPage, ListView, Me, MenuItem, ModuleDetail, ModulePermissions, ModuleSchema, RecordConversion,
  ImportPreview, ImportResult, PagedResponse, RecordHistoryEntry, RelatedTab, Rule, RollupSource, User, ViewSort, Workflow,
} from 'src/domain/dynamic/types';
import type {
  AdminDashboardDetail, AdminDashboardItem, CardResult, DashboardCard, DashboardDetail, DashboardLayout, DashboardList,
} from 'src/domain/dynamic/dashboard-types';
import type { CatalogModule, DrillResult, ModuleCatalog, ReportDateRange, ReportDefinition, ReportDetail, ReportItem, ReportResult, ReportShareTarget } from 'src/domain/dynamic/report-types';
import type { MeTheme, ThemeChoice, ThemeDefaults } from 'src/domain/theme/theme-model';
import type { Department, DepartmentInput } from 'src/domain/dynamic/organization';
import type { CheckDefinition, LedgerBody, LedgerConfig, ModuleCheck, ModuleEvent, RecordEffects, RecordLedger } from 'src/domain/dynamic/events-ledger';
import type {
  NotificationList, RecordTasks, TaskComment, TaskCounts, TaskDetail, TaskInput, TaskListFilter, TaskListTab, TaskPage, TaskPatch,
} from 'src/domain/dynamic/task-types';
import type { TaskRule, TaskRuleInput } from 'src/domain/dynamic/task-rule-draft';
import { handleAuthFailure, http, toApiError } from './http';
import {
  normalizeNotificationList, normalizeRecordTasks, normalizeTaskComment, normalizeTaskDetail, normalizeTaskPage, normalizeTaskRule,
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
    http.put(`/fields/${id}`, body).then(data),
  setFieldStatus: (id: number, status: 'active' | 'inactive') => http.put(`/fields/${id}/status`, { status }).then(data),
  reorderFields: (blockId: number, fieldIds: number[]) => http.put(`/blocks/${blockId}/field-order`, { fieldIds }).then(data),
  addOption: (fieldId: number, body: { label: I18nText; color?: string }) =>
    http.post(`/fields/${fieldId}/options`, body).then(data),
  updateOption: (id: number, body: { label: I18nText; color?: string; active: boolean }) =>
    http.put(`/options/${id}`, body).then(data),

  rollupSources: (key: string) => http.get<RollupSource[]>(`/modules/${key}/rollup-sources`).then(data),
  getModuleSchema: (key: string) => http.get<ModuleSchema>(`/modules/${key}/schema`).then(data),
  getPreviewSchema: (key: string) => http.get<ModuleSchema>(`/modules/${key}/schema/preview`).then(data),
  getUsers: () => http.get<User[]>('/users').then(data),
  getGroups: () => http.get<Group[]>('/groups').then(data),
  getAdminUsers: () => http.get<AdminUser[]>('/admin/users').then(data),
  createUser: (body: AdminUserCreate) => http.post('/admin/users', body).then(data),
  updateUser: (id: string, body: AdminUserUpdate) => http.put(`/admin/users/${encodeURIComponent(id)}`, body).then(data),
  resetUserPassword: (id: string, password: string) =>
    http.post(`/admin/users/${encodeURIComponent(id)}/reset-password`, { password }).then(data),
  setUserActive: (id: string, active: boolean) =>
    http.post(`/admin/users/${encodeURIComponent(id)}/${active ? 'activate' : 'deactivate'}`).then(data),
  getAdminGroups: () => http.get<AdminGroup[]>('/admin/groups').then(data),
  createGroup: (body: { name: string; locale: string }) => http.post<AdminGroup>('/admin/groups', body).then(data),
  renameGroup: (id: number, body: { name: string; locale: string }) => http.put<AdminGroup>(`/admin/groups/${id}`, body).then(data),
  setGroupMembers: (id: number, body: { userIds: string[] }) => http.put(`/admin/groups/${id}/members`, body).then(data),
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
  getWorkflow: (key: string) => http.get<Workflow>(`/modules/${key}/workflow`).then(data),
  putWorkflow: (key: string, body: Workflow) => http.put<Workflow>(`/modules/${key}/workflow`, body).then(data),
  deleteWorkflow: (key: string) => http.delete(`/modules/${key}/workflow`).then(data),
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

  getPagedRecords: (key: string, { currentPage, pagesSize, filter, sort, q }: PagedRecordsQuery) =>
    http.get<PagedResponse<DynamicRecord>>(`/modules/${key}/records/paged`, {
      params: {
        currentPage, pagesSize, filter, q,
        sortBy: sort?.length ? sort.map((s) => `f_${s.field}`) : undefined,
        sortOrder: sort?.length ? sort.map((s) => s.order) : undefined,
      },
      paramsSerializer: { indexes: null },
    }).then(data),
  createRecord: (key: string, values: RecordValues, hostKey?: string, details?: DetailsPayload, idempotencyKey?: string) =>
    http.post<DynamicRecord>(`/modules/${key}/records`, { values, hostKey, details }, idem(idempotencyKey)).then(data),
  getRecord: (id: number) => http.get<DynamicRecord>(`/records/${id}`).then(data),
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
  exportRecords: async (key: string, format: 'csv' | 'xlsx', { filter, sort, q }: ExportQuery, locale: string) => {
    const r = await http.get<Blob>(`/modules/${key}/export`, {
      params: {
        format, filter, q, locale,
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
  runReport: (id: number, range: ReportDateRange | null, locale: string) =>
    http.post<ReportResult>(`/reports/${id}/run`, { range }, { params: { locale } }).then(data),
  drillReport: (id: number, groupKeys: string[], range: ReportDateRange | null, locale: string) =>
    http.post<DrillResult>(`/reports/${id}/drill`, { groupKeys, range }, { params: { locale } }).then(data),
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
  exportReport: async (id: number, locale: string) => {
    const r = await http.get<Blob>(`/reports/${id}/export`, { params: { locale }, responseType: 'blob', validateStatus: () => true });
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
  getPrintTemplates: (moduleKey: string) => http.get<PrintTemplateOption[]>(`/modules/${moduleKey}/print-templates`).then(data),
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
  getTaskRules: (moduleKey: string) => http.get<TaskRule[]>(`/modules/${moduleKey}/task-rules`).then(data)
    .then((list) => list.map(normalizeTaskRule)),
  createTaskRule: (moduleKey: string, body: TaskRuleInput) =>
    http.post<TaskRule>(`/modules/${moduleKey}/task-rules`, body).then(data).then(normalizeTaskRule),
  updateTaskRule: (moduleKey: string, ruleKey: string, body: TaskRuleInput) =>
    http.put<TaskRule>(`/modules/${moduleKey}/task-rules/${ruleKey}`, body).then(data).then(normalizeTaskRule),
  deleteTaskRule: (moduleKey: string, ruleKey: string) => http.delete(`/modules/${moduleKey}/task-rules/${ruleKey}`).then(data),
  applyTaskRule: (recordId: number, ruleKey: string) =>
    http.post<RecordTasks>(`/records/${recordId}/task-rules/${ruleKey}/apply`).then(data).then(normalizeRecordTasks),
  getTaskSummary: () => http.get<TaskCounts>('/tasks/summary').then(data)
    .then((c) => ({ dueToday: c.dueToday ?? 0, overdue: c.overdue ?? 0 })),
  getNotifications: (take = 20) =>
    http.get<NotificationList>('/notifications', { params: { take } }).then(data).then(normalizeNotificationList),
  markNotificationRead: (id: number) => http.post(`/notifications/${id}/read`).then(data),
  markAllNotificationsRead: () => http.post('/notifications/read-all').then(data),
  getTask: (id: number) => http.get<TaskDetail>(`/tasks/${id}`).then(data).then(normalizeTaskDetail),
  createTask: (body: TaskInput) => http.post<TaskDetail>('/tasks', body).then(data).then(normalizeTaskDetail),
  /** A property left out stays as it is; an explicit null clears it, so the patch is sent as given. */
  updateTask: (id: number, patch: TaskPatch) => http.patch<TaskDetail>(`/tasks/${id}`, patch).then(data).then(normalizeTaskDetail),
  deleteTask: (id: number) => http.delete(`/tasks/${id}`).then(data),
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
