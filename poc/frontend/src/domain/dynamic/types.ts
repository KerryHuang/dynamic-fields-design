import type { MeTheme } from 'src/domain/theme/theme-model';

export type Locale = 'zh-TW' | 'en-US' | 'th' | 'vi';
export type I18nText = Partial<Record<Locale, string>>;

export type FieldType =
  | 'text' | 'textarea' | 'integer' | 'decimal' | 'currency' | 'percent'
  | 'date' | 'datetime' | 'time' | 'boolean' | 'email' | 'phone' | 'url'
  | 'enum' | 'multienum' | 'user' | 'formula' | 'autonumber' | 'reference' | 'rollup' | 'attachment';

/** What a user picks from when printing a record; the body stays with the designer. */
export interface PrintTemplateOption {
  key: string;
  name: I18nText;
}

export interface PrintTemplateDefinition extends PrintTemplateOption {
  /** Sanitized HTML with `{{f_12}}` placeholders and `{{#each detail-module-key}}…{{/each}}` loops. */
  body: string;
  active: boolean;
}

/** One file in an attachment field's value; the server owns id, name and size. */
export interface AttachmentFile {
  id: string;
  name: string;
  size: number;
}

export interface AttachmentSettings {
  uploadEnabled: boolean;
  /** Why uploads are off, when the operator gave a reason; the UI falls back to a generic localized text. */
  disabledReason?: string;
  maxBytes: number;
  allowedExtensions: string[];
  maxFilesPerField: number;
}

export interface UploadedAttachment extends AttachmentFile {
  /** The record's new rowVersion: an upload is saved on its own and bumps it. */
  rowVersion: string;
}

export interface FieldConfig {
  required: boolean;
  readOnly: boolean;
  maxLength?: number;
  precision?: number;
  scale?: number;
  /** formula: expression with `{<fieldId>}` references. */
  formula?: string;
  resultType?: 'number' | 'currency' | 'date';
  /** autonumber: e.g. REP-{yyyyMM}-{0000}. */
  format?: string;
  defaultValue?: unknown;
  /** date: new records start with the (local) today; excludes defaultValue. */
  defaultToday?: boolean;
  /** Detail modules: new rows take the current value of this header-module field; excludes defaultValue and defaultToday. */
  parentDefaultFieldId?: number;
  showInList: boolean;
  isFilterable: boolean;
  isSortable: boolean;
  /** 表單上佔滿整列；未設定時依型別預設（textarea 佔滿）。 */
  fullWidth?: boolean;
  /** reference: the target is fixed at creation. */
  refTarget?: { kind: 'module' | 'entity'; key: string };
  onDelete?: 'restrict' | 'setNull' | 'cascade';
  /** reference: `detail` makes the referencing module a detail (line) module of the target. */
  relation?: 'lookup' | 'detail';
  /** autonumber: `parent` numbers within the header record (detail modules only). */
  scope?: 'parent';
  /** reference (module target): copies values of the picked record into this record. */
  fill?: FillConfig;
  /** rollup: aggregates child records; resultType and scale are derived by the server. */
  rollup?: {
    sourceFieldId: number;
    function: 'sum' | 'count' | 'min' | 'max';
    aggregateFieldId?: number;
    condition?: ConditionNode;
  };
}

/** `from` is a field Id of the referenced module, `to` a field Id of this module. */
export interface FillMap { from: number; to: number }
export interface FillConfig { mode: 'lock' | 'default'; map: FillMap[] }

export interface SchemaOption {
  /** opt_<id> */
  value: string;
  label: I18nText;
  color?: string;
  active: boolean;
}

export interface SchemaReference {
  kind: 'module' | 'entity';
  key: string;
  name: I18nText;
  /** Relative to the API base (/api/v3/dynamic). */
  lightUrl: string;
}

export interface SchemaField {
  id: number;
  key: string;
  type: FieldType;
  label: I18nText;
  config: FieldConfig;
  options: SchemaOption[];
  reference?: SchemaReference;
  /** 凍結的貢獻欄位：貢獻它的應用程式已關閉，任何人都不能改（共用主檔）。 */
  lockedBy?: FieldLock | null;
}

/** 欄位屬於哪個已關閉的應用程式。 */
export interface FieldLock { appKey: string; appName: I18nText }

export interface SchemaBlock {
  id: number;
  label: I18nText;
  collapsed: boolean;
  /** 表單每列欄數 1 到 3。 */
  columns: number;
  fields: SchemaField[];
}

