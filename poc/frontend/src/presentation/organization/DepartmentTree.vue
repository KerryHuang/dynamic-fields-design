<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { buildDepartmentTree, flattenDepartmentTree, type Department } from 'src/domain/dynamic/organization';

/** Indented list of the department tree (desktop left pane and the phone list alike). */
const props = defineProps<{ departments: Department[]; selectedId: number | null; managerName: (userId: string) => string }>();
const emit = defineEmits<{ select: [id: number] }>();
const { t, locale } = useI18n();
const rows = computed(() => flattenDepartmentTree(buildDepartmentTree(props.departments)));
</script>

<template>
  <q-list separator>
    <q-item
      v-for="d in rows" :key="d.id" clickable :active="d.id === selectedId"
      :data-testid="`dept-node-${d.id}`" :data-depth="d.depth" @click="emit('select', d.id)"
    >
      <q-item-section :style="{ paddingLeft: `${Math.min(d.depth, 9) * 16}px` }">
        <q-item-label class="ellipsis">{{ resolveI18n(d.name, locale) }}</q-item-label>
        <q-item-label caption class="ellipsis">
          {{ d.managerUserId ? managerName(d.managerUserId) : t('organization.vacant') }}
        </q-item-label>
      </q-item-section>
      <q-item-section side>
        <div class="row items-center no-wrap q-gutter-xs">
          <q-badge v-if="!d.active" color="muted" :label="t('organization.inactive')" />
          <q-badge outline color="primary" :label="d.memberUserIds.length" />
        </div>
      </q-item-section>
    </q-item>
    <q-item v-if="!rows.length">
      <q-item-section class="text-muted">{{ t('organization.empty') }}</q-item-section>
    </q-item>
  </q-list>
</template>
