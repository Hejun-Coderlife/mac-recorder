/* 抓一张真实网站截图给「框选区域」那段用：无界面 Chrome 打开网页，截首屏，同时找出首屏里最大的那张图（框要对准它）
   输出 site.png + site.js（window.SITE = {url, w, h, target:{x,y,w,h}}；用 script 标签读，file:// 下不能 fetch）
   用法：[PROXY=http://127.0.0.1:7897] node promo/grab_site.mjs [网址] [框对准的元素选择器]                                                                     */
import { spawn } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const URL_ = process.argv[2] || 'https://www.apple.com.cn/';
const SEL = process.argv[3];                 // 可选：框要对准的元素（CSS 选择器），不给就找首屏最大的图
const PROXY = process.env.PROXY;             // 可选：代理，如 http://127.0.0.1:7897
const W = 1440, H = 811;   // 和演示屏幕里浏览器内容区同比例（920×518）
const PORT = 9335;
const chrome = spawn('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', ['--headless=new', `--remote-debugging-port=${PORT}`,
  '--hide-scrollbars', '--force-device-scale-factor=1', '--lang=zh-CN', '--autoplay-policy=no-user-gesture-required', ...(PROXY ? [`--proxy-server=${PROXY}`] : []), `--user-data-dir=${join(tmpdir(), 'promo-site-chrome')}`, 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws;
for (let i = 0; i < 60 && !ws; i++) {
  await sleep(300);
  try { const pg = (await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()).find(t => t.type === 'page'); if (pg) ws = new WebSocket(pg.webSocketDebuggerUrl); } catch {}
}
await new Promise(r => ws.addEventListener('open', r));
let id = 0; const pending = new Map();
ws.addEventListener('message', e => { const m = JSON.parse(e.data); pending.get(m.id)?.(m); pending.delete(m.id); });
const send = (method, params) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = expr => send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }).then(m => m.result?.result?.value);
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 2, mobile: false });
await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });
await send('Page.enable');
await send('Page.navigate', { url: URL_ });
for (let i = 0; i < 60 && (await evaluate('document.readyState')) !== 'complete'; i++) await sleep(500);
await sleep(4000);   // 等首屏大图、动画加载完
const target = SEL ? await evaluate(`(() => { const r = document.querySelector(${JSON.stringify(SEL)})?.getBoundingClientRect(); return r && { x: r.left, y: r.top, w: r.width, h: r.height }; })()`) : await evaluate(`(() => {
  let best = null, area = 0;
  for (const el of document.querySelectorAll('img, video, picture, [style*="background-image"]')) {
    const r = el.getBoundingClientRect();
    const x = Math.max(0, r.left), y = Math.max(0, r.top), w = Math.min(${W}, r.right) - x, h = Math.min(${H}, r.bottom) - y;
    if (w > 200 && h > 120 && w * h > area && w < ${W} * .95) { area = w * h; best = { x, y, w, h }; }
  }
  return best;
})()`);
const shot = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(join(HERE, 'site.png'), Buffer.from(shot.result.data, 'base64'));
writeFileSync(join(HERE, 'site.js'), `window.SITE = ${JSON.stringify({ url: new URL(URL_).host.replace(/^www\./, ''), w: W, h: H, target })};\n`);
console.log('target', target);
ws.close(); chrome.kill();
