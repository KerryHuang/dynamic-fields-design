<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import FilterContainer from 'src/components/framework/FilterContainer.vue';
import { isNestedCondition, pruneCondition } from 'src/domain/dynamic/condition';
import type { ConditionNode, ModuleSchema } from 'src/domain/dynamic/types';
import ConditionEditor from './ConditionEditor.vue';

const props = defineProps<{ schema: ModuleSchema; modelValue?: ConditionNode }>();
const emit = defineEmits<{ 'update:modelValue': [value: ConditionNode | undefined] }>();
const { t } = useI18n();

const fields = computed(() => props.schema.blocks.flatMap((b) => b.fields).filter((f) => f.config.isFilterable));
const complex = computed(() => isNestedCondition(props.modelValue));

// The editor works on a draft; the list only changes on apply. Fields are filter-only: no fieldRef here.
const draft = ref<ConditionNode | undefined>(props.modelValue);
watch(() => props.modelValue, (v) => { draft.value = v; });

// 沒有任何條件時預設收合，把空間讓給清單；有條件（含稍後才載入的檢視）或使用者手動展開才展開。
const hasCondition = computed(() => !!pruneCondition(props.modelValue));
const open = ref(hasCondition.value);
watch(hasCondition, (has) => { if (has) open.value = true; });

function apply() {
  emit('update:modelValue', pruneCondition(complex.value ? props.modelValue : draft.value));
}
function clear() {
  draft.value = undefined;
  emit('update:modelValue', undefined);
}
</script>

<template>
  <FilterContainer v-model:open="open" :title="t('dynamic.filter.title')" :apply-label="t('dynamic.filter.apply')" :clear-label="t('dynamic.filter.clear')" @apply="apply" @clear="clear">
    <div v-if="complex" class="text-muted" data-testid="complex-notice">{{ t('dynamic.filter.complex') }}</div>
    <div v-else-if="fields.length === 0" class="text-muted" data-testid="no-filter-fields">{{ t('dynamic.filter.noFields') }}</div>
    <ConditionEditor v-else v-model="draft" :fields="fields" />
  </FilterContainer>
</template>
