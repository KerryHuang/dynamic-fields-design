<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useCreateTaskView, useDeleteTaskView } from 'src/application/dynamic/queries/task-board-queries';
import { MAX_BOARD_NAME, saveAsConfig } from 'src/domain/dynamic/task-board-config';
import type { ApiError } from 'src/services/api/http';
import type { TaskBoardConfig, TaskViewDetail, TaskViewList, TaskViewSummary } from 'src/domain/dynamic/task-board-types';

/**
 * 看板選擇器：預設看板、我的看板、分享給我的（帶建立者姓名）、另存為新看板。
 * 自己的看板可以分享與刪除；分享給我的看板只有系統管理員能刪（管理員看得到所有有分享的看板，只有刪除，不能分享或改設定）。
 * 按鈕沒有 aria-label：看板名稱與「（未儲存）」就是它的可及名稱。每一列的「選這個看板」與動作按鈕是並排的兄弟節點，不互相包含。
 */
const props = defineProps<{
  views: TaskViewList | undefined;
  failed?: boolean;
  currentId: number | null;
  currentName: string;
  /** 目前畫面套用了尚未儲存的設定。 */
  unsaved: boolean;
  isAdmin: boolean;
  hasSubordinates: boolean;
  /** 目前畫面使用的設定：「另存為新看板」存的就是它。 */
  config: TaskBoardConfig | null;
}>();
const emit = defineEmits<{
  select: [id: number | null];
  share: [id: number];
  deleted: [id: number];
  created: [detail: TaskViewDetail];
  retry: [];
}>();
const { t } = useI18n();
const create = useCreateTaskView();
const del = useDeleteTaskView();

const mine = computed(() => props.views?.mine ?? []);
const shared = computed(() => props.views?.shared ?? []);
const ownerOf = (v: TaskViewSummary) => {
  const name = v.ownerName ?? v.ownerUserId;
  return v.ownerActive ? name : t('tasks.inactiveUser', { name });
};

// 選單裡的按鈕開出對話框後，選單與按鈕都已卸載，關閉時 Quasar 沒有地方可還焦點；焦點掉到 body 時放回選擇器按鈕
const button = ref<{ $el: HTMLElement } | null>(null);
function focusButton() { if (!document.activeElement || document.activeElement === document.body) button.value?.$el.focus(); }
defineExpose({ focusButton });

// ---- 另存為新看板 ----
const saving = ref(false);
const name = ref('');
const nameError = ref('');
const createError = ref('');
const scopeSwapped = computed(() => !props.hasSubordinates && props.config?.scope === 'subordinates');
function openSaveAs() {
  name.value = '';
  nameError.value = '';
  createError.value = '';
  saving.value = true;
}
function submitSaveAs() {
  const trimmed = name.value.trim();
  nameError.value = trimmed ? '' : t('taskBoard.picker.nameRequired');
  if (!trimmed || !props.config) return;
  createError.value = '';
  create.mutate({ name: trimmed, config: saveAsConfig(props.config, props.hasSubordinates) }, {
    onSuccess: (detail) => {
      saving.value = false;
      Notify.create({ type: 'positive', message: t('taskBoard.picker.created', { name: detail.name }) });
      emit('created', detail);
    },
    // 伺服器的錯誤文字是中文，不直接顯示
    onError: () => { createError.value = t('taskBoard.picker.createFailed'); },
  });
}

// ---- 刪除 ----
const deleting = ref<TaskViewSummary | null>(null);
const deleteError = ref('');
function confirmDelete() {
  const v = deleting.value;
  if (!v) return;
  deleteError.value = '';
  del.mutate(v.id, {
    onSuccess: () => {
      deleting.value = null;
      Notify.create({ type: 'positive', message: t('taskBoard.picker.deleted') });
      emit('deleted', v.id);
    },
    onError: (e) => {
      // 已被別人刪掉：目的達成，當成已刪除（清單由 mutation 重抓）
      if ((e as unknown as ApiError).status === 404) { deleting.value = null; emit('deleted', v.id); return; }
      deleteError.value = t('taskBoard.picker.deleteFailed');
    },
  });
}
</script>

