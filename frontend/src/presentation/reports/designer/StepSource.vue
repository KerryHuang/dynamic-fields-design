<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { CatalogModule, ModuleCatalog, ReportDatasetItem } from 'src/domain/dynamic/report-types';
import { hasContent, setChild, setSource, type DesignerState, type Unavailable } from './designer-state';

const props = defineProps<{
  state: DesignerState; modules: CatalogModule[]; datasets: ReportDatasetItem[]; catalog: ModuleCatalog | null; unavailable: Unavailable;
}>();
const { t } = useI18n();
const s = props.state;

// 資料來源與動態模組放同一個下拉，用分組標題分開（主控裁決）；值是 module:<key> 或 dataset:<name>
interface SourceOption { value: string; label: string; header?: boolean; disable?: boolean }
const sourceOptions = computed<SourceOption[]>(() => [
  ...(props.datasets.length ? [{ value: '#system', label: t('reports.designer.sourceSystem'), header: true, disable: true }] : []),
  ...props.datasets.map((d) => ({ value: `dataset:${d.dataset}`, label: d.name })),
  ...(props.modules.length ? [{ value: '#modules', label: t('reports.designer.sourceModules'), header: true, disable: true }] : []),
  ...props.modules.map((m) => ({ value: `module:${m.key}`, label: m.name })),
]);
const sourceValue = computed(() => (s.dataset === 'module' ? (s.mainModuleKey ? `module:${s.mainModuleKey}` : null) : `dataset:${s.dataset}`));
const parseSource = (v: string) => (v.startsWith('dataset:')
  ? { dataset: v.slice(8) as Exclude<DesignerState['dataset'], 'module'>, key: null }
  : { dataset: 'module' as const, key: v.slice(7) });

// 改資料來源會清空欄位、條件等設定，有內容時先確認
const pending = ref<ReturnType<typeof parseSource> | null>(null);
function pickSource(v: string) {
  const next = parseSource(v);
  if (next.dataset === s.dataset && next.key === s.mainModuleKey) return;
  if ((s.dataset !== 'module' || s.mainModuleKey) && hasContent(s)) pending.value = next;
  else setSource(s, next.dataset, next.key);
}
function confirmSource() {
  if (pending.value) setSource(s, pending.value.dataset, pending.value.key);
  pending.value = null;
}

const childKey = (c: { moduleKey: string; linkFieldId: number }) => `${c.moduleKey}:${c.linkFieldId}`;
const childOptions = computed(() => [
  { value: '', label: t('reports.designer.childNone') },
  ...(props.catalog?.children ?? []).map((c) => ({
    value: childKey(c), label: `${c.name}（${c.linkLabel}${c.isDetail ? `，${t('reports.designer.detail')}` : ''}）`,
  })),
]);
function pickChild(v: string) {
  const c = props.catalog?.children.find((x) => childKey(x) === v);
  setChild(s, c ? { moduleKey: c.moduleKey, linkFieldId: c.linkFieldId } : null);
}
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="designer-step-0">
    <q-select
      dense outlined emit-value map-options :label="t('reports.designer.source')" :options="sourceOptions"
      :model-value="sourceValue" data-testid="main-module" @update:model-value="pickSource"
    >
      <template #option="scope">
        <q-item-label v-if="scope.opt.header" header class="text-weight-bold">{{ scope.opt.label }}</q-item-label>
        <q-item v-else v-bind="scope.itemProps"><q-item-section>{{ scope.opt.label }}</q-item-section></q-item>
      </template>
    </q-select>
    <div v-if="state.dataset !== 'module'" class="text-caption text-muted" data-testid="dataset-hint">{{ t(`reports.designer.datasetHint.${state.dataset}`) }}</div>
    <template v-if="state.dataset === 'module' && state.mainModuleKey && catalog">
      <q-select
        dense outlined emit-value map-options :label="t('reports.designer.child')" :options="childOptions"
        :model-value="state.child ? childKey(state.child) : ''" data-testid="child-module" @update:model-value="pickChild"
      />
      <div class="text-caption text-muted">{{ t('reports.designer.childHint') }}</div>
    </template>
    <div v-if="unavailable.child" class="text-negative" data-testid="child-unavailable">
      {{ t('reports.designer.childUnavailable', { name: state.child?.moduleKey }) }}
      <q-btn flat dense no-caps color="primary" :label="t('reports.designer.remove')" data-testid="child-remove" @click="setChild(s, null)" />
    </div>
    <q-dialog :model-value="pending !== null" persistent>
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('reports.designer.changeMainTitle') }}</q-card-section>
        <q-card-section>{{ t('reports.designer.changeMainBody') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" data-testid="main-change-cancel" @click="pending = null" />
          <q-btn color="negative" :label="t('reports.designer.changeMainConfirm')" data-testid="main-change-confirm" @click="confirmSource" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
