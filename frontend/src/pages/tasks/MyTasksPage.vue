<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { clearLastTaskBoard, readLastTaskBoard, writeLastTaskBoard } from 'src/application/dynamic/last-task-board';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useBoardColumns, useTaskView, useTaskViews } from 'src/application/dynamic/queries/task-board-queries';
import { useMyTasks, useTaskCapabilities } from 'src/application/dynamic/queries/tasks-queries';
import type { BoardSource, TaskBoardConfig, TaskViewDetail } from 'src/domain/dynamic/task-board-types';
import { taskStatusColor, type TaskListFilter, type TaskListTab } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import BoardPicker from 'src/presentation/tasks/board/BoardPicker.vue';
import BoardSettingsDialog from 'src/presentation/tasks/board/BoardSettingsDialog.vue';
import TaskBoard from 'src/presentation/tasks/board/TaskBoard.vue';
import TaskGantt from 'src/presentation/tasks/gantt/TaskGantt.vue';
import TaskViewShareDialog from 'src/presentation/tasks/board/TaskViewShareDialog.vue';
import SubtaskProgress from 'src/presentation/dynamic/tasks/SubtaskProgress.vue';
import TaskDialog from 'src/presentation/dynamic/tasks/TaskDialog.vue';
import TaskFormDialog from 'src/presentation/dynamic/tasks/TaskFormDialog.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';

const BASE_TABS: TaskListTab[] = ['assigned', 'created', 'mentioned'];
const ALL_TABS: TaskListTab[] = [...BASE_TABS, 'subordinates'];
const FILTERS: TaskListFilter[] = ['open', 'overdue', 'today', 'thisWeek', 'all'];
const SIZE = 20;

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const identity = useIdentityStore();

const pick = <T extends string>(value: unknown, allowed: T[], fallback: T): T =>
  (allowed as string[]).includes(String(value)) ? (value as T) : fallback;
const capabilities = useTaskCapabilities();
// The subordinates tab exists only when the server says the user manages someone; a link to it from someone without falls back
const hasSubordinates = computed(() => !!capabilities.data.value?.subordinates);
const tabs = computed(() => (hasSubordinates.value ? ALL_TABS : BASE_TABS));
const requestedTab = computed(() => pick(route.query.tab, ALL_TABS, 'assigned'));
const tab = computed(() => (requestedTab.value === 'subordinates' && capabilities.data.value && !hasSubordinates.value ? 'assigned' : requestedTab.value));
const filter = computed(() => pick(route.query.filter, FILTERS, 'open'));
// A notification link is /tasks?open={id}; anything that is not a whole number is ignored.
const openId = computed(() => (/^\d+$/.test(String(route.query.open ?? '')) ? Number(route.query.open) : null));
const page = ref(1);

type ViewMode = 'list' | 'board' | 'gantt';
const mode = computed<ViewMode>(() => (route.query.view === 'board' || route.query.view === 'gantt' ? route.query.view : 'list'));
const isBoard = computed(() => mode.value === 'board');
const viewOptions = computed(() => (['list', 'board', 'gantt'] as const).map((v) => ({ value: v, label: t(`taskBoard.view.${v}`), attrs: { 'data-testid': `view-${v}` } })));
// board=<id>: a whole number; no board means the default board, which the server defines. Anything else counts as unreadable (D6).
const boardParam = computed(() => (route.query.board === undefined ? null : String(route.query.board)));
const boardId = computed(() => (boardParam.value !== null && /^[1-9]\d*$/.test(boardParam.value) ? Number(boardParam.value) : null));
// 「套用」的設定只影響這個畫面（D4）：有未儲存的設定時改用設定查詢，網址仍帶原看板，重新整理就回到存好的版本。
const draft = ref<TaskBoardConfig | null>(null);
const boardSource = computed<BoardSource>(() => (draft.value ? { config: draft.value } : { viewId: boardId.value }));
const boardQuery = useBoardColumns(boardSource, isBoard);
const views = useTaskViews(isBoard);
const me = useMe();
const isAdmin = computed(() => !!me.data.value?.isSystemAdmin);
// 預設看板沒有存檔，設定的起點用欄查詢回應帶的設定（伺服器決定預設值，前端不另存一份）；存檔的看板讀自己的詳細資料（要 rowVersion 與權限）
const detail = useTaskView(boardId, isBoard);
// 詳細資料端點不分種類：board= 帶到甘特檢視的 Id 時不能把甘特設定交給看板元件，當成找不到（同欄查詢的 404，D6）
const boardDetail = computed(() => (detail.data.value?.kind === 'board' ? detail.data.value : null));
const savedConfig = computed<TaskBoardConfig | null>(() => (boardId.value === null ? boardQuery.data.value?.config ?? null : boardDetail.value?.config ?? null));
const config = computed(() => draft.value ?? savedConfig.value);
const canSave = computed(() => boardId.value !== null && !!boardDetail.value?.canEdit);
const canShare = computed(() => boardId.value !== null && !!boardDetail.value?.isOwner);
const boardName = computed(() => {
  if (boardId.value === null) return t('taskBoard.defaultName');
  const all = [...(views.data.value?.mine ?? []), ...(views.data.value?.shared ?? [])];
  return boardDetail.value?.name ?? all.find((v) => v.id === boardId.value && v.kind === 'board')?.name ?? '';
});
const boardGone = ref(false);

