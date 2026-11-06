<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { readRecent, RECENT_SHOWN } from 'src/application/dynamic/recent-modules';
import { groupMenu, systemLinks } from 'src/domain/dynamic/menu-nav';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const identity = useIdentityStore();
const me = useMe();
const menu = useMenu();

const groups = computed(() => groupMenu(menu.data.value ?? []));
const links = computed(() => systemLinks(me.data.value));
// 只列選單裡還在的模組（已停用或被收回權限的不顯示）；storage 不可用時是空陣列，整區隱藏
const recent = computed(() => {
  const byKey = new Map((menu.data.value ?? []).map((m) => [m.key, m]));
  return readRecent(identity.userId).flatMap((k) => byKey.get(k) ?? []).slice(0, RECENT_SHOWN);
});
</script>

<template>
  <q-page padding>
    <div class="q-mb-lg" data-testid="home-welcome">
      <div class="text-h5">Formwright</div>
      <div v-if="me.data.value" class="text-muted">{{ t('home.welcome', { name: me.data.value.displayName }) }}</div>
    </div>

    <ForbiddenNotice v-if="menu.isError.value" />

    <template v-else-if="menu.isSuccess.value">
      <section v-if="recent.length" class="q-mb-lg" data-testid="home-recent">
        <div class="text-subtitle1 q-mb-sm">{{ t('home.recent') }}</div>
        <div class="row q-gutter-sm">
          <q-btn
            v-for="m in recent"
            :key="m.key"
            outline
            no-caps
            :icon="m.icon || 'apps'"
            :label="resolveI18n(m.name, locale)"
            :to="`/dynamic/${m.key}`"
            data-testid="home-recent-item"
          />
        </div>
      </section>

      <section class="q-mb-lg">
        <div class="text-subtitle1 q-mb-sm">{{ t('home.apps') }}</div>
        <div v-if="!groups.length" class="text-muted" data-testid="home-empty">{{ t('home.noModules') }}</div>
        <div v-for="g in groups" :key="g.title ?? ''" class="q-mb-md" data-testid="home-app-group">
          <div class="text-caption text-muted q-mb-xs" data-testid="home-group-title">{{ g.title ?? t('menu.custom') }}</div>
          <div class="row q-col-gutter-sm">
            <div v-for="m in g.items" :key="m.key" class="col-12 col-sm-6 col-md-4 col-lg-3">
              <q-card flat bordered>
                <q-item clickable :to="`/dynamic/${m.key}`" data-testid="home-app-card">
                  <q-item-section avatar><q-icon :name="m.icon || 'apps'" /></q-item-section>
                  <q-item-section class="home-card-name">{{ resolveI18n(m.name, locale) }}</q-item-section>
                </q-item>
              </q-card>
            </div>
          </div>
        </div>
      </section>
    </template>

    <section class="q-mb-lg">
      <div class="text-subtitle1 q-mb-sm">{{ t('home.reports') }}</div>
      <div class="row q-col-gutter-sm">
        <div class="col-12 col-sm-6 col-md-4 col-lg-3">
          <q-card flat bordered>
            <q-item clickable to="/reports" data-testid="home-reports">
              <q-item-section avatar><q-icon name="bar_chart" /></q-item-section>
              <q-item-section>{{ t('menu.myReports') }}</q-item-section>
            </q-item>
          </q-card>
        </div>
      </div>
    </section>

    <section v-if="links.length" class="q-mb-lg" data-testid="home-system">
      <div class="text-subtitle1 q-mb-sm">{{ t('home.system') }}</div>
      <div class="row q-col-gutter-sm">
        <div v-for="l in links" :key="l.to" class="col-12 col-sm-6 col-md-4 col-lg-3">
          <q-card flat bordered>
            <q-item clickable :to="l.to" data-testid="home-system-link">
              <q-item-section avatar><q-icon :name="l.icon" /></q-item-section>
              <q-item-section>{{ t(l.label) }}</q-item-section>
            </q-item>
          </q-card>
        </div>
      </div>
    </section>
  </q-page>
</template>
