<script setup lang="ts">
import { useI18n } from 'vue-i18n';

// 設計頁工具列（§10.2）：唯讀者只有「全圖」（節點清單、試算隨後續段加入）；手機只留圖示。
// 捨棄草稿只在「已發布過、而且有草稿」時出現；只有草稿、從未發布的流程沒有版本可回，改在更多選單提供「刪除流程」（U64）。
defineProps<{
  readonly: boolean; phone: boolean; showFlowProps: boolean; undoable: boolean; redoable: boolean; dirty: boolean; busy: boolean;
  errorCount: number; canDiscard: boolean; canDelete: boolean;
}>();
const emit = defineEmits<{ fit: []; flowProps: []; undo: []; redo: []; validate: []; save: []; publish: []; discard: []; deleteFlow: []; issues: [] }>();
const { t } = useI18n();
</script>

<template>
  <div class="row items-center no-wrap q-gutter-x-xs de-toolbar" role="toolbar" :aria-label="t('approval.designer.graph.title')" data-testid="de-toolbar">
    <q-btn flat dense no-caps icon="fit_screen" :label="phone ? undefined : t('approval.designer.graph.fit')" :aria-label="t('approval.designer.graph.fit')" data-testid="tb-fit" @click="emit('fit')" />
    <slot name="after-fit" />
    <q-btn
      v-if="showFlowProps" flat dense no-caps icon="tune" :label="phone ? undefined : t('approval.designer.graph.flowProps')" :aria-label="t('approval.designer.graph.flowProps')"
      data-testid="tb-flow-props" @click="emit('flowProps')"
    />
    <template v-if="!readonly">
      <q-btn flat dense round icon="undo" :disable="!undoable" :aria-label="t('approval.designer.graph.undo')" data-testid="tb-undo" @click="emit('undo')" />
      <q-btn flat dense round icon="redo" :disable="!redoable" :aria-label="t('approval.designer.graph.redo')" data-testid="tb-redo" @click="emit('redo')" />
      <q-btn flat dense no-caps icon="rule" :label="phone ? undefined : t('approval.designer.graph.validate')" :aria-label="t('approval.designer.graph.validate')" :disable="busy" data-testid="tb-validate" @click="emit('validate')" />
      <q-btn
        v-if="errorCount > 0" flat dense no-caps color="negative" icon="error" :label="String(errorCount)" :aria-label="t('approval.designer.graph.errorsBadge', { n: errorCount })"
        data-testid="tb-errors" @click="emit('issues')"
      />
      <q-btn
        :flat="!dirty" :unelevated="dirty" dense no-caps :color="dirty ? 'primary' : undefined" icon="save" :label="phone ? undefined : t('approval.designer.graph.saveDraft')"
        :aria-label="t('approval.designer.graph.saveDraft')" :disable="busy" data-testid="tb-save" @click="emit('save')"
      />
      <q-btn unelevated dense no-caps color="primary" icon="publish" :label="phone ? undefined : t('approval.designer.graph.publish')" :aria-label="t('approval.designer.graph.publish')" :disable="busy" data-testid="tb-publish" @click="emit('publish')" />
      <q-btn v-if="canDiscard || canDelete" flat dense round icon="more_vert" :aria-label="t('approval.designer.graph.moreActions')" data-testid="tb-more">
        <q-menu>
          <q-list dense>
            <q-item v-if="canDiscard" v-close-popup clickable data-testid="tb-discard" @click="emit('discard')"><q-item-section>{{ t('approval.designer.graph.discard') }}</q-item-section></q-item>
            <q-item v-if="canDelete" v-close-popup clickable data-testid="tb-delete" @click="emit('deleteFlow')"><q-item-section class="text-negative">{{ t('approval.designer.graph.deleteFlow') }}</q-item-section></q-item>
          </q-list>
        </q-menu>
      </q-btn>
    </template>
    <slot />
  </div>
</template>
