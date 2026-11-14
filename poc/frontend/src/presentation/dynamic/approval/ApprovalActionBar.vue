<script setup lang="ts">
import { QPageSticky, useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import RowActionMenu, { type RowAction } from 'src/components/framework/RowActionMenu.vue';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import type { DynamicRecord, ModuleSchema } from 'src/domain/dynamic/types';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { currentStage, hasApprovalActions, myOpenTask } from './approval-actions';
import ApproveDialog from './ApproveDialog.vue';
import CancelApprovalDialog from './CancelApprovalDialog.vue';
import ReassignDialog from './ReassignDialog.vue';
import RejectDialog from './RejectDialog.vue';
import TransferDialog from './TransferDialog.vue';
import WithdrawDialog from './WithdrawDialog.vue';

const props = defineProps<{ instance: ApprovalInstanceView; recordId: number; moduleKey: string; schema?: ModuleSchema | null; record?: DynamicRecord }>();
const { t } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();

const task = computed(() => myOpenTask(props.instance));
const isInitiator = computed(() => !!identity.userId && props.instance.initiatorUserId === identity.userId);
const dialog = ref<'approve' | 'reject' | 'transfer' | 'withdraw' | 'reassign' | 'cancel' | null>(null);

interface Btn { key: 'approve' | 'reject' | 'transfer' | 'withdraw'; color: string; outline?: boolean; disable?: boolean }
const buttons = computed<Btn[]>(() => {
  const list: Btn[] = [];
  if (task.value) list.push({ key: 'approve', color: 'primary' }, { key: 'reject', color: 'negative', outline: true }, { key: 'transfer', color: 'primary', outline: true });
  if (isInitiator.value && props.instance.status === 'pending') list.push({ key: 'withdraw', color: 'primary', outline: true, disable: !props.instance.canWithdraw });
  return list;
});

const show = computed(() => hasApprovalActions(props.instance, identity.userId));

// Phone: at most two buttons on the bottom bar; the rest go to the menu.
const mobile = computed(() => $q.screen.lt.sm);
const inline = computed(() => (mobile.value ? buttons.value.slice(0, 2) : buttons.value));
// The reason sits next to the button, so it shows only while the withdraw button is on the bar itself.
const withdrawBlocked = computed(() => inline.value.some((b) => b.key === 'withdraw' && b.disable));
// Manager actions (reassign, terminate) always live in the menu, at every width.
const overflow = computed<RowAction[]>(() => {
  const items: RowAction[] = mobile.value
    ? buttons.value.slice(2).map((b) => ({ label: t(`approval.actions.${b.key}`), run: () => { dialog.value = b.key; }, disable: b.disable, testid: `approval-more-${b.key}` }))
    : [];
  if (props.instance.canCancel && props.instance.status === 'pending') {
    for (const key of ['reassign', 'cancel'] as const) items.push({ label: t(`approval.actions.${key}`), run: () => { dialog.value = key; }, testid: `approval-more-${key}` });
  }
  return items;
});
</script>

<template>
  <component
    :is="mobile ? QPageSticky : 'div'" v-if="show"
    v-bind="mobile ? { position: 'bottom', expand: true, class: 'approval-sticky', style: 'margin: 0' } : {}"
  >
    <div
      class="row items-center justify-end no-wrap"
      :class="mobile ? 'full-width approval-mobile-bar' : 'q-gutter-sm'"
      :data-testid="mobile ? 'approval-mobile-bar' : 'approval-bar'"
    >
      <span v-if="withdrawBlocked" class="text-caption text-muted col" data-testid="withdraw-blocked">{{ t('approval.actions.withdrawBlocked') }}</span>
      <q-btn
        v-for="b in inline" :key="b.key" :color="b.color" :outline="b.outline" :disable="b.disable" :label="t(`approval.actions.${b.key}`)"
        :data-testid="`action-${b.key}`" @click="dialog = b.key"
      />
      <RowActionMenu v-if="overflow.length" :actions="overflow" testid="approval-more" />
    </div>
  </component>
  <ApproveDialog
    v-if="dialog === 'approve' && task" :model-value="true" :record-id="recordId" :module-key="moduleKey" :task-id="task.id" :row-version="instance.rowVersion"
    :editable-field-keys="currentStage(instance)?.editableFieldKeys" :schema="schema" :record="record" @update:model-value="dialog = null"
  />
  <RejectDialog
    v-if="dialog === 'reject' && task" :model-value="true" :record-id="recordId" :module-key="moduleKey" :task-id="task.id" :row-version="instance.rowVersion"
    :can-return-previous="task.canReturnPrevious" @update:model-value="dialog = null"
  />
  <TransferDialog
    v-if="dialog === 'transfer' && task" :model-value="true" :record-id="recordId" :module-key="moduleKey" :task-id="task.id" :instance="instance"
    @update:model-value="dialog = null"
  />
  <ReassignDialog
    v-if="dialog === 'reassign'" :model-value="true" :record-id="recordId" :module-key="moduleKey" :instance="instance" @update:model-value="dialog = null"
  />
  <CancelApprovalDialog
    v-if="dialog === 'cancel'" :model-value="true" :record-id="recordId" :module-key="moduleKey" :instance-id="instance.id" :row-version="instance.rowVersion"
    @update:model-value="dialog = null"
  />
  <WithdrawDialog
    v-if="dialog === 'withdraw'" :model-value="true" :record-id="recordId" :module-key="moduleKey" :instance-id="instance.id" :row-version="instance.rowVersion"
    @update:model-value="dialog = null"
  />
</template>

<style scoped>
/* Above the blocks that follow in the page (they are positioned too), below the header, drawers and dialogs. */
.approval-sticky { z-index: 100; }
.approval-mobile-bar {
  gap: 8px;
  padding: 8px 8px calc(8px + env(safe-area-inset-bottom));
  background: var(--t-panel);
  border-top: 1px solid var(--t-border);
}
</style>
