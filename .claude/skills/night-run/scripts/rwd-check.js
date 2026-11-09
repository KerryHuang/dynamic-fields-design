// 在 Demo 頁面內用 evaluate_script 執行：依序切到每個路徑，回報有沒有橫向捲動與頁面標題。
// 先用 emulate 設好版型，再把整個函式貼進 evaluate_script 的 function，第一個參數改成要檢查的路徑。
// 版型：390x844x3,mobile,touch／820x1180x2,mobile,touch／1366x768x1／1920x1080x1。
async (paths = ['/dynamic/mes-mold']) => {
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const out = [];
  for (const p of paths) {
    history.pushState({}, '', p);
    dispatchEvent(new PopStateEvent('popstate'));
    await wait(3000);
    const d = document.documentElement;
    out.push({
      path: p,
      overflow: d.scrollWidth > d.clientWidth,
      title: document.querySelector('main h1, main .text-h5, main .text-h6')?.textContent?.trim() ?? null,
      alerts: [...document.querySelectorAll('.q-notification, [role=alert]')].map((e) => e.textContent.trim()).slice(0, 2),
    });
  }
  return out;
}