export interface SchemaPermissions {
  read: boolean;
  add: boolean;
  edit: boolean;
  delete: boolean;
  printExport: boolean;
  pricing: boolean;
}

export type RuleKind = 'visibility' | 'required' | 'readOnly' | 'validation' | 'optionFilter';

export interface SchemaRule {
  kind: RuleKind;
  condition: ConditionNode;
  targets: string[];
  options: string[];
  message?: I18nText;
}

/** Designer-side rule (PUT /modules/{key}/rules). */
export interface Rule {
  id?: number;
  kind: RuleKind;
  condition: ConditionNode;
  targets: string[];
  optionIds: number[];
  message?: I18nText;
  active: boolean;
}

export interface SchemaDetail {
  moduleKey: string;
  name: I18nText;
  parentFieldId: number;
  parentFieldKey: string;
  titleFieldKey: string | null;
  fields: SchemaField[];
  rules: SchemaRule[];
}

/** State values are opt_<id>; transitions are already filtered to what the current user may do. */
export interface SchemaWorkflow {
  statusFieldKey: string;
  initialState: string;
  states: { value: string; readOnly: boolean }[];
  transitions: { from: string; to: string }[];
}

export interface ModuleSchema {
  key: string;
  programCode: string;
  name: I18nText;
  icon?: string;
  /** null when the title field is hidden for the current user. */
  titleFieldKey: string | null;
  /** The title is composed from a template: the list's title column is "標題" and sorting the title field orders by the stored title. */
  hasTitleTemplate?: boolean;
  version: number;
  /** Absent means standalone. */
  kind?: 'standalone' | 'extension';
  hostEntityKey?: string;
  permissions: SchemaPermissions;
  /** The module's app is closed; add/edit/delete are already false. */
  readOnly?: boolean;
  /** Ledger module: records cannot be edited or deleted, only reversed; edit/delete are already false. */
  ledger?: boolean;
  blocks: SchemaBlock[];
  rules: SchemaRule[];
  /** Active detail modules of this header; absent when there are none. */
  details?: SchemaDetail[];
  /** Absent when the module has no workflow. */
  workflow?: SchemaWorkflow;
}

export interface ReferenceInfo {
  key: string;
  /** The server omits null properties, so this may be absent. */
  title?: string | null;
  state: 'ok' | 'missing' | 'restricted';
}

export interface LightItem { key: string; title: string }
export interface LightPage { items: LightItem[]; total: number }

export interface RelatedTab {
  fieldId: number;
  fieldKey: string;
  fieldLabel: I18nText;
  moduleKey: string;
  moduleName: I18nText;
  canAdd: boolean;
}

export interface BlockingRef {
  moduleKey: string;
  moduleName: I18nText;
  fieldLabel: I18nText;
  recordId: number;
  title?: string | null;
}

/** 409 body of DELETE /records/{id} when other records still reference it. */
export interface DeleteBlocked {
  message: string;
  references: BlockingRef[];
  total: number;
}

export interface EntitySummary { key: string; name: I18nText }

export interface DynamicRecord {
  id: number;
  moduleKey: string;
  title: string;
  values: Record<string, unknown>;
  references: Record<string, ReferenceInfo>;
  hostKey: string | null;
  ownerUserId: string;
  createdUserId: string;
  createdTime: string;
  modifiedUserId: string;
  modifiedTime: string;
  rowVersion: string;
  /** Already posted by an event: only the status field may change, and it cannot be deleted. */
  frozenByEffects?: boolean;
  /** 凍結的貢獻欄位，依欄位 key。 */
  lockedBy?: Record<string, FieldLock>;
  /** Detail rows by child module key; present on GET record of a header. */
  details?: Record<string, DynamicRecord[]>;
}

/** One detail row in a save body; omitted rows are left untouched by the server. */
export interface DetailRowPayload {
  id?: number;
  values?: Record<string, unknown>;
  rowVersion?: string;
  _delete?: true;
}

export interface RollupSource {
  fieldId: number;
  fieldLabel: I18nText;
  moduleKey: string;
  moduleName: I18nText;
  relation: 'lookup' | 'detail';
}

export interface PagedResponse<T> {
  items: T[];
  totalCount: number;
  currentPage: number;
  pagesSize: number;
}

export interface RecordHistoryEntry {
  id: number;
  operation: 'create' | 'update' | 'delete';
  fieldKey?: string;
  fieldLabel?: I18nText;
  oldText?: string;
  newText?: string;
  changedUserId: string;
  changedUserName: string;
  changedTime: string;
  detail?: { moduleKey: string; moduleName: I18nText; recordId: number; title: string };
}

