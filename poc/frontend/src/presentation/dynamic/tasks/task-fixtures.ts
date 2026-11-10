/** Wire-shaped task payloads (null properties are left out, like the server does). */
export const taskWire = (o: Record<string, unknown> = {}) => ({
  id: 5, title: '回電給客戶', status: 'todo', assigneeUserId: 'sales01', assigneeName: '業務一', assigneeActive: true,
  dueDate: '2026-12-01', overdue: false,
  record: { accessible: true, moduleKey: 'crm_customer', recordId: 9, title: '甲公司', deleted: false },
  fromRule: false, canEdit: true, ...o,
});

export const taskDetailWire = (o: Record<string, unknown> = {}) => ({
  ...taskWire(), description: '確認報價', createdBy: 'admin', createdByName: '管理員', createdTime: '2026-11-01T02:00:00Z',
  ruleDeleted: false, canComment: true, canDelete: true, frozen: false, ...o,
});

export const commentWire = (o: Record<string, unknown> = {}) => ({
  id: 1, kind: 'comment', authorUserId: 'sales01', authorName: '業務一', body: '好的', withdrawn: false,
  createdTime: '2026-11-02T03:00:00Z', mentions: [], attachments: [], canWithdraw: false, ...o,
});

export const USERS = [
  { userId: 'admin', displayName: '管理員' },
  { userId: 'sales01', displayName: '業務一' },
  { userId: 'sales02', displayName: '業務二' },
];
