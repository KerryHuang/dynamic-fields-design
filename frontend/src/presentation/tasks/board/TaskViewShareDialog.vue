<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useSetTaskViewShares, useTaskView, useTaskViewShareTargets } from 'src/application/dynamic/queries/task-board-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { TaskViewShareKind } from 'src/domain/dynamic/task-board-types';

/**
 * 分享看板：使用者、部門、群組三個分頁（版面沿用報表分享對話框）。分享只決定對方看不看得到看板設定，不授予任何任務權限。
 * 分享是整批取代，所以已經在分享名單裡、但選項清單沒有的對象（已停用的部門或使用者）也要留著，不然儲存會把它們默默拿掉。
 * 部門與群組的 id 是數字、分享的 id 一律是字串，送出時轉字串。
 */
const props = defineProps<{ viewId: number | null; open: boolean }>();
const emit = defineEmits<{ close: []; hidden: [] }>();
const { t, locale } = useI18n();

const KINDS: TaskViewShareKind[] = ['user', 'department', 'group'];
const view = useTaskView(() => props.viewId, () => props.open);
const targets = useTaskViewShareTargets(() => props.open);
const save = useSetTaskViewShares();

const tab = ref<TaskViewShareKind>('user');
const selected = ref<string[]>([]);
const search = ref('');
const error = ref('');
const key = (kind: TaskViewShareKind, id: string) => `${kind}:${id}`;

// 每次開啟都從目前的分享狀態起算，而且要等這次開啟的重抓結果回來（快取裡可能是舊的，分享是整批取代，用舊的起算會蓋掉別處的修改）；
// 已不存在的對象（兩個名稱都是 null）送出一定被伺服器拒絕，起算時就丟掉
const seeded = ref(false);
watch(() => props.open, (o) => { seeded.value = false; if (o) { tab.value = 'user'; search.value = ''; error.value = ''; seedFrom(); } }, { immediate: true });
watch(() => [view.data.value, view.isFetching.value], seedFrom);
function seedFrom() {
  const d = view.data.value;
  if (!props.open || seeded.value || view.isFetching.value || !d || d.id !== props.viewId) return;
  seeded.value = true;
  selected.value = d.shares.filter((s) => s.displayName !== null || s.nameI18n !== null).map((s) => key(s.kind, s.id));
}

interface Row { id: string; label: string }
const nameOfShare = (kind: TaskViewShareKind, id: string) => {
  const s = view.data.value?.shares.find((x) => x.kind === kind && x.id === id);
  return s?.displayName ?? (s?.nameI18n ? resolveI18n(s.nameI18n, locale.value) : id);
};
/** 選項清單加上「已在名單裡但清單沒有」的對象（標已停用）。 */
function rowsOf(kind: TaskViewShareKind): Row[] {
  const tg = targets.data.value;
  const base: Row[] = !tg ? [] : kind === 'user'
    ? tg.users.map((u) => ({ id: u.id, label: u.name }))
    : (kind === 'department' ? tg.departments : tg.groups).map((x) => ({ id: String(x.id), label: resolveI18n(x.name, locale.value) }));
  const known = new Set(base.map((r) => r.id));
  const extra = (view.data.value?.shares ?? [])
    .filter((s) => s.kind === kind && !known.has(s.id) && (s.displayName !== null || s.nameI18n !== null))
    .map((s) => ({ id: s.id, label: t('tasks.inactiveUser', { name: nameOfShare(kind, s.id) }) }));
  return [...base, ...extra];
}
const rows = computed(() => {
  const all = rowsOf(tab.value);
  const q = search.value.trim().toLowerCase();
  return tab.value === 'user' && q ? all.filter((r) => r.label.toLowerCase().includes(q) || r.id.toLowerCase().includes(q)) : all;
});
const countOf = (kind: TaskViewShareKind) => selected.value.filter((k) => k.startsWith(`${kind}:`)).length;