export interface MenuItem {
  key: string;
  name: I18nText;
  icon?: string;
  section?: string;
  /** The owning app is closed: data stays readable, nothing can be written. */
  closed?: boolean;
}

export interface User {
  userId: string;
  displayName: string;
}

export interface AuthUser {
  userId: string;
  displayName: string;
  mustChangePassword: boolean;
}

export interface AuthOptions {
  demoLoginEnabled: boolean;
  demoGenerating: boolean;
  demoAccounts: User[];
}

export interface AdminUser {
  userId: string;
  displayName: string;
  isActive: boolean;
  isSystemAdmin: boolean;
  isDemoAccount: boolean;
  isLocked: boolean;
  groupIds: number[];
}
export interface AdminUserUpdate { displayName: string; groupIds: number[]; isSystemAdmin: boolean }
export interface AdminUserCreate extends AdminUserUpdate { userId: string; password: string }

export interface AdminGroup { id: number; key: string; nameI18n: I18nText; memberUserIds: string[] }
export interface BlockingOption { module: string; field: string; option: string }
export interface GroupImpact {
  memberCount: number;
  modules: string[];
  fieldPermissionCount: number;
  dataScopeCount: number;
  blockingOptions: BlockingOption[];
  /** 分享給這個群組的報表名稱（刪除群組會一併移除分享）。 */
  reports: string[];
  /** 指派給這個群組的儀表板名稱（刪除群組會一併移除指派）。 */
  dashboards: string[];
}

export interface Me {
  userId: string;
  displayName: string;
  mustChangePassword: boolean;
  isSystemAdmin: boolean;
  /** Demo accounts have no password, so changing it always fails. */
  isDemoAccount?: boolean;
  groups: { id: number; key: string; name: I18nText }[];
  designer: { read: boolean; add: boolean; edit: boolean; delete: boolean };
  theme: MeTheme;
}

/** GetModule response: every status of field and option, for the designer. */
export interface DesignerOption {
  id: number;
  /** opt_<id> */
  value: string;
  label: I18nText;
  color?: string;
  sequence: number;
  active: boolean;
}

export interface DesignerField {
  id: number;
  key: string;
  type: FieldType;
  label: I18nText;
  config: FieldConfig;
  blockId: number;
  sequence: number;
  status: 'Active' | 'Inactive';
  options: DesignerOption[];
}

export interface DesignerBlock {
  id: number;
  label: I18nText;
  sequence: number;
  collapsed: boolean;
  columns: number;
  fields: DesignerField[];
}

export interface ModuleDetail {
  id: number;
  key: string;
  programCode: string;
  name: I18nText;
  icon?: string;
  section?: string;
  titleFieldId?: number;
  /** Title template with field ids in braces, e.g. `{12} {13}`; absent means the title is the title field. */
  titleTemplate?: string;
  status: 'Draft' | 'Active' | 'Inactive';
  version: number;
  /** Absent means standalone. */
  kind?: 'standalone' | 'extension';
  hostEntityKey?: string;
  /** Header module key when this is a detail module. */
  detailOf?: string;
  blocks: DesignerBlock[];
}

export type FilterOperator =
  | 'eq' | 'ne' | 'in' | 'notIn' | 'gt' | 'gte' | 'lt' | 'lte' | 'between'
  | 'contains' | 'startsWith' | 'withinNextDays' | 'withinLastDays' | 'beforeToday' | 'isEmpty' | 'isNotEmpty';

/** `field` is Field.Id (0 or undefined while no field is picked in the editor). */
export interface FieldCondition {
  field: number;
  op: FilterOperator;
  /** `{ fieldRef }` compares against another field's value. */
  value?: unknown | { fieldRef: number };
}
export type ConditionGroup = { all: ConditionNode[] } | { any: ConditionNode[] };
export type ConditionNode = ConditionGroup | FieldCondition;

export interface ViewSort {
  /** Field.Id */
  field: number;
  order: 'asc' | 'desc';
}

export interface ListView {
  id: number;
  name: I18nText;
  shared: boolean;
  isOwner: boolean;
  /** Field.Id[] */
  columns: number[];
  /** Omitted by the backend when null. */
  filter?: ConditionNode;
  sort: ViewSort[];
  isDefault: boolean;
  staleFieldIds: number[];
}

export type DataScope = 'all' | 'group' | 'own';

export interface Privileges {
  read: boolean;
  add: boolean;
  edit: boolean;
  delete: boolean;
  printExport: boolean;
  pricing: boolean;
  approve: boolean;
}

