<script setup lang="ts">
import { Notify } from 'quasar';
import { useI18n } from 'vue-i18n';
import { useSelectTheme } from 'src/application/dynamic/queries/theme-queries';
import type { ThemeMode, ThemeStyle } from 'src/domain/theme/theme-model';
import { useThemeStore } from 'src/stores/use-theme-store';
import ThemeChooser from './ThemeChooser.vue';

defineProps<{ modelValue: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [boolean] }>();

const { t } = useI18n();
const theme = useThemeStore();
const select = useSelectTheme();

function save(mode: ThemeMode | null, style: ThemeStyle | null) {
  select({ mode, style }, () => Notify.create({ type: 'negative', message: t('theme.saveFailed') }));
}
</script>

<template>
  <q-dialog :model-value="modelValue" @update:model-value="emit('update:modelValue', $event)">
    <q-card style="width: 560px; max-width: 95vw">
      <q-card-section class="row items-center">
        <div class="text-h6 col">{{ t('theme.title') }}</div>
        <q-btn v-close-popup flat round dense icon="close" data-testid="theme-close" />
      </q-card-section>
      <q-card-section>
        <div v-if="!theme.canSave" class="q-mb-md text-muted" data-testid="theme-demo-hint">{{ t('theme.demoLocalHint') }}</div>
        <ThemeChooser
          :mode="theme.choice.mode" :selected-style="theme.choice.style" :defaults="theme.defaults" allow-follow
          @update:mode="save($event, theme.choice.style)" @update:style="save(theme.choice.mode, $event)"
        />
      </q-card-section>
    </q-card>
  </q-dialog>
</template>