const query = useMyTasks(tab, filter, page, () => mode.value === 'list');
const items = computed(() => query.data.value?.items ?? []);
const pageCount = computed(() => Math.max(1, Math.ceil((query.data.value?.totalCount ?? 0) / SIZE)));
const filterOptions = computed(() => FILTERS.map((f) => ({ value: f, label: t(`tasks.filters.${f}`) })));
const adding = ref(false);

function go(patch: Record<string, string | undefined>) {
  const next = { ...route.query, ...patch };
  for (const k of Object.keys(next)) if (next[k] === undefined) delete next[k];
  void router.replace({ query: next });
}
function setTab(v: TaskListTab) { page.value = 1; go({ tab: v }); }
function setFilter(v: TaskListFilter) { page.value = 1; go({ filter: v }); }
const setOpen = (id: number | null) => go({ open: id === null ? undefined : String(id) });
function setView(v: ViewMode) {
  boardGone.value = false;
  draft.value = null;
  if (v === 'list') return go({ view: undefined, board: undefined });
  // gt2a 只有預設甘特圖（存檔甘特圖與 gantt=<id> 隨 gt2c）
  if (v === 'gantt') return go({ view: 'gantt', board: undefined });
  const last = readLastTaskBoard(identity.userId);
  go({ view: 'board', board: last === null ? undefined : String(last) });
}

// D6: a board that is gone or no longer shared (404/403), or a board id that makes no sense, falls back to the default board
// with a notice. The remembered "last board" goes too when it is the board that failed, so the next visit does not hit it again;
// a malformed link says nothing about the remembered board, so that one stays.
function fallbackToDefault({ forget }: { forget: boolean }) {
  if (forget) clearLastTaskBoard(identity.userId);
  boardGone.value = true;
  draft.value = null;
  go({ board: undefined });
}

