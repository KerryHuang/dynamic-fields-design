export type Locale = 'zh-TW' | 'en-US' | 'th' | 'vi';
export type I18nText = Partial<Record<Locale, string>>;

export type FieldType =
  | 'text' | 'textarea' | 'integer' | 'decimal' | 'currency' | 'percent'
  | 'date' | 'datetime' | 'time' | 'boolean' | 'email' | 'phone' | 'url'
  | 'enum' | 'multienum' | 'user' | 'formula' | 'autonumber' | 'reference' | 'rollup';

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
  /** rollup: aggregates child records; resultType and scale are derived by the server. */
  rollup?: {
    sourceFieldId: number;
    function: 'sum' | 'count' | 'min' | 'max';
    aggregateFieldId?: number;
    condition?: ConditionNode;
  };
}

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
}

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

export interface ModuleSchema {
  key: string;
  programCode: string;
  name: I18nText;
  icon?: string;
  /** null when the title field is hidden for the current user. */
  titleFieldKey: string | null;
  version: number;
  /** Absent means standalone. */
  kind?: 'standalone' | 'extension';
  hostEntityKey?: string;
  permissions: SchemaPermissions;
  blocks: SchemaBlock[];
  rules: SchemaRule[];
  /** Active detail modules of this header; absent when there are none. */
  details?: SchemaDetail[];
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

export interface Me {
  userId: string;
  displayName: string;
  mustChangePassword: boolean;
  isSystemAdmin: boolean;
  groups: { id: number; key: string; name: I18nText }[];
  designer: { read: boolean; add: boolean; edit: boolean; delete: boolean };
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
  | 'contains' | 'startsWith' | 'withinNextDays' | 'withinLastDays' | 'isEmpty' | 'isNotEmpty';

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

export interface GroupPermission { groupId: number; privileges: Privileges; scope?: DataScope | null }
export interface UserPermission { userId: string; privileges: Privileges }
export interface FieldPermission { fieldId: number; groupId: number; access: 'hidden' | 'readOnly' }
export interface OptionScope { optionId: number; groupIds: number[] }

export interface ModulePermissions {
  groups: GroupPermission[];
  users: UserPermission[];
  fields: FieldPermission[];
  options: OptionScope[];
}

export interface Group { id: number; key: string; name: I18nText; members: string[] }

export interface ImportColumn { header: string; fieldKey?: string; ignoredReason?: string }
/** `errors` keys are field keys or '_record'. */
export interface ImportRowError { line: number; errors: Record<string, string[]> }
export interface ImportPreview { columns: ImportColumn[]; total: number; valid: number; errors: ImportRowError[] }
export interface ImportResult { created: number }
