<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useMyTasks } from 'src/application/dynamic/queries/tasks-queries';
import { taskStatusColor, type TaskListFilter, type TaskListTab } from 'src/domain/dynamic/task-types';
import TaskDialog from 'src/presentation/dynamic/tasks/TaskDialog.vue';
import TaskFormDialog from 'src/presentation/dynamic/tasks/TaskFormDialog.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';

const TABS: TaskListTab[] = ['assigned', 'created', 'mentioned'];
const FILTERS: TaskListFilter[] = ['open', 'overdue', 'thisWeek', 'all'];
const SIZE = 20;

const { t } = useI18n();
const route = useRoute();
const router = useRouter();
const identity = useIdentityStore();

const pick = <T extends string>(value: unknown, allowed: T[], fallback: T): T =>
  (allowed as string[]).includes(String(value)) ? (value as T) : fallback;
const tab = computed(() => pick(route.query.tab, TABS, 'assigned'));
const filter = computed(() => pick(route.query.filter, FILTERS, 'open'));
// A notification link is /tasks?open={id}; anything that is not a whole number is ignored.
const openId = computed(() => (/^\d+$/.test(String(route.query.open ?? '')) ? Number(route.query.open) : null));
const page = ref(1);

const query = useMyTasks(tab, filter, page);
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
</script>

<template>
  <q-page padding>
    <div class="row items-center no-wrap q-mb-sm">
      <div class="text-h6">{{ t('tasks.myTitle') }}</div>
      <q-space />
      <q-btn color="primary" no-caps icon="add" :label="t('tasks.add')" data-testid="my-task-add" @click="adding = true" />
    </div>
    <q-tabs :model-value="tab" dense align="left" narrow-indicator class="text-primary" @update:model-value="(v) => setTab(v as TaskListTab)">
      <q-tab v-for="x in TABS" :key="x" :name="x" no-caps :label="t(`tasks.tabs.${x}`)" :data-testid="`tab-${x}`" />
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
            <span>{{ task.assigneeName ?? (task.assigneeUserId || t('tasks.unassigned')) }}</span>
            <span v-if="task.dueDate" :class="{ 'text-negative': task.overdue }">{{ task.dueDate }}</span>
            <q-badge outline :color="taskStatusColor(task.status)" :label="t(`tasks.status.${task.status}`)" />
          </q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
    <div v-if="pageCount > 1" class="flex flex-center q-pa-sm">
      <q-pagination v-model="page" :max="pageCount" :max-pages="5" direction-links data-testid="tasks-pagination" />
    </div>
    <TaskFormDialog v-model="adding" :default-assignee="identity.userId" />
    <TaskDialog :task-id="openId" @update:task-id="setOpen" />
  </q-page>
</template>