// 選看板：預設看板清掉網址的 board 與「上次的看板」記憶；其他看板的記憶由載入成功時寫入。換看板就丟掉未儲存的設定。
function selectBoard(id: number | null) {
  boardGone.value = false;
  draft.value = null;
  if (id === null) clearLastTaskBoard(identity.userId);
  go({ board: id === null ? undefined : String(id) });
}
function onBoardDeleted(id: number) {
  if (readLastTaskBoard(identity.userId) === id) clearLastTaskBoard(identity.userId);
  if (id === boardId.value) selectBoard(null);
}
const onSavedAsNew = (d: TaskViewDetail) => selectBoard(d.id);
function onSaved() { draft.value = null; }
const settingsOpen = ref(false);
const picker = ref<InstanceType<typeof BoardPicker> | null>(null);
const shareOpen = ref(false);
const shareId = ref<number | null>(null);
function openShare(id: number) { shareId.value = id; shareOpen.value = true; }
// 未儲存的設定屬於它當下的看板：網址的看板一變就丟掉（各入口也會自己清，這裡擋住以後新增的路徑，例如通知連結帶 board）
watch(boardId, () => { draft.value = null; });
watch(() => [isBoard.value, boardParam.value, boardId.value] as const, ([board, param, id]) => {
  if (board && param !== null && id === null) fallbackToDefault({ forget: false });
}, { immediate: true });
watch(() => detail.data.value, (d) => {
  if (isBoard.value && d && d.id === boardId.value && d.kind !== 'board') fallbackToDefault({ forget: true });
}, { immediate: true });
watch(() => boardQuery.error.value, (e) => {
  if (isBoard.value && boardId.value !== null && [403, 404].includes((e as ApiError | null)?.status ?? 0)) fallbackToDefault({ forget: true });
}, { immediate: true });
watch(() => [boardId.value, boardQuery.isSuccess.value] as const, ([id, ok]) => {
  if (id !== null && ok) writeLastTaskBoard(identity.userId, id);
}, { immediate: true });
// 未儲存的設定本身無效（例如「近 N 天」的 N 不合格）時，欄查詢回 400，重試一定失敗：改提示設定無效，並給「捨棄未儲存的設定」
const draftInvalid = computed(() => draft.value !== null && !boardQuery.data.value && (boardQuery.error.value as unknown as ApiError | null)?.status === 400);
const boardFailed = computed(() => !!boardQuery.error.value && !boardQuery.data.value && !draftInvalid.value
  && ![403, 404].includes((boardQuery.error.value as unknown as ApiError).status));
</script>

