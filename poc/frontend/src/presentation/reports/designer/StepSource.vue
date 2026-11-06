<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { CatalogModule, ModuleCatalog } from 'src/domain/dynamic/report-types';
import { setChild, setMainModule, type DesignerState, type Unavailable } from './designer-state';

const props = defineProps<{ state: DesignerState; modules: CatalogModule[]; catalog: ModuleCatalog | null; unavailable: Unavailable }>();
const { t } = useI18n();
const s = props.state;

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
      :model-value="state.mainModuleKey" data-testid="main-module" @update:model-value="setMainModule(s, $event)"
    />
    <template v-if="state.mainModuleKey && catalog">
      <q-select
        dense outlined emit-value map-options :label="t('reports.designer.child')" :options="childOptions"
        :model-value="state.child ? childKey(state.child) : ''" data-testid="child-module" @update:model-value="pickChild"
      />
      <div class="text-caption text-grey-7">{{ t('reports.designer.childHint') }}</div>
    </template>
    <div v-if="unavailable.child" class="text-negative" data-testid="child-unavailable">
      {{ t('reports.designer.childUnavailable', { name: state.child?.moduleKey }) }}
      <q-btn flat dense no-caps color="primary" :label="t('reports.designer.remove')" data-testid="child-remove" @click="setChild(s, null)" />
    </div>
  </div>
</template>
