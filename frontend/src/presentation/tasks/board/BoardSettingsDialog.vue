<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, reactive, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useCreateTaskView, useUpdateTaskView } from 'src/application/dynamic/queries/task-board-queries';
import { useUsers } from 'src/application/dynamic/queries/permissions-queries';
import {
  BOARD_DUE_RANGES, BOARD_RANGES_WITH_N, BOARD_SORT_KEYS, BOARD_SOURCES, BOARD_STATUSES, buildBoardConfig, changeGroupBy, DEFAULT_RANGE_N, formFromConfig,
  MAX_BOARD_NAME, MAX_EXPLICIT_ASSIGNEES, MAX_KEYWORD, MAX_RANGE_N, moveInList, rangeNInvalid, saveAsConfig, scopeOptions, setFilterAssignees, type BoardForm,
} from 'src/domain/dynamic/task-board-config';
import type { BoardGroupBy, TaskBoardConfig, TaskViewDetail } from 'src/domain/dynamic/task-board-types';
import GenericSelect from 'src/components/framework/GenericSelect.vue';
import type { ApiError } from 'src/services/api/http';

/**
 * 看板設定（D4）。「套用」只影響目前畫面（不寫回、頁面標「（未儲存）」）；「儲存」才寫回，只有自己的存檔看板有；
 * 預設看板與分享給我的看板只有「套用」與「另存為新看板」。欄順序用上下移按鈕、不拖拉。
 * 範圍照原設定保留（D7）：沒有部屬的人不能「新選」部屬的任務，但原本就是這個範圍的設定仍列出它、套用與儲存都不改範圍，
 * 只有「另存為新看板」才換成「指派給我」。伺服器的錯誤文字不顯示。
 * 儲存用的 rowVersion 是開啟當下的版本：409 之後頁面會重抓，開著的對話框不偷偷改用新版本，重新開啟才會拿到。
 */
const props = defineProps<{
  modelValue: boolean;
  config: TaskBoardConfig;
  /** 目前看板名稱：「儲存」會帶上、「另存為新看板」的預設名稱。 */
  name: string;
  viewId: number | null;
  rowVersion: string;
  /** 目前看板是自己的存檔看板（才能「儲存」）。 */
  canSave: boolean;
  hasSubordinates: boolean;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: boolean];
  apply: [config: TaskBoardConfig];
  saved: [detail: TaskViewDetail];
  savedAsNew: [detail: TaskViewDetail];
}>();
const { t } = useI18n();
const users = useUsers();
const update = useUpdateTaskView();
const create = useCreateTaskView();

const form = reactive<BoardForm>(formFromConfig(props.config));
let openedRowVersion = props.rowVersion;
const nameDraft = ref(props.name);
const error = ref('');
const rangeError = ref('');
function reset() {
  Object.assign(form, formFromConfig(props.config));
  openedRowVersion = props.rowVersion;
  nameDraft.value = props.name;
  error.value = '';
  rangeError.value = '';
}
watch(() => props.modelValue, (open) => { if (open) reset(); }, { immediate: true });

const built = computed(() => buildBoardConfig(form));
const trimmedName = computed(() => nameDraft.value.trim());
const nameError = ref('');
const busy = computed(() => update.isPending.value || create.isPending.value);

const userLabel = (id: string) => users.data.value?.find((u) => u.userId === id)?.displayName ?? id;
const userOptions = computed(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: u.displayName })));
const columnLabel = (key: string) => (key === 'unassigned' ? t('tasks.unassigned') : form.groupBy === 'status' ? t(`tasks.status.${key}`) : t(`taskBoard.due.${key}`));
const GROUPS: BoardGroupBy[] = ['status', 'assignee', 'dueBucket'];
const scopes = computed(() => scopeOptions(props.hasSubordinates, props.config.scope));
const noSubordinates = computed(() => !props.hasSubordinates && form.scope === 'subordinates');
const scopeLabel = (s: string) => (s === 'subordinates' && !props.hasSubordinates ? t('taskBoard.settings.scopeOptionNoSubordinates') : t(`taskBoard.settings.scopeOption.${s}`));
const rangeOptions = computed(() => [{ value: '', label: t('taskBoard.settings.dueRangeNone') }, ...BOARD_DUE_RANGES.map((k) => ({ value: k, label: t(`reports.designer.range.${k}`) }))]);
const sortKeyOptions = computed(() => BOARD_SORT_KEYS.map((k) => ({ value: k, label: t(`taskBoard.settings.sortKey.${k}`) })));
const addableUsers = computed(() => userOptions.value.filter((u) => !form.assignees.includes(u.value)));

