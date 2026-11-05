<script setup lang="ts">
import { date } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRelatedPaged, useRelatedTabs } from 'src/application/dynamic/queries/relation-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';

const props = defineProps<{ recordId: number }>();
const { t, locale } = useI18n();
const SIZE = 10;

const recordId = computed(() => props.recordId);
const tabsQuery = useRelatedTabs(recordId);
const tabs = computed(() => tabsQuery.data.value ?? []);
const selected = ref<number>();
const active = computed(() => tabs.value.find((x) => x.fieldId === selected.value) ?? tabs.value[0]);
const page = ref(1);
const size = ref(SIZE);
const activeId = computed(() => active.value?.fieldId);
const pagedQuery = useRelatedPaged(recordId, activeId, page, size);
const items = computed(() => pagedQuery.data.value?.items ?? []);
const pageCount = computed(() => Math.max(1, Math.ceil((pagedQuery.data.value?.totalCount ?? 0) / SIZE)));

function select(id: number) {
  selected.value = id;
  page.value = 1;
}
const tabLabel = (x: { moduleName: Record<string, string | undefined>; fieldLabel: Record<string, string | undefined> }) =>
  `${resolveI18n(x.moduleName, locale.value)} · ${resolveI18n(x.fieldLabel, locale.value)}`;
</script>

<template>
  <q-card v-if="tabs.length > 0 && active" flat bordered class="q-mt-md" data-testid="related-tabs">
    <q-tabs :model-value="active.fieldId" align="left" dense @update:model-value="select">
      <q-tab v-for="x in tabs" :key="x.fieldId" :name="x.fieldId" :label="tabLabel(x)" :data-testid="`related-tab-${x.fieldId}`" />
    </q-tabs>
    <q-separator />
    <div v-if="active.canAdd" class="q-pa-sm">
      <q-btn color="primary" dense icon="add" :label="t('dynamic.related.add')" data-testid="related-add"
        :to="`/dynamic/${active.moduleKey}/new?prefill=${active.fieldKey}:${recordId}`" />
    </div>
    <q-list separator>
      <q-item v-for="r in items" :key="r.id" clickable :to="`/dynamic/${active.moduleKey}/${r.id}`" data-testid="related-row">
        <q-item-section>{{ r.title || `#${r.id}` }}</q-item-section>
        <q-item-section side>{{ date.formatDate(new Date(r.modifiedTime), 'YYYY-MM-DD HH:mm') }}</q-item-section>
      </q-item>
      <q-item v-if="items.length === 0 && !pagedQuery.isFetching.value">
        <q-item-section class="text-grey">{{ t('dynamic.related.empty') }}</q-item-section>
      </q-item>
    </q-list>
    <div v-if="pageCount > 1" class="flex flex-center q-pa-sm">
      <q-pagination v-model="page" :max="pageCount" direction-links data-testid="related-pagination" />
    </div>
  </q-card>
</template>
