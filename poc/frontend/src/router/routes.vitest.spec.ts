import { describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import { routes } from './routes';

const loaderOf = (path: string) => {
  const router = createRouter({ history: createMemoryHistory(), routes });
  const leaf = router.resolve(path).matched.at(-1)!;
  return { leaf, source: String(leaf.components?.default) };
};

describe('report routes', () => {
  it('/reports/new and /reports/:id/edit open the designer, /reports/:id the viewer', () => {
    expect(loaderOf('/reports/new').source).toContain('ReportDesignerPage');
    expect(loaderOf('/reports/7/edit').source).toContain('ReportDesignerPage');
    expect(loaderOf('/reports/7').source).toContain('ReportViewPage');
    expect(loaderOf('/reports/7/edit').leaf.path).toContain(':id');
  });
});

describe('appearance route', () => {
  it('/system/appearance 開啟 AppearancePage', () => {
    expect(loaderOf('/system/appearance').source).toContain('AppearancePage');
  });
});

describe('policies route', () => {
  it('/system/policies 開啟 PoliciesPage', () => {
    expect(loaderOf('/system/policies').source).toContain('PoliciesPage');
  });
});

describe('conversions route', () => {
  it('/system/conversions opens ConversionsPage', () => {
    expect(loaderOf('/system/conversions').source).toContain('ConversionsPage');
  });
});

describe('tasks route', () => {
  it('/tasks opens MyTasksPage', () => {
    expect(loaderOf('/tasks').source).toContain('MyTasksPage');
  });
});

describe('approvals route', () => {
  it('/approvals resolves to the approval center', () => {
    expect(loaderOf('/approvals').source).toContain('ApprovalCenterPage');
  });
});
