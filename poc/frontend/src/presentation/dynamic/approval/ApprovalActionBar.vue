<script setup lang="ts">
import { QPageSticky, useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import RowActionMenu, { type RowAction } from 'src/components/framework/RowActionMenu.vue';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { approvalTaskActions, canReturnPrevious, hasApprovalActions, myOpenTask, type ApprovalActionContext } from './approval-actions';
import ApproveDialog from './ApproveDialog.vue';
import RejectDialog from './RejectDialog.vue';
import WithdrawDialog from './WithdrawDialog.vue';

const props = defineProps<{ instance: ApprovalInstanceView; recordId: number; moduleKey: string }>();
const { t } = useI18n();
const $q = useQuasar();
const identity = useIdentityStore();

const task = computed(() => myOpenTask(props.instance));
const isInitiator = computed(() => !!identity.userId && props.instance.initiatorUserId === identity.userId);
const show = computed(() => hasApprovalActions(props.instance, identity.userId));
const dialog = ref<'approve' | 'reject' | 'withdraw' | null>(null);

interface Btn { key: 'approve' | 'reject' | 'withdraw'; color: string; outline?: boolean; disable?: boolean }
const buttons = computed<Btn[]>(() => {
  const list: Btn[] = [];
  if (task.value) list.push({ key: 'approve', color: 'primary' }, { key: 'reject', color: 'negative', outline: true });
  if (isInitiator.value && props.instance.status === 'pending') list.push({ key: 'withdraw', color: 'primary', outline: true, disable: !props.instance.canWithdraw });
  return list;
});
const withdrawBlocked = computed(() => buttons.value.some((b) => b.key === 'withdraw' && b.disable));

const context = computed<ApprovalActionContext | null>(() => (task.value
  ? { instance: props.instance, task: task.value, recordId: props.recordId, refresh: () => {} }
  : null));
const extras = computed<RowAction[]>(() => {
  const c = context.value;
  return c ? approvalTaskActions.filter((a) => a.visible(c)).map((a) => ({ label: a.label(t), run: () => a.run(c), testid: `approval-extra-${a.key}` })) : [];
});

// Phone: at most two buttons on the bottom bar; the rest and the extra actions go to the menu.
const mobile = computed(() => $q.screen.lt.sm);
const inline = computed(() => (mobile.value ? buttons.value.slice(0, 2) : buttons.value));
const overflow = computed<RowAction[]>(() => [
  ...(mobile.value ? buttons.value.slice(2).map((b) => ({ label: t(`approval.actions.${b.key}`), run: () => { dialog.value = b.key; }, disable: b.disable })) : []),
  ...extras.value,
]);
</script>

<template>
  <component :is="mobile ? QPageSticky : 'div'" v-if="show" v-bind="mobile ? { position: 'bottom', expand: true } : {}">
    <div
      class="row items-center justify-end q-gutter-sm no-wrap"
      :class="mobile ? 'q-pa-sm full-width approval-mobile-bar' : ''"
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
    @update:model-value="dialog = null"
  />
  <RejectDialog
    v-if="dialog === 'reject' && task" :model-value="true" :record-id="recordId" :module-key="moduleKey" :task-id="task.id" :row-version="instance.rowVersion"
    :can-return-previous="canReturnPrevious(instance)" @update:model-value="dialog = null"
  />
  <WithdrawDialog
    v-if="dialog === 'withdraw'" :model-value="true" :record-id="recordId" :module-key="moduleKey" :instance-id="instance.id" :row-version="instance.rowVersion"
    @update:model-value="dialog = null"
  />
</template>

<style scoped>
.approval-mobile-bar { background: var(--t-panel); border-top: 1px solid var(--t-border); }
</style>
