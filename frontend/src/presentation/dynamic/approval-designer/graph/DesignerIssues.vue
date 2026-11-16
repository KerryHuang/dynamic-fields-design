<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { issueRows, type Selection } from './designer-graph';
import type { GraphDesigner } from './use-graph-designer';

// 錯誤與警告清單（§10.2）：點一項選取目標、畫布移到該節點、開屬性面板（由設計頁處理）。
const props = defineProps<{ designer: GraphDesigner }>();
const emit = defineEmits<{ pick: [target: Selection]; close: [] }>();
const { t, locale } = useI18n();
const rows = computed(() => (props.designer.state.value ? issueRows(props.designer.issues.value, props.designer.state.value.definition, { locale: locale.value, t }) : []));
</script>

<template>
  <section class="de-issues" role="region" :aria-label="t('approval.designer.graph.issuesTitle')" data-testid="de-issues">
    <div class="row items-center no-wrap q-px-sm">
      <div class="col text-subtitle2">{{ t('approval.designer.graph.issuesTitle') }}</div>
      <q-btn flat dense round size="sm" icon="close" :aria-label="t('approval.designer.graph.close')" data-testid="de-issues-close" @click="emit('close')" />
    </div>
    <div v-if="!rows.length" class="q-px-sm q-pb-sm text-muted" data-testid="de-issues-empty">{{ t('approval.designer.graph.issuesEmpty') }}</div>
    <q-list v-else dense separator>
      <q-item v-for="(r, i) in rows" :key="i" clickable class="de-issue" :data-testid="`de-issue-${i}`" :data-severity="r.severity" @click="emit('pick', r.target)">
        <q-item-section avatar>
          <q-icon :name="r.severity === 'error' ? 'error' : 'warning'" :color="r.severity === 'warning' ? 'warning' : 'negative'" size="18px" />
        </q-item-section>
        <q-item-section>
          <q-item-label>{{ r.title }}</q-item-label>
          <q-item-label caption>{{ r.severity === 'warning' ? `${t('approval.designer.graph.warningLabel')}：` : '' }}{{ r.message }}</q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
  </section>
</template>

<style scoped>
.de-issues { max-height: 30%; overflow: auto; border-bottom: 1px solid var(--t-border); background: var(--t-panel); }
</style>
