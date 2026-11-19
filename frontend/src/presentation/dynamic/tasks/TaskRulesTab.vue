<script setup lang="ts">
import RowActionMenu from 'src/components/framework/RowActionMenu.vue';
import { useQuasar } from 'quasar';
import { computed, nextTick, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useEffectivePolicies } from 'src/application/dynamic/queries/policies-queries';
import { useDeleteTaskRule, useResumeTaskRule, useSaveTaskRule, useTaskRulesQuery } from 'src/application/dynamic/queries/task-rules-queries';
import { formatTime } from 'src/domain/ops/ops-format';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { fromTaskRule, toTaskRuleBody, type TaskRule } from 'src/domain/dynamic/task-rule-draft';
import type { ModuleDetail } from 'src/domain/dynamic/types';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import { joinList } from 'src/i18n/translate';
import type { ApiError } from 'src/services/api/http';
import TaskRuleEditorDialog from './TaskRuleEditorDialog.vue';

/** focusRule：暫停通知的連結帶 `rule=<規則代碼>`，捲到那條規則並展開暫停資訊（維運設計 §17.2.5）。 */
const props = defineProps<{ module: ModuleDetail; focusRule?: string | null }>();
const { t, locale } = useI18n();
const $q = useQuasar();

const key = computed(() => props.module.key);
const rulesQuery = useTaskRulesQuery(key);
const me = useMe();
const policies = useEffectivePolicies(key);
/** 上限一律取自政策生效值；還沒載入就是 undefined，相關按鈕停用（前端不留預設值）。 */
const maxRules = computed(() => policies.data.value?.values['task.maxRulesPerModule'] as number | undefined);
const maxTasks = computed(() => policies.data.value?.values['task.maxTasksPerRule'] as number | undefined);
const atLimit = computed(() => maxRules.value !== undefined && (rulesQuery.data.value?.length ?? 0) >= maxRules.value);
const saveRule = useSaveTaskRule(key);
const deleteRule = useDeleteTaskRule(key);

const fields = computed(() => toSchemaFields(props.module));
const readonly = computed(() => !me.data.value?.designer.edit);
/** 點整列開編輯：沒有編輯權限、或任務上限還沒載入時不可點 */
const canOpen = computed(() => !readonly.value && maxTasks.value !== undefined);
const ready = computed(() => rulesQuery.data.value && me.data.value);

const dialog = ref(false);
const editing = ref<TaskRule | undefined>(undefined);
function open(rule?: TaskRule) {
  editing.value = rule;
  dialog.value = true;
}

const failed = (e: unknown) => {
  const err = e as ApiError;
  const msgs = Object.values(err.errors ?? {}).flat();
  if (err.message) msgs.push(err.message);
  $q.notify({ type: 'negative', message: msgs.length ? joinList(msgs) : t('taskRules.saveFailed') });
};
function toggleActive(rule: TaskRule) {
  saveRule.mutate({ ruleKey: rule.key, body: { ...toTaskRuleBody(fromTaskRule(rule)), active: !rule.active } }, { onError: failed });
}

// 自動暫停（維運設計 §17.2.7）：能編輯規則的人才有「恢復」（O2-U6，使用者確認 2026-11-19）；點標籤展開暫停資訊。
// 恢復不補產任務（O2-U7b），確認框寫明略過幾次存檔與這個觸發方式的補救方式。
const expandedPause = ref<string | null>(null);
const resume = useResumeTaskRule(key);
const pendingResume = ref<TaskRule | null>(null);
function confirmResume() {
  const rule = pendingResume.value;
  pendingResume.value = null;
  if (!rule) return;
  resume.mutate(rule.key, { onSuccess: () => $q.notify({ type: 'positive', message: t('taskRules.pause.resumed') }), onError: failed });
}
watch([() => props.focusRule, () => rulesQuery.data.value], async ([rule, list]) => {
  if (!rule || !list?.some((r) => r.key === rule)) return;
  if (list.find((r) => r.key === rule)?.autoPause) expandedPause.value = rule;
  await nextTick();
  document.querySelector(`[data-testid="task-rule-row-${CSS.escape(rule)}"]`)?.scrollIntoView({ block: 'center' });
}, { immediate: true });

