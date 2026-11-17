<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { clearLastTaskBoard, readLastTaskBoard, writeLastTaskBoard } from 'src/application/dynamic/last-task-board';
import { useBoardColumns, useTaskViews } from 'src/application/dynamic/queries/task-board-queries';
import { useMyTasks, useTaskCapabilities } from 'src/application/dynamic/queries/tasks-queries';
import { taskStatusColor, type TaskListFilter, type TaskListTab } from 'src/domain/dynamic/task-types';
import type { ApiError } from 'src/services/api/http';
import TaskBoard from 'src/presentation/tasks/board/TaskBoard.vue';
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

type ViewMode = 'list' | 'board';
const mode = computed<ViewMode>(() => (route.query.view === 'board' ? 'board' : 'list'));
const isBoard = computed(() => mode.value === 'board');
const viewOptions = computed(() => (['list', 'board'] as const).map((v) => ({ value: v, label: t(`taskBoard.view.${v}`), attrs: { 'data-testid': `view-${v}` } })));
// board=<id>: a whole number; no board means the default board, which the server defines. Anything else counts as unreadable (D6).
const boardParam = computed(() => (route.query.board === undefined ? null : String(route.query.board)));
const boardId = computed(() => (boardParam.value !== null && /^[1-9]\d*$/.test(boardParam.value) ? Number(boardParam.value) : null));
const boardSource = computed(() => ({ viewId: boardId.value }));
const boardQuery = useBoardColumns(boardSource, isBoard);
const views = useTaskViews(isBoard);
const boardName = computed(() => {
  if (boardId.value === null) return t('taskBoard.defaultName');
  const all = [...(views.data.value?.mine ?? []), ...(views.data.value?.shared ?? [])];
  return all.find((v) => v.id === boardId.value)?.name ?? '';
});
const boardGone = ref(false);

const query = useMyTasks(tab, filter, page, () => !isBoard.value);
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
  if (v === 'list') return go({ view: undefined, board: undefined });
  const last = readLastTaskBoard(identity.userId);
  go({ view: 'board', board: last === null ? undefined : String(last) });
}

// D6: a board that is gone or no longer shared (404/403), or a board id that makes no sense, falls back to the default board
// with a notice; the remembered "last board" goes too so the next visit does not hit it again.
function fallbackToDefault() {
  clearLastTaskBoard(identity.userId);
  boardGone.value = true;
  go({ board: undefined });
}
watch(() => [isBoard.value, boardParam.value, boardId.value] as const, ([board, param, id]) => {
  if (board && param !== null && id === null) fallbackToDefault();
}, { immediate: true });
watch(() => boardQuery.error.value, (e) => {
  if (isBoard.value && boardId.value !== null && [403, 404].includes((e as ApiError | null)?.status ?? 0)) fallbackToDefault();
}, { immediate: true });
watch(() => [boardId.value, boardQuery.isSuccess.value] as const, ([id, ok]) => {
  if (id !== null && ok) writeLastTaskBoard(identity.userId, id);
}, { immediate: true });
const boardFailed = computed(() => !!boardQuery.error.value && !boardQuery.data.value && !boardGone.value
  && ![403, 404].includes((boardQuery.error.value as ApiError).status));
</script>

<template>
  <q-page padding>
    <div class="row items-center q-mb-sm" style="gap: 8px">
      <div class="text-h6">{{ t('tasks.myTitle') }}</div>
      <q-btn-toggle
        :model-value="mode" :options="viewOptions" dense no-caps unelevated toggle-color="primary" :aria-label="t('taskBoard.view.label')"
        data-testid="view-toggle" @update:model-value="(v) => setView(v as ViewMode)"
      />
      <q-space />
      <q-btn color="primary" no-caps icon="add" :label="t('tasks.add')" data-testid="my-task-add" @click="adding = true" />
    </div>
    <template v-if="isBoard">
      <q-banner v-if="boardGone" dense rounded class="bg-warning text-white q-mb-sm" role="status" data-testid="board-gone-notice">
        {{ t('taskBoard.gone') }}
        <template #action><q-btn flat dense no-caps :label="t('tasks.close')" data-testid="board-gone-close" @click="boardGone = false" /></template>
      </q-banner>
      <!-- Task 10 puts the board picker here -->
      <div class="q-mb-sm text-subtitle2" data-testid="board-picker-slot">{{ boardName }}</div>
      <div v-if="boardFailed" class="text-negative q-py-sm" data-testid="board-load-failed">
        {{ t('taskBoard.loadFailed') }}
        <q-btn flat dense no-caps color="primary" :label="t('taskBoard.retry')" data-testid="board-retry" @click="boardQuery.refetch()" />
      </div>
      <q-linear-progress v-else-if="boardQuery.isLoading.value" indeterminate class="q-my-sm" data-testid="board-loading" />
      <TaskBoard v-if="boardQuery.data.value" :board="boardQuery.data.value" :source="boardSource" @open="setOpen" />
    </template>
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
            <q-item-label :class="{ 'text-strike text-muted': task.status === 'done' || task.status === 'cancelled' }" style="overflow-wrap: anywhere">
              <span :data-testid="`my-task-open-${task.id}`" @click.stop="setOpen(task.id)">{{ task.title }}</span>
            </q-item-label>
            <q-item-label caption style="overflow-wrap: anywhere">
              <template v-if="task.record">{{ task.record.accessible ? (task.record.title || t('tasks.recordFallback', { id: task.record.recordId })) : t('tasks.recordNoAccess') }}</template>
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
    <TaskFormDialog v-model="adding" :default-assignee="identity.userId" />
    <TaskDialog :task-id="openId" @update:task-id="setOpen" />
  </q-page>
</template>
