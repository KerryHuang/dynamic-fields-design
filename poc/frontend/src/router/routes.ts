import type { RouteRecordRaw } from 'vue-router';

export const routes: RouteRecordRaw[] = [
  { path: '/login', component: () => import('src/pages/auth/LoginPage.vue'), meta: { public: true } },
  { path: '/service-unavailable', component: () => import('src/pages/auth/ServiceUnavailablePage.vue'), meta: { public: true } },
  { path: '/change-password', component: () => import('src/pages/auth/ChangePasswordPage.vue') },
  {
    path: '/',
    component: () => import('src/layouts/MainLayout.vue'),
    children: [
      { path: '', component: () => import('src/pages/HomePage.vue') },
      { path: 'dynamic/:moduleKey', component: () => import('src/pages/dynamic/ListPage.vue') },
      { path: 'dynamic/:moduleKey/new', component: () => import('src/pages/dynamic/FormPage.vue') },
      { path: 'dynamic/:moduleKey/:id(\\d+)', component: () => import('src/pages/dynamic/DetailPage.vue') },
      { path: 'dynamic/:moduleKey/:id(\\d+)/edit', component: () => import('src/pages/dynamic/FormPage.vue') },
      { path: 'system/dynamic-modules', component: () => import('src/pages/system/dynamic-modules/ModuleListPage.vue') },
      { path: 'system/dynamic-modules/:moduleKey', component: () => import('src/pages/system/dynamic-modules/ModuleEditorPage.vue') },
      { path: 'system/demo-data', component: () => import('src/pages/system/DemoDataPage.vue') },
    ],
  },
];
