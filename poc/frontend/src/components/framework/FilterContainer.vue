<script setup lang="ts">
defineProps<{ title: string; applyLabel: string; clearLabel: string }>();
defineEmits<{ apply: []; clear: [] }>();
</script>

<template>
  <q-expansion-item :label="title" icon="filter_list" default-opened dense class="filter-container">
    <q-card flat bordered>
      <q-card-section><slot /></q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="clearLabel" data-testid="clear" @click="$emit('clear')" />
        <q-btn color="primary" unelevated :label="applyLabel" data-testid="apply" @click="$emit('apply')" />
      </q-card-actions>
    </q-card>
  </q-expansion-item>
</template>