export interface GroupPermission {
  groupId: number;
  privileges: Privileges;
  scope?: DataScope | null;
  /** 共用主檔模組：每個動作各自的資料範圍。 */
  actionScopes?: ActionScope[] | null;
}
export interface ActionScope { action: keyof Privileges; scope: DataScope }
/** 共用主檔上限定可見的貢獻欄位：`groupIds` 是管理員手動加的群組，`groups` 是全部可見群組與來源。 */
export interface FieldVisibility {
  fieldId: number;
  groupIds: number[];
  groups?: { groupId: number; source: 'app' | 'master' | 'manual'; appKey: string | null }[];
}
export interface UserPermission { userId: string; privileges: Privileges }
export interface FieldPermission { fieldId: number; groupId: number; access: 'hidden' | 'readOnly' }
export interface OptionScope { optionId: number; groupIds: number[] }

export interface ModulePermissions {
  groups: GroupPermission[];
  users: UserPermission[];
  fields: FieldPermission[];
  options: OptionScope[];
  /** 共用主檔模組：群組逐動作設定範圍，並可另外授予貢獻欄位的可見群組。 */
  sharedMaster?: boolean;
  fieldGrants?: FieldVisibility[] | null;
}

export interface Group { id: number; key: string; name: I18nText; members: string[] }

export interface ImportColumn { header: string; fieldKey?: string; ignoredReason?: string }
/** `errors` keys are field keys or '_record'. */
export interface ImportRowError { line: number; errors: Record<string, string[]> }
export interface ImportPreview { columns: ImportColumn[]; total: number; valid: number; errors: ImportRowError[] }
export interface ImportResult { created: number }

/** 安全刪除的目標：模組用 key，欄位與區塊用 id。 */
export type DeleteTargetRef = { kind: 'module'; key: string } | { kind: 'field' | 'block'; id: number };

export type DeleteBlockerKind =
  | 'moduleActive' | 'liveRecords' | 'fieldActive' | 'fieldData' | 'titleField' | 'lastBlock' | 'referenceField'
  | 'rollup' | 'formula' | 'parentDefault' | 'fill' | 'workflow' | 'conversion' | 'rule' | 'view' | 'report' | 'dashboard' | 'personalDashboard';

/** 擋下刪除的原因或引用來源；count 只用於 liveRecords 與 fieldData。 */
export interface DeleteBlocker {
  kind: DeleteBlockerKind;
  moduleKey?: string | null;
  id?: number | null;
  name: string;
  count?: number | null;
}

/** 刪除預檢；purge 是刪除會永久清除的軟刪除記錄與其索引、歷程筆數。 */
export interface DeleteCheck {
  canDelete: boolean;
  blockers: DeleteBlocker[];
  purge: { records: number; indexRows: number; historyRows: number };
}

/** Designer-side workflow (PUT /modules/{key}/workflow); ids are Field.Id / Option.Id, empty `groupIds` means anyone with Edit. */
export interface Workflow {
  statusFieldId: number;
  initialOptionId: number;
  states: { optionId: number; readOnly: boolean }[];
  transitions: { from: number; to: number; groupIds: number[] }[];
}

export interface ConversionLines {
  sourceModuleKey: string;
  targetModuleKey: string;
  sourceLineFieldId: number;
  quantity: { from: number; to: number };
  copy: FillMap[];
}

export interface ConversionDefinition {
  /** Option ids of the source status field; empty means any state. */
  sourceStates: number[];
  header: { sourceFieldId: number; copy: FillMap[] };
  lines?: ConversionLines;
}

export interface Conversion {
  key: string;
  name: I18nText;
  sourceModuleKey: string;
  targetModuleKey: string;
  active: boolean;
  definition: ConversionDefinition;
}

export interface ConversionLineStatus {
  lineId: number;
  title: string;
  quantity: number;
  converted: number;
  remaining: number;
}

export interface ConvertedTarget { id: number; title: string; moduleKey: string }

/** One conversion as it applies to one source record (GET /records/{id}/conversions); `lines` absent means a header conversion. */
export interface RecordConversion {
  key: string;
  name: I18nText;
  targetModuleKey: string;
  targetName: I18nText;
  allowed: boolean;
  reason?: string;
  lines?: ConversionLineStatus[];
  targets: ConvertedTarget[];
}

export interface ConvertBody {
  rowVersion?: string;
  values?: Record<string, unknown>;
  lines?: { sourceLineId: number; quantity: number }[];
}
