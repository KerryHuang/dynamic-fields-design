<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DeleteBlocked } from 'src/domain/dynamic/types';

const props = defineProps<{ modelValue: boolean; blocked: DeleteBlocked }>();
defineEmits<{ 'update:modelValue': [value: boolean] }>();
const { t, locale } = useI18n();
const remaining = computed(() => Math.max(0, props.blocked.total - props.blocked.references.length));
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="$emit('update:modelValue', $event)">
    <q-card style="min-width: 24rem">
      <q-card-section class="text-subtitle1" data-testid="blocked-message">{{ blocked.message }}</q-card-section>
      <q-list separator>
        <q-item v-for="r in blocked.references" :key="`${r.moduleKey}:${r.recordId}`" clickable :to="r.title ? `/dynamic/${r.moduleKey}/${r.recordId}` : undefined" data-testid="blocked-item">
          <q-item-section>
            <q-item-label>{{ r.title ?? t('dynamic.reference.restricted') }}</q-item-label>
            <q-item-label caption>{{ resolveI18n(r.moduleName, locale) }} · {{ resolveI18n(r.fieldLabel, locale) }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
      <q-card-section v-if="remaining > 0" class="text-grey" data-testid="blocked-remaining">{{ t('dynamic.deleteBlocked.remaining', { n: remaining }) }}</q-card-section>
      <q-card-actions align="right">
        <q-btn flat :label="t('dynamic.deleteBlocked.close')" v-close-popup />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>
