<script setup lang="ts">
import { date } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRecordEffects } from 'src/application/dynamic/queries/ledger-queries';

const props = defineProps<{ recordId: number }>();
const { t } = useI18n();

// A failed request (no permission, an old server) just leaves the section out.
const query = useRecordEffects(() => props.recordId);
const items = computed(() => query.data.value?.items ?? []);
const hidden = computed(() => query.data.value?.hiddenCount ?? 0);
const timeText = (iso: string) => date.formatDate(new Date(iso), 'YYYY-MM-DD HH:mm');
</script>

<template>
  <q-card v-if="items.length || hidden > 0" flat bordered class="q-mt-md" data-testid="record-effects">
    <q-card-section class="text-subtitle1">{{ t('events.results.title') }}</q-card-section>
    <q-list v-if="items.length" separator>
      <q-item v-for="x in items" :key="`${x.eventKey}/${x.effectCode}/${x.kind}/${x.targetRecordId}`" :data-testid="`effect-item-${x.targetRecordId}`">
        <q-item-section>
          <q-item-label>
            <router-link v-if="x.title !== null" :to="`/dynamic/${x.targetModuleKey}/${x.targetRecordId}`">{{ x.title || `#${x.targetRecordId}` }}</router-link>
            <span v-else class="text-muted">{{ t('events.results.noAccess') }}</span>
          </q-item-label>
          <q-item-label caption class="effect-caption">
            {{ x.targetModuleName }} · {{ t(x.kind === 'reverse' ? 'events.results.reversed' : 'events.results.created') }} · {{ x.eventKey }} / {{ x.effectCode }} · {{ timeText(x.createdTime) }}
          </q-item-label>
        </q-item-section>
      </q-item>
    </q-list>
    <q-card-section v-if="hidden > 0" class="text-muted" data-testid="effects-hidden">{{ t('events.results.hidden', { n: hidden }) }}</q-card-section>
  </q-card>
</template>

<style scoped>
.effect-caption { overflow-wrap: anywhere; }
</style>