const pendingDelete = ref<TaskRule | null>(null);
function confirmDelete() {
  const rule = pendingDelete.value;
  pendingDelete.value = null;
  if (!rule) return;
  deleteRule.mutate(rule.key, { onSuccess: () => $q.notify({ type: 'positive', message: t('taskRules.deleted') }), onError: failed });
}
</script>

<template>
  <div v-if="rulesQuery.isError.value" class="text-negative">{{ t('taskRules.loadFailed') }}</div>
  <div v-else-if="!ready" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else>
    <div v-if="policies.isError.value" class="text-negative q-mb-sm" data-testid="task-rules-policy-error">{{ t('policies.loadFailed') }}</div>
    <q-banner v-if="module.tasksEnabled === false" dense class="bg-info-soft q-mb-sm" data-testid="task-rules-module-off">
      {{ t('taskRules.moduleTasksOff') }}
    </q-banner>
    <div class="row items-center q-mb-sm">
      <q-space />
      <q-btn color="primary" icon="add" dense :label="t('taskRules.add')" :disable="readonly || maxRules === undefined || atLimit" data-testid="add-task-rule" @click="open()">
        <q-tooltip v-if="atLimit">{{ t('taskRules.limit', { max: maxRules }) }}</q-tooltip>
      </q-btn>
    </div>

    <q-list bordered separator>
      <q-item v-if="!rulesQuery.data.value!.length"><q-item-section class="text-muted">{{ t('taskRules.empty') }}</q-item-section></q-item>
      <q-item
        v-for="r in rulesQuery.data.value" :key="r.key" :clickable="canOpen" :class="{ 'text-muted': !r.active }"
        :data-testid="`task-rule-row-${r.key}`" @click="canOpen && open(r)"
      >
        <q-item-section>
          <q-item-label style="overflow-wrap: anywhere">
            {{ resolveI18n(r.name, locale) }}
            <q-badge v-if="!r.active" color="muted" class="q-ml-sm">{{ t('taskRules.inactive') }}</q-badge>
          </q-item-label>
          <q-item-label caption>
            {{ t(`taskRules.trigger.${r.trigger.on}`) }} / {{ t(`taskRules.repeat.${r.repeat}`) }} / {{ t('taskRules.taskCount', { n: r.tasks.length }) }}
          </q-item-label>
          <q-item-label v-if="r.warnings.length" class="text-warning" :data-testid="`task-rule-warnings-${r.key}`">
            {{ t('taskRules.warnings') }}：{{ joinList(r.warnings) }}
          </q-item-label>
          <q-item-label v-if="r.autoPause" @click.stop>
            <q-badge
              color="negative" class="cursor-pointer" tabindex="0" role="button" :aria-expanded="expandedPause === r.key"
              :data-testid="`task-rule-paused-${r.key}`" @click="expandedPause = expandedPause === r.key ? null : r.key"
              @keydown.enter.prevent="expandedPause = expandedPause === r.key ? null : r.key"
            >
              {{ t('taskRules.pause.badge') }}
              <q-icon :name="expandedPause === r.key ? 'expand_less' : 'expand_more'" size="xs" />
            </q-badge>
          </q-item-label>
          <div v-if="r.autoPause && expandedPause === r.key" class="text-caption q-mt-xs" :data-testid="`task-rule-pause-info-${r.key}`" @click.stop>
            <div>{{ t('taskRules.pause.since', { time: formatTime(r.autoPause.pausedUtc) }) }}</div>
            <div>{{ t('taskRules.pause.failures', { n: r.autoPause.consecutiveFailures }) }}</div>
            <div v-if="r.autoPause.lastErrorCode">{{ t('taskRules.pause.lastError', { code: r.autoPause.lastErrorCode }) }}</div>
            <div v-if="r.autoPause.lastRequestId">{{ t('taskRules.pause.requestId', { code: r.autoPause.lastRequestId.slice(0, 8) }) }}</div>
            <div>{{ t('taskRules.pause.skipped', { n: r.autoPause.skippedWhilePaused }) }}</div>
          </div>
          <q-item-label v-else-if="r.lastPause" caption :data-testid="`task-rule-last-pause-${r.key}`">
            {{ t('taskRules.pause.lastPause', { from: formatTime(r.lastPause.pausedUtc), to: formatTime(r.lastPause.resumedUtc), n: r.lastPause.skippedWhilePaused }) }}
          </q-item-label>
          <q-item-label v-if="!r.autoPause && r.failureStreak" caption class="text-warning" :data-testid="`task-rule-streak-${r.key}`">
            {{ t('taskRules.pause.streak', { n: r.failureStreak.consecutiveFailures, time: formatTime(r.failureStreak.lastFailureUtc) }) }}
          </q-item-label>
        </q-item-section>
        <q-item-section side>
          <RowActionMenu
            v-if="$q.screen.lt.sm" :testid="`task-rule-more-${r.key}`"
            :actions="[
              ...(r.autoPause && !readonly ? [{ label: t('taskRules.pause.resume'), run: () => (pendingResume = r), testid: `task-rule-resume-menu-${r.key}` }] : []),
              { label: r.active ? t('taskRules.deactivate') : t('taskRules.activate'), disable: readonly, run: () => toggleActive(r) },
              { label: t('taskRules.delete'), disable: readonly, negative: true, run: () => (pendingDelete = r) },
            ]"
          />
          <div v-else class="row no-wrap items-center" @click.stop>
            <q-btn
              v-if="r.autoPause && !readonly" flat dense no-caps color="primary" :label="t('taskRules.pause.resume')"
              :data-testid="`task-rule-resume-${r.key}`" @click="pendingResume = r"
            />
            <q-btn
              flat dense round :icon="r.active ? 'toggle_on' : 'toggle_off'" :title="r.active ? t('taskRules.deactivate') : t('taskRules.activate')"
              :disable="readonly" :data-testid="`task-rule-toggle-${r.key}`" @click="toggleActive(r)"
            />
            <q-btn
              flat dense round icon="delete" color="negative" :title="t('taskRules.delete')" :disable="readonly"
              :data-testid="`task-rule-delete-${r.key}`" @click="pendingDelete = r"
            />
          </div>
        </q-item-section>
      </q-item>
    </q-list>

    <TaskRuleEditorDialog v-if="maxTasks !== undefined" v-model="dialog" :module-key="module.key" :fields="fields" :rule="editing" :max-tasks="maxTasks" />

    <q-dialog :model-value="!!pendingResume" @update:model-value="(v) => { if (!v) pendingResume = null; }">
      <q-card class="task-rule-confirm" data-testid="task-rule-resume-dialog">
        <q-card-section class="text-subtitle1">{{ t('taskRules.pause.confirmTitle', { name: pendingResume ? resolveI18n(pendingResume.name, locale) : '' }) }}</q-card-section>
        <q-card-section class="q-pt-none">
          <p>{{ t('taskRules.pause.confirmSkipped', { n: pendingResume?.autoPause?.skippedWhilePaused ?? 0 }) }}</p>
          <p data-testid="task-rule-resume-remedy">{{ pendingResume ? t(`taskRules.pause.remedy.${pendingResume.trigger.on}`) : '' }}</p>
          <p v-if="pendingResume && pendingResume.trigger.on !== 'manual'" class="text-caption text-muted">{{ t('taskRules.pause.manualTaskNote') }}</p>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('designer.close')" @click="pendingResume = null" />
          <q-btn color="primary" :label="t('taskRules.pause.resume')" :loading="resume.isPending.value" data-testid="task-rule-resume-confirm" @click="confirmResume" />
        </q-card-actions>
      </q-card>
    </q-dialog>

    <q-dialog :model-value="!!pendingDelete" @update:model-value="(v) => { if (!v) pendingDelete = null; }">
      <q-card class="task-rule-confirm">
        <q-card-section>{{ t('taskRules.confirmDelete', { name: pendingDelete ? resolveI18n(pendingDelete.name, locale) : '' }) }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('designer.close')" @click="pendingDelete = null" />
          <q-btn color="negative" :label="t('taskRules.delete')" data-testid="task-rule-delete-confirm" @click="confirmDelete" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.task-rule-confirm { width: 420px; max-width: 95vw; }
</style>
