// 產生虛構的設計示意圖（全部虛構，無真實客戶或產品）
import { chromium } from 'playwright-core'
const b = await chromium.launch({ channel: 'chrome' })
const p = await b.newPage({ viewport: { width: 1200, height: 800 } })
const draw = (title, version, extra) => `<html><body style="margin:0;background:#fff;font-family:'PingFang TC',sans-serif">
<svg width="1200" height="800" viewBox="0 0 1200 800">
<rect x="20" y="20" width="1160" height="760" fill="none" stroke="#333" stroke-width="2"/>
<text x="40" y="70" font-size="32" font-weight="600">${title}</text><text x="40" y="110" font-size="22" fill="#666">${version}｜示意圖（虛構）</text>
<rect x="300" y="200" width="600" height="380" rx="24" fill="#eef3f7" stroke="#2b5d7d" stroke-width="4"/>
<rect x="360" y="260" width="200" height="120" rx="8" fill="#fff" stroke="#2b5d7d" stroke-width="3"/>
<circle cx="${extra.led}" cy="300" r="18" fill="#3c9" stroke="#2b5d7d" stroke-width="3"/>
${extra.slots ? [0,1,2,3].map(i=>`<rect x="${620+i*50}" y="460" width="20" height="90" rx="6" fill="#2b5d7d"/>`).join('') : ''}
<line x1="300" y1="620" x2="900" y2="620" stroke="#999" stroke-width="2"/><text x="560" y="650" font-size="20" fill="#666">240 mm</text>
${extra.mark ? `<circle cx="830" cy="300" r="60" fill="none" stroke="#d33" stroke-width="5"/><text x="760" y="390" font-size="24" fill="#d33">指示燈移到右上</text><rect x="600" y="440" width="230" height="130" fill="none" stroke="#d33" stroke-width="5" stroke-dasharray="12 8"/><text x="610" y="600" font-size="24" fill="#d33">側面加散熱槽</text>` : ''}
</svg></body></html>`
const files = [
  ['外殼設計-v1.png', draw('客製控制盒外殼', '第一版', { led: 460 })],
  ['客戶標註-v1.png', draw('客製控制盒外殼', '第一版（客戶標註）', { led: 460, mark: true })],
  ['外殼設計-v2.png', draw('客製控制盒外殼', '第二版', { led: 830, slots: true })],
]
for (const [n, h] of files) { await p.setContent(h); await p.screenshot({ path: 'files/' + n }) }
await b.close(); console.log('ok')
