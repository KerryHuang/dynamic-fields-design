import { cardWire } from '../board/board-fixtures';

/** 預設甘特圖的設定（伺服器回應的形狀）。 */
export const ganttConfigWire = (o: Record<string, unknown> = {}) => ({
  version: 1, groupBy: 'parent', scope: 'assigned', filters: { statuses: ['todo', 'inProgress', 'done'] }, scale: 'week', ...o,
});

/**
 * 甘特查詢回應（null 屬性不輸出，同伺服器）：今天 2026-11-18（週三），週起始日星期一，預設週刻度 13 週。
 * 列：父任務 5「規劃活動內容」與兩個子任務、一個只有到期日的里程碑、一個逾期、一個已完成；未排程兩筆（一筆只有開始日）。
 */
export const ganttWire = (o: Record<string, unknown> = {}) => ({
  today: '2026-11-18', weekStart: 'monday', window: { from: '2026-11-16', to: '2027-02-14' }, scopeAvailable: true, config: ganttConfigWire(),
  rows: [
    cardWire({ id: 5, title: '規劃活動內容', status: 'inProgress', startDate: '2026-11-16', dueDate: '2026-12-01', level: 'parent', progress: { done: 1, cancelled: 1, total: 4 } }),
    cardWire({ id: 6, title: '確認展位與預算', status: 'done', startDate: '2026-11-16', dueDate: '2026-11-25', level: 'child', parentTaskId: 5, parentTitle: '規劃活動內容' }),
    cardWire({ id: 7, title: '整理邀請客戶名單', status: 'inProgress', startDate: '2026-11-21', dueDate: '2026-12-02', level: 'child', parentTaskId: 5, parentTitle: '規劃活動內容' }),
    cardWire({ id: 8, title: '寄合約', dueDate: '2026-11-20' }),
    cardWire({ id: 9, title: '回覆報價', startDate: '2026-11-10', dueDate: '2026-11-17', overdue: true, assigneeUserId: 'sales02', assigneeName: '業務二' }),
  ],
  total: 5, truncated: false, parents: [], spans: [{ parentId: 5, from: '2026-11-16', to: '2026-12-09' }],
  unscheduled: [cardWire({ id: 20, title: '整理名片', dueDate: undefined, startDate: '2026-11-20' }), cardWire({ id: 21, title: '想想看', dueDate: undefined })],
  unscheduledTotal: 2, unscheduledTruncated: false,
  ...o,
});
