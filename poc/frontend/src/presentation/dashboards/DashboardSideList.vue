<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useQuasar } from 'quasar';
import type { DashboardList } from 'src/domain/dynamic/dashboard-types';

/** 儀表板清單：指派版在前（管理員順序），自己的在後。手機寬度預設收合，避免佔掉版面。 */
const props = defineProps<{ list: DashboardList; currentId: number | null }>();
const emit = defineEmits<{ select: [id: number]; create: []; remove: [id: number] }>();
const { t } = useI18n();
const $q = useQuasar();
const narrow = computed(() => $q.screen.lt.sm);
const currentName = computed(() =>
  [...props.list.assigned, ...props.list.mine].find((d) => d.id === props.currentId)?.name ?? t('dashboards.title'));
</script>

<template>
  <q-expansion-item
    :default-opened="!narrow" :label="narrow ? currentName : t('dashboards.title')" icon="dashboard" header-class="text-weight-medium"
    class="dashboard-side-list" data-testid="dashboard-list"
  >
    <q-list dense>
      <q-item
        v-for="d in list.assigned" :key="`a${d.id}`" clickable :active="d.id === currentId" :data-testid="`dashboard-item-${d.id}`"
        @click="emit('select', d.id)"
      >
        <q-item-section>{{ d.name }}</q-item-section>
        <q-item-section v-if="d.hasUpdate || d.adjusted" side>
          <q-badge v-if="d.hasUpdate" color="info" :label="t('dashboards.updated')" />
          <q-badge v-else color="primary" outline :label="t('dashboards.adjusted')" />
        </q-item-section>
      </q-item>
      <q-item-label v-if="list.mine.length" header class="q-pb-none">{{ t('dashboards.mine') }}</q-item-label>
      <q-item
        v-for="d in list.mine" :key="`m${d.id}`" clickable :active="d.id === currentId" :data-testid="`dashboard-item-${d.id}`"
        @click="emit('select', d.id)"
      >
        <q-item-section>{{ d.name }}</q-item-section>
        <q-item-section side>
          <q-btn
            flat dense round size="sm" icon="delete" :aria-label="t('dashboards.delete')" :data-testid="`dashboard-delete-${d.id}`"
            @click.stop="emit('remove', d.id)"
          />
        </q-item-section>
      </q-item>
      <q-item clickable data-testid="dashboard-create" @click="emit('create')">
        <q-item-section avatar><q-icon name="add" /></q-item-section>
        <q-item-section>{{ t('dashboards.create') }}</q-item-section>
      </q-item>
    </q-list>
  </q-expansion-item>
</template>
