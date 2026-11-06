import type {
  AdminGroup, AdminUser, AdminUserCreate, AdminUserUpdate, AuthOptions, AuthUser, ConditionNode, DesignerField, DetailRowPayload, DynamicRecord, EntitySummary, FieldConfig, FieldType, Group, GroupImpact, I18nText, LightPage, ListView, Me, MenuItem, ModuleDetail, ModulePermissions, ModuleSchema,
  ImportPreview, ImportResult, PagedResponse, RecordHistoryEntry, RelatedTab, Rule, RollupSource, User, ViewSort,
} from 'src/domain/dynamic/types';
import type {
  AdminDashboardDetail, AdminDashboardItem, CardResult, DashboardDetail, DashboardLayout, DashboardList,
} from 'src/domain/dynamic/dashboard-types';
import type { CatalogModule, DrillResult, ModuleCatalog, ReportDateRange, ReportDefinition, ReportDetail, ReportItem, ReportResult, ReportShareTarget } from 'src/domain/dynamic/report-types';
import type { MeTheme, ThemeChoice, ThemeDefaults } from 'src/domain/theme/theme-model';
import { handleAuthFailure, http, toApiError } from './http';

export interface ModuleSummary {
  id: number;
  key: string;
  programCode: string;
  name: I18nText;
  icon?: string;
  section?: string;
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

/** One function per backend endpoint; names are the Task 10 Request names without the `Request` suffix. */
export const dynamicApi = {
  getModules: () => http.get<ModuleSummary[]>('/modules').then(data),
  getModule: (key: string) => http.get<ModuleDetail>(`/modules/${key}`).then(data),
  createModule: (body: { key: string; name: I18nText; icon?: string; section?: string; kind?: 'standalone' | 'extension'; hostEntityKey?: string }) =>
    http.post<ModuleSummary>('/modules', body).then(data),
  updateModule: (key: string, body: { name: I18nText; icon?: string; section?: string; titleFieldId?: number }) =>
    http.put<ModuleSummary>(`/modules/${key}`, body).then(data),
  activateModule: (key: string) => http.post<void>(`/modules/${key}/activate`).then(data),
  deactivateModule: (key: string) => http.post<void>(`/modules/${key}/deactivate`).then(data),

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
  getModulePermissions: (key: string) => http.get<ModulePermissions>(`/modules/${key}/permissions`).then(data),
  saveModulePermissions: (key: string, body: ModulePermissions) =>
    http.put<ModulePermissions>(`/modules/${key}/permissions`, body).then(data),
  getRules: (key: string) => http.get<Rule[]>(`/modules/${key}/rules`).then(data),
  putRules: (key: string, rules: Rule[]) => http.put<Rule[]>(`/modules/${key}/rules`, { rules }).then(data),
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
  createRecord: (key: string, values: RecordValues, hostKey?: string, details?: DetailsPayload) =>
    http.post<DynamicRecord>(`/modules/${key}/records`, { values, hostKey, details }).then(data),
  getRecord: (id: number) => http.get<DynamicRecord>(`/records/${id}`).then(data),
  updateRecord: (id: number, values: RecordValues, rowVersion: string, details?: DetailsPayload) =>
    http.put<DynamicRecord>(`/records/${id}`, { values, rowVersion, details }).then(data),
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
  deleteRecord: (id: number, rowVersion: string) => http.delete<void>(`/records/${id}`, { params: { rowVersion } }).then(data),
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
  commitImport: (key: string, file: File) =>
    http.post<ImportResult>(`/modules/${key}/import`, importForm(file), { params: { commit: true } }).then(data),
};
