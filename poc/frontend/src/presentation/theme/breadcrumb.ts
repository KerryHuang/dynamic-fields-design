import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { MenuItem } from 'src/domain/dynamic/types';

/** 系統管理底下各頁的選單文案鍵；沒列的路徑不顯示麵包屑。 */
const SYSTEM_PAGES: [prefix: string, key: string][] = [
  ['users', 'menu.users'],
  ['groups', 'menu.groups'],
  ['appearance', 'theme.adminMenu'],
  ['demo-data', 'menu.demoData'],
  ['dynamic-modules', 'menu.dynamicModules'],
];

const firstSegments = (path: string) => path.split('/').filter(Boolean);

/**
 * 正式企業風格的麵包屑：純文字、兩層、不可點（設計文件把「重新設計導覽」列為非目標）。
 * 選單還沒載入、沒有權限或不認得的路徑回空陣列，不報錯。
 */
export function breadcrumbFor(path: string, menu: MenuItem[], t: (key: string) => string, locale: string): string[] {
  const [area, page] = firstSegments(path);
  if (area === 'dynamic' && page) {
    const m = menu.find((x) => x.key === page);
    return m ? [m.section || t('menu.custom'), resolveI18n(m.name, locale)] : [];
  }
  if (area === 'reports') return [t('menu.reports'), t('menu.myReports')];
  if (area === 'system') {
    const hit = SYSTEM_PAGES.find(([p]) => p === page);
    return hit ? [t('menu.system'), t(hit[1])] : [];
  }
  return [];
}