const toggle = (list: string[], key: string) => { const i = list.indexOf(key); if (i >= 0) list.splice(i, 1); else list.push(key); };
const isShown = (key: string) => !form.hidden.includes(key);
const pickRange = (kind: string) => {
  form.dueRange = kind ? { kind, n: BOARD_RANGES_WITH_N.includes(kind) ? (form.dueRange?.n ?? DEFAULT_RANGE_N) : null } : null;
};
function addAssignee(v: string | string[] | null) {
  if (typeof v === 'string' && v && form.assignees.length < MAX_EXPLICIT_ASSIGNEES && !form.assignees.includes(v)) form.assignees.push(v);
}
const close = () => emit('update:modelValue', false);

/** 「近 N 天」的 N 不合格就不送出（後端會回 400），在欄位上說明。 */
function rangeOk(): boolean {
  rangeError.value = rangeNInvalid(form) ? t('taskBoard.settings.rangeNInvalid') : '';
  return !rangeError.value;
}
function apply() {
  if (!rangeOk()) return;
  emit('apply', built.value);
  close();
}
function failure(e: unknown): string {
  const err = e as ApiError;
  return err.status === 409 ? t('taskBoard.settings.stale') : t('taskBoard.settings.saveFailed');
}
function requireName(): boolean {
  nameError.value = trimmedName.value ? '' : t('taskBoard.picker.nameRequired');
  return !nameError.value;
}
function save() {
  if (props.viewId === null || !requireName() || !rangeOk()) return;
  error.value = '';
  update.mutate({ id: props.viewId, name: trimmedName.value, config: built.value, rowVersion: openedRowVersion }, {
    onSuccess: (detail) => { Notify.create({ type: 'positive', message: t('taskBoard.settings.saved') }); emit('saved', detail); close(); },
    onError: (e) => { error.value = failure(e); },
  });
}
function saveAs() {
  if (!requireName() || !rangeOk()) return;
  error.value = '';
  create.mutate({ name: trimmedName.value, config: saveAsConfig(built.value, props.hasSubordinates) }, {
    onSuccess: (detail) => { Notify.create({ type: 'positive', message: t('taskBoard.picker.created', { name: detail.name }) }); emit('savedAsNew', detail); close(); },
    onError: (e) => { error.value = failure(e); },
  });
}
</script>

