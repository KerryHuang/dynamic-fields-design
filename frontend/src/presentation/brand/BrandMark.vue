<script setup lang="ts">
// 品牌 mark（模塊 F）。三塊用 currentColor 跟著所在位置的文字色（淺色、深色主題都看得清楚），第四塊走 --t-brand-accent。
</script>

<template>
  <svg class="brand-mark" viewBox="0 0 64 64" aria-hidden="true" focusable="false">
    <rect x="10" y="8" width="14" height="48" rx="2" fill="currentColor" />
    <rect x="28" y="8" width="28" height="14" rx="2" fill="currentColor" />
    <rect x="28" y="28" width="16" height="12" rx="2" fill="currentColor" />
    <rect x="48" y="28" width="8" height="12" rx="2" class="brand-mark-accent" />
  </svg>
</template>

<style scoped>
.brand-mark {
  width: 1.6em;
  height: 1.6em;
  flex: none;
}
.brand-mark-accent {
  fill: var(--t-brand-accent);
}
</style>
