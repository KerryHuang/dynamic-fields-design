<script setup lang="ts">
import { computed, watch } from 'vue';
import { useRouter } from 'vue-router';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';

const router = useRouter();
const me = useMe();
const menu = useMenu();
const target = computed(() => {
  if (me.data.value?.designer.read) return '/system/dynamic-modules';
  const first = menu.data.value?.[0];
  return first ? `/dynamic/${first.key}` : null;
});
const settled = computed(() => !me.isFetching.value && !menu.isFetching.value);

watch(target, (to) => { if (to) router.replace(to); }, { immediate: true });
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="settled && !target" />
  </q-page>
</template>