<template>
  <q-dialog :model-value="modelValue" :maximized="$q.screen.lt.sm" @update:model-value="(v) => emit('update:modelValue', v)">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 640px; max-width: 95vw'" class="column no-wrap" data-testid="board-settings">
      <q-card-section class="text-h6">{{ t('taskBoard.settings.title') }}</q-card-section>
      <q-card-section class="col scroll q-pt-none column no-wrap q-gutter-y-md" style="overflow-x: hidden" data-testid="settings-body">
        <div v-if="!canSave" class="text-caption text-muted" data-testid="settings-readonly-note">{{ t('taskBoard.settings.readonlyNote') }}</div>

        <q-input
          v-model="nameDraft" dense outlined :maxlength="MAX_BOARD_NAME" :label="t('taskBoard.settings.name')" :error="!!nameError" :error-message="nameError"
          data-testid="settings-name"
        />

        <fieldset class="settings-group">
          <legend class="text-subtitle2">{{ t('taskBoard.settings.groupBy') }}</legend>
          <div class="row q-gutter-x-md">
            <q-radio
              v-for="g in GROUPS" :key="g" :model-value="form.groupBy" :val="g" dense :label="t(`taskBoard.settings.group.${g}`)"
              :data-testid="`settings-group-${g}`" @update:model-value="changeGroupBy(form, g)"
            />
          </div>
        </fieldset>

        <fieldset class="settings-group">
          <legend class="text-subtitle2">{{ t('taskBoard.settings.scope') }}</legend>
          <div class="row q-gutter-x-md">
            <q-radio v-for="s in scopes" :key="s" v-model="form.scope" :val="s" dense :label="scopeLabel(s)" :data-testid="`settings-scope-${s}`" />
          </div>
          <div v-if="noSubordinates" class="text-caption text-muted q-mt-xs" data-testid="settings-scope-note">{{ t('taskBoard.settings.scopeNoSubordinates') }}</div>
        </fieldset>

        <fieldset class="settings-group">
          <legend class="text-subtitle2">{{ t('taskBoard.settings.filters') }}</legend>
          <div class="column q-gutter-y-sm">
            <div>
              <div class="text-caption text-muted">{{ t('taskBoard.settings.statuses') }}</div>
              <div class="row q-gutter-x-md">
                <q-checkbox
                  v-for="s in BOARD_STATUSES" :key="s" v-model="form.statuses" :val="s" dense :label="t(`tasks.status.${s}`)"
                  :disable="form.groupBy === 'dueBucket'" :data-testid="`settings-status-${s}`"
                />
              </div>
              <div v-if="form.groupBy === 'dueBucket'" class="text-caption text-muted" data-testid="settings-due-note">{{ t('taskBoard.settings.dueBucketNote') }}</div>
            </div>
            <GenericSelect
              :model-value="form.assigneeUserIds" :label="t('taskBoard.settings.filterAssignees')" :options="userOptions" multiple
              data-testid="settings-filter-assignees" @update:model-value="(v) => setFilterAssignees(form, (v as string[] | null) ?? [])"
            />
            <q-checkbox v-model="form.includeUnassigned" dense :label="t('taskBoard.settings.includeUnassigned')" data-testid="settings-include-unassigned" />
            <GenericSelect
              :model-value="form.creatorUserIds" :label="t('taskBoard.settings.creators')" :options="userOptions" multiple
              data-testid="settings-filter-creators" @update:model-value="(v) => (form.creatorUserIds = (v as string[] | null) ?? [])"
            />
            <div>
              <div class="text-caption text-muted">{{ t('taskBoard.settings.sources') }}</div>
              <div class="row q-gutter-x-md">
                <q-checkbox
                  v-for="s in BOARD_SOURCES" :key="s" v-model="form.sources" :val="s" dense :label="t(`taskBoard.settings.source.${s}`)"
                  :data-testid="`settings-source-${s}`"
                />
              </div>
            </div>
            <div class="row q-col-gutter-sm">
              <q-select
                class="col-12 col-sm" dense outlined emit-value map-options :label="t('taskBoard.settings.dueRange')" :options="rangeOptions"
                :model-value="form.dueRange?.kind ?? ''" data-testid="settings-due-range" @update:model-value="pickRange"
              />
              <q-input
                v-if="form.dueRange && BOARD_RANGES_WITH_N.includes(form.dueRange.kind)" class="col-12 col-sm-3" dense outlined type="number" min="1" :max="MAX_RANGE_N"
                :label="t('reports.designer.rangeN')" :model-value="form.dueRange.n ?? ''" :error="!!rangeError" :error-message="rangeError" data-testid="settings-due-range-n"
                @update:model-value="(v) => { form.dueRange!.n = v === '' || v === null ? null : Number(v); rangeError = ''; }"
              />
            </div>
            <q-input v-model="form.keyword" dense outlined :maxlength="MAX_KEYWORD" :label="t('taskBoard.settings.keyword')" data-testid="settings-keyword" />
          </div>
        </fieldset>

        <fieldset class="settings-group">
          <legend class="text-subtitle2">{{ t('taskBoard.settings.columns') }}</legend>
          <ul class="column q-gutter-y-xs settings-list">
            <li
              v-for="(key, i) in form.order" :key="key" class="row items-center no-wrap" :data-testid="`settings-col-row-${key}`"
            >
              <q-checkbox
                :model-value="isShown(key)" dense class="col" :label="columnLabel(key)" :aria-label="t('taskBoard.settings.columnShow', { name: columnLabel(key) })"
                :data-testid="`settings-col-show-${key}`" @update:model-value="toggle(form.hidden, key)"
              />
              <template v-if="form.groupBy !== 'assignee'">
                <q-btn
                  flat dense round icon="arrow_upward" :disable="i === 0" :aria-label="t('taskBoard.settings.columnUp', { name: columnLabel(key) })"
                  :data-testid="`settings-col-up-${key}`" @click="form.order = moveInList(form.order, key, -1)"
                />
                <q-btn
                  flat dense round icon="arrow_downward" :disable="i === form.order.length - 1" :aria-label="t('taskBoard.settings.columnDown', { name: columnLabel(key) })"
                  :data-testid="`settings-col-down-${key}`" @click="form.order = moveInList(form.order, key, 1)"
                />
              </template>
            </li>
          </ul>

          <template v-if="form.groupBy === 'assignee'">
            <div class="text-caption text-muted q-mt-sm">{{ t('taskBoard.settings.assignees') }}</div>
            <ul v-if="form.assignees.length" class="column q-gutter-y-xs settings-list">
              <li v-for="(id, i) in form.assignees" :key="id" class="row items-center no-wrap" :data-testid="`settings-assignee-row-${id}`">
                <span class="col ellipsis">{{ userLabel(id) }}</span>
                <q-btn
                  flat dense round icon="arrow_upward" :disable="i === 0" :aria-label="t('taskBoard.settings.columnUp', { name: userLabel(id) })"
                  :data-testid="`settings-assignee-up-${id}`" @click="form.assignees = moveInList(form.assignees, id, -1)"
                />
                <q-btn
                  flat dense round icon="arrow_downward" :disable="i === form.assignees.length - 1" :aria-label="t('taskBoard.settings.columnDown', { name: userLabel(id) })"
                  :data-testid="`settings-assignee-down-${id}`" @click="form.assignees = moveInList(form.assignees, id, 1)"
                />
                <q-btn
                  flat dense round icon="close" :aria-label="t('taskBoard.settings.assigneeRemove', { name: userLabel(id) })"
                  :data-testid="`settings-assignee-remove-${id}`" @click="form.assignees = form.assignees.filter((x) => x !== id)"
                />
              </li>
            </ul>
            <div v-else class="text-muted text-caption">{{ t('taskBoard.settings.assigneesNone') }}</div>
            <GenericSelect
              class="q-mt-sm" :model-value="null" :label="t('taskBoard.settings.assigneesAdd')" :options="addableUsers"
              :readonly="form.assignees.length >= MAX_EXPLICIT_ASSIGNEES" data-testid="settings-assignee-add" @update:model-value="addAssignee"
            />
            <q-checkbox v-model="form.autoAppend" dense class="q-mt-sm" :label="t('taskBoard.settings.autoAppend')" data-testid="settings-auto-append" />
            <div class="text-caption text-muted">{{ t('taskBoard.settings.autoAppendHint') }}</div>
          </template>
        </fieldset>

        <fieldset class="settings-group">
          <legend class="text-subtitle2">{{ t('taskBoard.settings.sort') }}</legend>
          <div class="row q-col-gutter-sm items-center">
            <q-select
              v-model="form.sortKey" class="col-12 col-sm" dense outlined emit-value map-options :options="sortKeyOptions"
              :label="t('taskBoard.settings.sort')" data-testid="settings-sort-key"
            />
            <div class="col-12 col-sm-auto row q-gutter-x-md">
              <q-radio v-model="form.sortDir" val="asc" dense :label="t('taskBoard.settings.sortDir.asc')" data-testid="settings-sort-dir-asc" />
              <q-radio v-model="form.sortDir" val="desc" dense :label="t('taskBoard.settings.sortDir.desc')" data-testid="settings-sort-dir-desc" />
            </div>
          </div>
        </fieldset>

        <div v-if="error" class="text-negative" role="alert" data-testid="settings-error">{{ error }}</div>
      </q-card-section>
      <q-card-actions align="right" class="q-gutter-x-xs">
        <q-btn flat no-caps :label="t('taskBoard.settings.close')" data-testid="settings-cancel" @click="close" />
        <q-btn outline no-caps color="primary" :label="t('taskBoard.settings.saveAs')" :disable="busy" data-testid="settings-save-as" @click="saveAs" />
        <q-btn v-if="canSave" outline no-caps color="primary" :label="t('taskBoard.settings.save')" :loading="update.isPending.value" :disable="busy" data-testid="settings-save" @click="save" />
        <q-btn color="primary" no-caps :label="t('taskBoard.settings.apply')" :disable="busy" data-testid="settings-apply" @click="apply" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.settings-group { border: 1px solid var(--t-border); border-radius: 4px; padding: 8px 12px; margin: 0; min-width: 0; }
.settings-group legend { padding: 0 4px; }
.settings-list { list-style: none; margin: 0; padding: 0; }
</style>
