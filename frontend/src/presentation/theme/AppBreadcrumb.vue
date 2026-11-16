<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute } from 'vue-router';
import type { MenuItem } from 'src/domain/dynamic/types';
import { breadcrumbFor } from './breadcrumb';

const props = defineProps<{ menu: MenuItem[] }>();
const { t, locale } = useI18n();
const route = useRoute();
const parts = computed(() => breadcrumbFor(route.path, props.menu, t, locale.value));
</script>

<template>
  <div v-if="parts.length" class="q-px-md q-pb-xs text-caption" data-testid="breadcrumb">
    <!-- 純文字：不加 to，所以不是連結 -->
    <q-breadcrumbs separator="›" class="theme-breadcrumb">
      <q-breadcrumbs-el v-for="p in parts" :key="p" :label="p" />
    </q-breadcrumbs>
  </div>
</template>
