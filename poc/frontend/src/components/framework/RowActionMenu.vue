<script setup lang="ts">
import { useI18n } from 'vue-i18n';

/** 手機寬度把列上的次要動作收進「⋮」選單。刪除這類危險動作放最後並標 negative；確認對話框由呼叫端的 run 開。 */
export interface RowAction {
  label: string;
  run: () => void;
  disable?: boolean;
  negative?: boolean;
  testid?: string;
}
defineProps<{ actions: RowAction[]; testid?: string; ariaLabel?: string }>();
const { t } = useI18n();
</script>

<template>
  <q-btn flat round dense icon="more_vert" :aria-label="ariaLabel ?? t('common.moreActions')" :data-testid="testid" @click.stop>
    <q-menu>
      <q-list dense>
        <q-item
          v-for="(a, i) in actions" :key="i" v-close-popup clickable :disable="a.disable" :class="{ 'text-negative': a.negative }"
          :data-testid="a.testid" @click="a.run()"
        >
          <q-item-section>{{ a.label }}</q-item-section>
        </q-item>
      </q-list>
    </q-menu>
  </q-btn>
</template>
