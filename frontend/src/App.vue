<template>
  <router-view />
</template>

<script setup lang="ts">
import { useThemeSync } from 'src/application/dynamic/queries/theme-queries';

useThemeSync();
</script>