function submit() {
  if (props.viewId === null) return;
  error.value = '';
  const order = (k: string) => KINDS.indexOf(k.slice(0, k.indexOf(':')) as TaskViewShareKind);
  const shares = [...selected.value].sort((a, b) => order(a) - order(b)).map((k) => {
    const i = k.indexOf(':');
    return { kind: k.slice(0, i) as TaskViewShareKind, id: k.slice(i + 1) };
  });
  save.mutate({ id: props.viewId, shares }, {
    onSuccess: () => { Notify.create({ type: 'positive', message: t('taskBoard.share.saved') }); emit('close'); },
    // 伺服器的錯誤文字是中文，不直接顯示；原因通常是對象剛被刪除，重新開啟即可
    onError: () => { error.value = t('taskBoard.share.saveFailed'); },
  });
}
// 還沒起算好之前不能儲存：此時送出的是空名單，會把分享全部清掉
const loading = computed(() => targets.isLoading.value || (props.open && props.viewId !== null && !seeded.value && !view.isError.value));
const failed = computed(() => targets.isError.value || view.isError.value);
function retry() {
  if (targets.isError.value) void targets.refetch();
  if (view.isError.value) void view.refetch();
}
</script>

<template>
  <q-dialog :model-value="open" :maximized="$q.screen.lt.sm" @update:model-value="emit('close')" @hide="emit('hidden')">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 480px; max-width: 95vw'" class="column no-wrap" data-testid="task-share">
      <q-card-section class="text-h6">{{ t('taskBoard.share.title') }}</q-card-section>
      <q-tabs v-model="tab" dense align="left" class="text-primary" data-testid="task-share-tabs">
        <q-tab v-for="k in KINDS" :key="k" :name="k" no-caps :data-testid="`task-share-tab-${k}`">
          {{ t(`taskBoard.share.${k === 'user' ? 'users' : k === 'department' ? 'departments' : 'groups'}`) }}<q-badge v-if="countOf(k)" class="q-ml-xs" color="primary" :label="countOf(k)" />
        </q-tab>
      </q-tabs>
      <q-separator />
      <q-card-section class="col column no-wrap" style="min-height: 120px; overflow-y: auto" data-testid="task-share-body">
        <div v-if="failed" class="text-negative" role="alert" data-testid="task-share-load-error">
          {{ t('taskBoard.share.loadFailed') }}
          <q-btn flat dense no-caps color="primary" :label="t('taskBoard.share.retry')" data-testid="task-share-retry" @click="retry" />
        </div>
        <q-linear-progress v-else-if="loading" indeterminate data-testid="task-share-loading" />
        <template v-else>
          <div v-if="tab === 'department'" class="text-caption text-muted q-mb-sm" data-testid="task-share-dept-note">{{ t('taskBoard.share.departmentNote') }}</div>
          <q-input v-if="tab === 'user'" v-model="search" dense outlined clearable class="q-mb-sm" :label="t('taskBoard.share.search')" data-testid="task-share-search" />
          <div role="group" :aria-label="t(`taskBoard.share.${tab === 'user' ? 'users' : tab === 'department' ? 'departments' : 'groups'}`)" class="column no-wrap" data-testid="task-share-list">
            <q-checkbox
              v-for="r in rows" :key="`${tab}:${r.id}`" v-model="selected" :val="key(tab, r.id)" dense class="q-py-xs"
              :label="r.label" :data-testid="`task-share-${tab}-${r.id}`"
            />
          </div>
          <div v-if="!rowsOf(tab).length" class="text-muted">
            {{ t(`taskBoard.share.${tab === 'user' ? 'noUsers' : tab === 'department' ? 'noDepartments' : 'noGroups'}`) }}
          </div>
        </template>
        <div class="text-caption text-muted q-mt-md">{{ t('taskBoard.share.note') }}</div>
        <div v-if="error" class="text-negative q-mt-sm" role="alert" data-testid="task-share-error">{{ error }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="t('taskBoard.share.cancel')" data-testid="task-share-cancel" @click="emit('close')" />
        <q-btn
          color="primary" no-caps :label="t('taskBoard.share.save')" :loading="save.isPending.value" :disable="loading || failed || !view.data.value"
          data-testid="task-share-save" @click="submit"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
