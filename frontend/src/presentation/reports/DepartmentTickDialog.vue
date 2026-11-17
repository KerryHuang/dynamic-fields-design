<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDepartments } from 'src/application/dynamic/queries/organization-queries';
import { buildDepartmentTree, type DepartmentNode } from 'src/domain/dynamic/organization';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';

/**
 * 報表頁的部門篩選：整棵部門樹（含停用部門）多選，另有「包含下層部門」。每個節點獨立勾選，是否含下層由勾選框決定，
 * 按「套用」才生效（避免勾一下就重跑一次報表）。手機全螢幕。
 */
const props = defineProps<{ open: boolean; selected: number[]; includeSub: boolean }>();
const emit = defineEmits<{ apply: [ids: number[], includeSub: boolean]; close: [] }>();
const { t, locale } = useI18n();
const departments = useDepartments();

interface TreeNode { id: number; label: string; children: TreeNode[] }
const toNode = (d: DepartmentNode): TreeNode => ({
  id: d.id,
  label: `${resolveI18n(d.name, locale.value)}${d.active ? '' : t('reports.filter.inactiveMark')}`,
  children: d.children.map(toNode),
});
const nodes = computed(() => buildDepartmentTree(departments.data.value ?? []).map(toNode));

const ticked = ref<number[]>([]);
const sub = ref(true);
watch(() => props.open, (o) => { if (o) { ticked.value = [...props.selected]; sub.value = props.includeSub; } }, { immediate: true });
</script>

<template>
  <q-dialog :model-value="open" :maximized="$q.screen.lt.sm" @update:model-value="emit('close')">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 420px; max-width: 95vw'" class="column no-wrap" data-testid="dept-filter-dialog">
      <q-card-section class="text-h6">{{ t('reports.filter.departmentTitle') }}</q-card-section>
      <q-card-section class="col scroll" style="min-height: 120px; max-height: 60vh">
        <div v-if="departments.isError.value" class="text-negative" data-testid="dept-filter-error">{{ t('reports.loadFailed') }}</div>
        <q-linear-progress v-else-if="departments.isLoading.value" indeterminate />
        <div v-else-if="!nodes.length" class="text-muted" data-testid="dept-filter-empty">{{ t('reports.filter.noDepartments') }}</div>
        <q-tree
          v-else v-model:ticked="ticked" :nodes="nodes" node-key="id" tick-strategy="strict" default-expand-all no-nodes-label=""
          data-testid="dept-filter-tree"
        />
      </q-card-section>
      <q-card-section class="q-pt-none">
        <q-checkbox v-model="sub" dense :label="t('reports.filter.includeSub')" data-testid="dept-filter-sub" />
        <div class="text-caption text-muted q-mt-xs">{{ t('reports.filter.unassignedNote') }}</div>
      </q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="t('reports.filter.clear')" data-testid="dept-filter-clear" @click="ticked = []" />
        <q-space />
        <q-btn flat no-caps :label="t('reports.cancel')" data-testid="dept-filter-cancel" @click="emit('close')" />
        <q-btn color="primary" no-caps :label="t('reports.filter.apply')" data-testid="dept-filter-apply" @click="emit('apply', ticked, sub)" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
