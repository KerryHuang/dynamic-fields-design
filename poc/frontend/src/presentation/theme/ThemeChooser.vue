<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { FACTORY_THEME, THEME_MODES, THEME_STYLES, parseMode, parseStyle } from 'src/domain/theme/theme-model';
import type { ThemeDefaults, ThemeMode, ThemeStyle } from 'src/domain/theme/theme-model';
import { useThemeStore } from 'src/stores/use-theme-store';

const FOLLOW = 'follow';

const props = defineProps<{
  mode: ThemeMode | null;
  style: ThemeStyle | null;
  /** 個人設定可選「跟隨全站預設」；全站預設本身不行。 */
  allowFollow: boolean;
  disabled?: boolean;
  defaults?: ThemeDefaults;
}>();
const emit = defineEmits<{ 'update:mode': [ThemeMode | null]; 'update:style': [ThemeStyle | null] }>();

const { t } = useI18n();
const theme = useThemeStore();
const current = computed(() => props.defaults ?? FACTORY_THEME);

const modeOptions = computed(() => [
  ...(props.allowFollow ? [{ value: FOLLOW, label: t('theme.follow', { value: t(`theme.modes.${current.value.mode}`) }) }] : []),
  ...THEME_MODES.map((m) => ({ value: m, label: t(`theme.modes.${m}`) })),
]);
const modeValue = computed(() => props.mode ?? (props.allowFollow ? FOLLOW : current.value.mode));
const styleValue = computed(() => props.style ?? (props.allowFollow ? FOLLOW : current.value.style));

function pickMode(v: string) {
  emit('update:mode', v === FOLLOW ? null : parseMode(v));
}

function pickStyle(v: string) {
  emit('update:style', v === FOLLOW ? null : parseStyle(v));
}
</script>

<template>
  <div class="column q-gutter-y-md">
    <div>
      <div class="text-subtitle2 q-mb-xs">{{ t('theme.mode') }}</div>
      <q-btn-toggle
        :model-value="modeValue" :options="modeOptions" :disable="disabled" no-caps unelevated dense
        toggle-color="primary" data-testid="mode-toggle"
        @update:model-value="pickMode"
      />
    </div>

    <div>
      <div class="text-subtitle2 q-mb-xs">{{ t('theme.style') }}</div>
      <div class="column q-gutter-y-sm">
        <q-radio
          v-if="allowFollow" :model-value="styleValue" :val="FOLLOW" :disable="disabled" data-testid="style-follow"
          :label="t('theme.follow', { value: t(`theme.styles.${current.style}.name`) })" @update:model-value="pickStyle"
        />
        <q-radio
          v-for="s in THEME_STYLES" :key="s" :model-value="styleValue" :val="s" :disable="disabled"
          :data-testid="`style-${s}`" @update:model-value="pickStyle"
        >
          <div class="row items-center no-wrap q-ml-sm q-gutter-x-md">
            <div :data-style="s" :data-mode="theme.resolvedMode" class="theme-sample">
              <div class="theme-sample__bar" />
              <div class="theme-sample__text">{{ t('theme.sampleText') }}</div>
              <div class="theme-sample__chip" />
            </div>
            <div>
              <div class="text-weight-medium">{{ t(`theme.styles.${s}.name`) }}</div>
              <div class="text-caption text-muted">{{ t(`theme.styles.${s}.desc`) }}</div>
            </div>
          </div>
        </q-radio>
      </div>
    </div>
  </div>
</template>