<template>
  <q-page padding>
    <div class="row items-center q-mb-sm" style="gap: 8px">
      <div class="text-h6">{{ t('tasks.myTitle') }}</div>
      <q-btn-toggle
        :model-value="mode" :options="viewOptions" dense no-caps unelevated toggle-color="primary" :aria-label="t('taskBoard.view.label')" role="group"
        data-testid="view-toggle" @update:model-value="(v) => setView(v as ViewMode)"
      />
      <q-space />
      <q-btn color="primary" no-caps icon="add" :label="t('tasks.add')" data-testid="my-task-add" @click="adding = true" />
    </div>
    <template v-if="isBoard">
      <q-banner v-if="boardGone" dense rounded class="bg-info-soft q-mb-sm" role="status" data-testid="board-gone-notice">
        {{ t('taskBoard.gone') }}
        <template #action><q-btn flat dense no-caps :label="t('tasks.close')" data-testid="board-gone-close" @click="boardGone = false" /></template>
      </q-banner>
      <div class="row items-center q-mb-sm" style="gap: 8px">
        <BoardPicker
          ref="picker" :views="views.data.value" :failed="views.isError.value" :current-id="boardId" :current-name="boardName" :unsaved="draft !== null"
          :is-admin="isAdmin" :has-subordinates="hasSubordinates" :config="config"
          @select="selectBoard" @share="openShare" @deleted="onBoardDeleted" @created="onSavedAsNew" @retry="views.refetch()"
        />
        <q-btn flat dense no-caps icon="tune" :label="t('taskBoard.settingsButton')" :disable="!config" data-testid="board-settings" @click="settingsOpen = true" />
        <q-btn v-if="canShare" flat dense no-caps icon="share" :label="t('taskBoard.shareButton')" data-testid="board-share" @click="openShare(boardId!)" />
      </div>
      <q-banner v-if="boardQuery.data.value && !boardQuery.data.value.scopeAvailable" dense rounded class="bg-info-soft q-mb-sm" role="status" data-testid="scope-unavailable-notice">
        {{ t('taskBoard.scopeUnavailable') }}
      </q-banner>
      <div v-if="draftInvalid" class="text-negative q-py-sm" role="alert" data-testid="board-invalid">
        {{ t('taskBoard.invalidConfig') }}
        <q-btn flat dense no-caps color="primary" :label="t('taskBoard.discardDraft')" data-testid="board-discard-draft" @click="draft = null" />
      </div>
      <div v-else-if="boardFailed" class="text-negative q-py-sm" data-testid="board-load-failed">
        {{ t('taskBoard.loadFailed') }}
        <q-btn flat dense no-caps color="primary" :label="t('taskBoard.retry')" data-testid="board-retry" @click="boardQuery.refetch()" />
      </div>
      <q-linear-progress v-else-if="boardQuery.isLoading.value" indeterminate class="q-my-sm" data-testid="board-loading" />
      <TaskBoard v-if="boardQuery.data.value" :board="boardQuery.data.value" :source="boardSource" @open="setOpen" />
    </template>
    <TaskGantt v-else-if="mode === 'gantt'" @open="setOpen" />
    <template v-else>
      <q-tabs :model-value="tab" dense align="left" narrow-indicator class="text-primary" @update:model-value="(v) => setTab(v as TaskListTab)">
        <q-tab v-for="x in tabs" :key="x" :name="x" no-caps :label="t(`tasks.tabs.${x}`)" :data-testid="`tab-${x}`" />
      </q-tabs>
      <q-separator />
      <div class="q-my-md" style="max-width: 240px">
        <q-select
          :model-value="filter" :options="filterOptions" emit-value map-options outlined dense
          :label="t('tasks.filterLabel')" data-testid="task-filter" @update:model-value="(v) => setFilter(v as TaskListFilter)"
        />
      </div>
      <div v-if="query.error.value && !query.data.value" class="text-negative" data-testid="tasks-load-failed">{{ t('tasks.loadFailed') }}</div>
      <div v-else-if="!items.length && !query.isFetching.value" class="text-muted q-py-md" data-testid="tasks-empty">{{ t('tasks.empty') }}</div>
      <q-list v-else separator bordered class="rounded-borders">
        <q-item
          v-for="task in items" :key="task.id" clickable :data-testid="`my-task-${task.id}`"
          @click="setOpen(task.id)"
        >
          <q-item-section>
            <q-item-label v-if="task.level === 'child' && task.parentTitle" caption class="ellipsis" :data-testid="`my-task-parent-${task.id}`">{{ t('tasks.subtask.parent') }}{{ t('tasks.colon') }}{{ task.parentTitle }}</q-item-label>
            <q-item-label :class="{ 'text-strike text-muted': task.status === 'done' || task.status === 'cancelled' }" style="overflow-wrap: anywhere">
              <span :data-testid="`my-task-open-${task.id}`" @click.stop="setOpen(task.id)">{{ task.title }}</span>
            </q-item-label>
            <q-item-label caption style="overflow-wrap: anywhere">
              <template v-if="task.record">{{ task.record.accessible ? (task.record.title || t('tasks.recordFallback', { id: task.record.recordId })) : t('tasks.recordNoAccess') }}</template>
            </q-item-label>
            <q-item-label v-if="task.progress" caption>
              <SubtaskProgress :progress="task.progress" />
            </q-item-label>
            <q-item-label caption class="row items-center q-gutter-x-sm">
              <span :data-testid="`my-task-assignee-${task.id}`">{{ task.assigneeName ?? (task.assigneeUserId || t('tasks.unassigned')) }}</span>
              <span v-if="task.dueDate" :class="{ 'text-negative': task.overdue }">{{ task.dueDate }}</span>
              <q-badge outline :color="taskStatusColor(task.status)" :label="t(`tasks.status.${task.status}`)" />
            </q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
      <div v-if="pageCount > 1" class="flex flex-center q-pa-sm">
        <q-pagination v-model="page" :max="pageCount" :max-pages="5" direction-links data-testid="tasks-pagination" />
      </div>
    </template>
    <BoardSettingsDialog
      v-if="config" v-model="settingsOpen" :config="config" :name="boardId === null ? '' : boardName" :view-id="canSave ? boardId : null" :row-version="boardDetail?.rowVersion ?? ''"
      :can-save="canSave" :has-subordinates="hasSubordinates" @apply="(c) => (draft = c)" @saved="onSaved" @saved-as-new="onSavedAsNew"
    />
    <TaskViewShareDialog :view-id="shareId" :open="shareOpen" @close="shareOpen = false" @hidden="picker?.focusButton()" />
    <TaskFormDialog v-model="adding" :default-assignee="identity.userId" />
    <TaskDialog :task-id="openId" @update:task-id="setOpen" />
  </q-page>
</template>
