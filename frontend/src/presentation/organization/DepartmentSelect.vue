<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDepartments } from 'src/application/dynamic/queries/organization-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { buildDepartmentTree, departmentPath, flattenDepartmentTree } from 'src/domain/dynamic/organization';

/** Tree-ordered department dropdown; `assignable` disables inactive departments (they cannot take new members). */
const props = defineProps<{ modelValue: number | null; label: string; assignable?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: number | null] }>();
const { t, locale } = useI18n();
const departments = useDepartments();

const options = computed(() =>
  flattenDepartmentTree(buildDepartmentTree(departments.data.value ?? [])).map((d) => ({
    value: d.id,
    label: resolveI18n(d.name, locale.value) + (d.active ? '' : `（${t('organization.inactive')}）`),
    depth: d.depth,
    disable: !!props.assignable && !d.active,
  })),
);
const display = computed(() => departmentPath(departments.data.value ?? [], props.modelValue, locale.value));
</script>

<template>
  <q-select
    :model-value="modelValue" :options="options" :label="label" :display-value="display"
    emit-value map-options clearable dense outlined options-dense
    @update:model-value="(v: number | null) => emit('update:modelValue', v ?? null)"
  >
    <template #option="{ itemProps, opt }">
      <q-item v-bind="itemProps">
        <q-item-section :style="{ paddingLeft: `${opt.depth * 16}px` }">{{ opt.label }}</q-item-section>
      </q-item>
    </template>
  </q-select>
</template>
