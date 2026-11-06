<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import type { CatalogModule, ModuleCatalog } from 'src/domain/dynamic/report-types';
import { hasContent, setChild, setMainModule, type DesignerState, type Unavailable } from './designer-state';

const props = defineProps<{ state: DesignerState; modules: CatalogModule[]; catalog: ModuleCatalog | null; unavailable: Unavailable }>();
const { t } = useI18n();
const s = props.state;

// 改主模組會清空欄位、條件等設定，有內容時先確認
const pendingMain = ref<string | null>(null);
function pickMain(key: string) {
  if (s.mainModuleKey && key !== s.mainModuleKey && hasContent(s)) pendingMain.value = key;
  else setMainModule(s, key);
}
function confirmMain() {
  setMainModule(s, pendingMain.value);
  pendingMain.value = null;
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
      dense outlined emit-value map-options :label="t('reports.designer.mainModule')" :options="modules.map((m) => ({ value: m.key, label: m.name }))"
      :model-value="state.mainModuleKey" data-testid="main-module" @update:model-value="pickMain"
    />
    <template v-if="state.mainModuleKey && catalog">
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
    <q-dialog :model-value="pendingMain !== null" persistent>
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('reports.designer.changeMainTitle') }}</q-card-section>
        <q-card-section>{{ t('reports.designer.changeMainBody') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" data-testid="main-change-cancel" @click="pendingMain = null" />
          <q-btn color="negative" :label="t('reports.designer.changeMainConfirm')" data-testid="main-change-confirm" @click="confirmMain" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
