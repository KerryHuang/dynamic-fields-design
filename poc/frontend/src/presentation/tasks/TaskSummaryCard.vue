<template>
  <section v-if="summary.isSuccess.value && counts" class="q-mb-lg" data-testid="home-tasks">
    <div class="text-subtitle1 q-mb-sm">{{ t('menu.myTasks') }}</div>
    <div class="row q-col-gutter-sm">
      <div class="col-12 col-sm-6 col-md-4 col-lg-3">
        <q-card flat bordered>
          <q-item clickable to="/tasks?filter=thisWeek" data-testid="home-tasks-due-today">
            <q-item-section avatar><q-icon name="event" /></q-item-section>
            <q-item-section>{{ t('home.tasksDueToday') }}</q-item-section>
            <q-item-section side class="text-h6">{{ counts.dueToday }}</q-item-section>
          </q-item>
        </q-card>
      </div>
      <div class="col-12 col-sm-6 col-md-4 col-lg-3">
        <q-card flat bordered>
          <q-item clickable to="/tasks?filter=overdue" data-testid="home-tasks-overdue">
            <q-item-section avatar><q-icon name="warning" :color="counts.overdue > 0 ? 'negative' : undefined" /></q-item-section>
            <q-item-section>{{ t('home.tasksOverdue') }}</q-item-section>
            <q-item-section side class="text-h6" :class="{ 'text-negative': counts.overdue > 0 }">{{ counts.overdue }}</q-item-section>
          </q-item>
        </q-card>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useTaskSummary } from 'src/application/dynamic/queries/tasks-queries';

const { t } = useI18n();
const summary = useTaskSummary();
// A failed request hides the whole card: the home page works without it.
const counts = computed(() => summary.data.value);
</script>
