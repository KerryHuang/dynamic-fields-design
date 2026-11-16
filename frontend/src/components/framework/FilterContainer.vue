<script setup lang="ts">

defineProps<{ title: string; applyLabel: string; clearLabel: string }>();
defineEmits<{ apply: []; clear: [] }>();
/** 展開狀態由使用者或父層控制；沒人管時預設展開。 */
const open = defineModel<boolean>('open', { default: true });
</script>

<template>
  <q-expansion-item v-model="open" :label="title" icon="filter_list" dense class="filter-container">
    <!-- 清除與套用放在標題列（展開時），條件列才不必再多占一整列 -->
    <template #header>
      <q-item-section avatar><q-icon name="filter_list" /></q-item-section>
      <q-item-section>{{ title }}</q-item-section>
      <q-item-section v-if="open" side>
        <div class="row no-wrap q-gutter-x-xs" @click.stop>
          <q-btn flat dense no-caps :label="clearLabel" data-testid="clear" @click="$emit('clear')" />
          <q-btn color="primary" unelevated dense no-caps :label="applyLabel" data-testid="apply" @click="$emit('apply')" />
        </div>
      </q-item-section>
    </template>
    <q-card flat bordered>
      <q-card-section><slot /></q-card-section>
    </q-card>
  </q-expansion-item>
</template>
