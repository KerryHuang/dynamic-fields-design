<script setup lang="ts">
import { computed, ref, toRef } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRecordConversions } from 'src/application/dynamic/queries/conversions-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { RecordConversion } from 'src/domain/dynamic/types';
import ConvertDialog from './ConvertDialog.vue';

const props = defineProps<{ recordId: number; rowVersion: string }>();
const { t, locale } = useI18n();
const query = useRecordConversions(toRef(props, 'recordId'));
const conversions = computed(() => query.data.value ?? []);

// Keyed, not held as an object: the list refetches after a conversion and the dialog must see the fresh remaining quantities.
const openKey = ref<string | null>(null);
const open = computed(() => conversions.value.find((c) => c.key === openKey.value));
const nameOf = (c: RecordConversion) => resolveI18n(c.name, locale.value);
const targetNameOf = (c: RecordConversion) => resolveI18n(c.targetName, locale.value);
</script>

<template>
  <div v-if="conversions.length" class="q-mb-md q-gutter-y-sm" data-testid="record-conversions">
    <div v-for="c in conversions" :key="c.key" :data-testid="`conversion-${c.key}`">
      <div class="row items-center q-gutter-sm">
        <q-btn
          v-if="c.allowed" outline color="primary" icon="swap_horiz" :label="t('docflow.convert.button', { name: targetNameOf(c) })"
          :data-testid="`convert-open-${c.key}`" @click="openKey = c.key"
        />
        <div v-else class="text-muted" :data-testid="`convert-reason-${c.key}`">{{ nameOf(c) }}：{{ c.reason }}</div>
      </div>
      <div v-if="c.targets.length" class="q-mt-xs" :data-testid="`convert-targets-${c.key}`">
        <span class="text-caption text-muted">{{ t('docflow.convert.targets', { name: targetNameOf(c) }) }}：</span>
        <template v-for="(target, i) in c.targets" :key="target.id">
          <span v-if="i">、</span>
          <router-link :to="`/dynamic/${target.moduleKey}/${target.id}`">{{ target.title }}</router-link>
        </template>
      </div>
    </div>
    <ConvertDialog
      v-if="open" :model-value="true" :record-id="recordId" :row-version="rowVersion" :conversion="open"
      @update:model-value="openKey = null"
    />
  </div>
</template>