<template>
  <div class="board-picker">
    <q-btn ref="button" outline dense no-caps icon-right="arrow_drop_down" class="board-picker-btn" aria-haspopup="menu" data-testid="board-picker">
      <span class="ellipsis board-picker-name">{{ currentName }}</span>
      <span v-if="unsaved" class="q-ml-xs text-muted text-no-wrap" data-testid="board-unsaved">{{ t('taskBoard.unsaved') }}</span>
      <q-menu class="board-picker-menu" data-testid="picker-menu">
        <q-list dense style="min-width: 260px; max-width: 92vw">
          <q-item v-close-popup clickable :active="currentId === null" data-testid="picker-item-default" @click="emit('select', null)">
            <q-item-section>{{ t('taskBoard.picker.default') }}</q-item-section>
          </q-item>
          <div v-if="failed" class="q-pa-sm text-negative" role="alert" data-testid="picker-load-failed">
            {{ t('taskBoard.picker.loadFailed') }}
            <q-btn flat dense no-caps color="primary" :label="t('taskBoard.retry')" data-testid="picker-retry" @click="emit('retry')" />
          </div>

          <q-separator />
          <q-item-label header data-testid="picker-group-mine">{{ t('taskBoard.picker.mine') }}</q-item-label>
          <q-item v-if="!mine.length && !failed" dense><q-item-section class="text-muted">{{ t('taskBoard.picker.noMine') }}</q-item-section></q-item>
          <div v-for="v in mine" :key="v.id" class="row no-wrap items-center">
            <q-item v-close-popup clickable class="col" :active="currentId === v.id" :data-testid="`picker-item-${v.id}`" @click="emit('select', v.id)">
              <q-item-section>
                <q-item-label class="ellipsis">{{ v.name }}</q-item-label>
                <q-item-label v-if="v.shareCount" caption>{{ t('taskBoard.picker.sharedCount') }}</q-item-label>
              </q-item-section>
            </q-item>
            <q-btn
              v-close-popup flat dense round icon="share" :aria-label="t('taskBoard.picker.shareFor', { name: v.name })"
              :data-testid="`picker-share-${v.id}`" @click="emit('share', v.id)"
            />
            <q-btn
              v-close-popup flat dense round icon="delete" :aria-label="t('taskBoard.picker.deleteFor', { name: v.name })"
              :data-testid="`picker-delete-${v.id}`" @click="deleting = v"
            />
          </div>

          <q-separator />
          <q-item-label header data-testid="picker-group-shared">{{ t('taskBoard.picker.shared') }}</q-item-label>
          <q-item v-if="!shared.length && !failed" dense><q-item-section class="text-muted">{{ t('taskBoard.picker.noShared') }}</q-item-section></q-item>
          <div v-for="v in shared" :key="v.id" class="row no-wrap items-center">
            <q-item v-close-popup clickable class="col" :active="currentId === v.id" :data-testid="`picker-item-${v.id}`" @click="emit('select', v.id)">
              <q-item-section>
                <q-item-label class="ellipsis">{{ v.name }}</q-item-label>
                <q-item-label caption class="ellipsis">{{ t('taskBoard.picker.ownedBy', { name: ownerOf(v) }) }}</q-item-label>
              </q-item-section>
            </q-item>
            <q-btn
              v-if="isAdmin" v-close-popup flat dense round icon="delete" :aria-label="t('taskBoard.picker.deleteFor', { name: v.name })"
              :data-testid="`picker-delete-${v.id}`" @click="deleting = v"
            />
          </div>

          <q-separator />
          <q-item v-close-popup clickable :disable="!config" data-testid="picker-save-as" @click="openSaveAs">
            <q-item-section avatar><q-icon name="save_as" /></q-item-section>
            <q-item-section>{{ t('taskBoard.picker.saveAs') }}</q-item-section>
          </q-item>
        </q-list>
      </q-menu>
    </q-btn>

    <q-dialog v-model="saving" :maximized="$q.screen.lt.sm" @hide="focusButton">
      <q-card :style="$q.screen.lt.sm ? undefined : 'width: 380px; max-width: 95vw'" data-testid="picker-save-as-dialog">
        <q-card-section class="text-h6">{{ t('taskBoard.picker.saveAsTitle') }}</q-card-section>
        <q-card-section class="q-pt-none column q-gutter-y-sm">
          <q-input
            v-model="name" dense outlined autofocus :maxlength="MAX_BOARD_NAME" :label="t('taskBoard.picker.name')" :error="!!nameError" :error-message="nameError"
            data-testid="picker-name" @keyup.enter="submitSaveAs"
          />
          <div v-if="scopeSwapped" class="text-caption text-muted" data-testid="picker-scope-swapped">{{ t('taskBoard.picker.scopeSwapped') }}</div>
          <div v-if="createError" class="text-negative" role="alert" data-testid="picker-create-error">{{ createError }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps :label="t('taskBoard.settings.close')" data-testid="picker-name-cancel" />
          <q-btn color="primary" no-caps :label="t('taskBoard.picker.create')" :loading="create.isPending.value" data-testid="picker-name-ok" @click="submitSaveAs" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog :model-value="deleting !== null" :maximized="$q.screen.lt.sm" @update:model-value="(v) => { if (!v) deleting = null; }" @hide="focusButton">
      <q-card :style="$q.screen.lt.sm ? undefined : 'width: 380px; max-width: 95vw'" data-testid="picker-delete-confirm">
        <q-card-section class="text-h6">{{ t('taskBoard.picker.deleteTitle') }}</q-card-section>
        <q-card-section class="q-pt-none">
          <div class="text-weight-medium" style="overflow-wrap: anywhere">{{ deleting?.name }}</div>
          <div>{{ deleting && !deleting.isOwner ? t('taskBoard.picker.deleteBodyShared', { owner: ownerOf(deleting) }) : t('taskBoard.picker.deleteBody') }}</div>
          <div v-if="deleteError" class="text-negative q-mt-sm" role="alert" data-testid="picker-delete-error">{{ deleteError }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn v-close-popup flat no-caps :label="t('taskBoard.settings.close')" data-testid="picker-delete-cancel" />
          <q-btn color="negative" no-caps :label="t('tasks.delete')" :loading="del.isPending.value" data-testid="picker-delete-ok" @click="confirmDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.board-picker { display: inline-block; max-width: 100%; }
.board-picker-btn { max-width: 100%; }
.board-picker-name { max-width: 60vw; }
</style>
