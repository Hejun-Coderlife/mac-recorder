/* 逐帧录「录屏 功能介绍」：本地 http 服务 → 无界面 Chrome 2 倍像素截图 → 缩到 1920×1080 30 帧
   字幕单独截成透明图，用 ffmpeg 叠上去（一份带字幕、一份不带）；旁白 + 轻音效自己混
   用法：node record.mjs              出 ~/Desktop/录屏功能介绍.mp4、-无字幕.mp4、.srt
         node record.mjs --still 秒…  静帧到 /tmp/p7_秒.png（带字幕，检查用） */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir, homedir } from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2), still = args[0] === '--still' ? args.slice(1) : null;
const DESK = join(homedir(), 'Desktop');
const OUT = join(DESK, '录屏功能介绍.mp4'), OUT_NS = join(DESK, '录屏功能介绍-无字幕.mp4'), SRT = join(DESK, '录屏功能介绍.srt');
const W = 1920, H = 1080, FPS = 30, DPR = 2;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const PORT = 9371, HTTP = 9372, WORK = join(tmpdir(), 'p7rec');

const http = spawn('python3', ['-m', 'http.server', String(HTTP), '--bind', '127.0.0.1'], { cwd: HERE, stdio: 'ignore' });
const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
  `--force-device-scale-factor=${DPR}`, `--user-data-dir=${join(tmpdir(), 'p7-chrome')}`, `http://127.0.0.1:${HTTP}/index.html?v=${Date.now()}${still ? '' : '&nosub'}`], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const quit = async () => { http.kill(); chrome.kill('SIGKILL'); await sleep(500); };   // 不等 exit 事件（无界面 Chrome 有时不发，会让 node 提前退出）
let ws;
for (let i = 0; i < 60 && !ws; i++) {
  await sleep(300);
  try { const pg = (await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()).find(t => t.type === 'page'); if (pg) ws = new WebSocket(pg.webSocketDebuggerUrl); } catch {}
}
await new Promise(r => ws.addEventListener('open', r));
let id = 0; const pend = new Map();
ws.addEventListener('message', e => { const m = JSON.parse(e.data);
  if (m.method === 'Runtime.exceptionThrown') console.error('页面出错：', JSON.stringify(m.params.exceptionDetails).slice(0, 700));
  pend.get(m.id)?.(m); pend.delete(m.id); });
const send = (method, params) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = expr => send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }).then(m => {
  if (m.result?.exceptionDetails) throw new Error(JSON.stringify(m.result.exceptionDetails).slice(0, 900));
  return m.result?.result?.value;
});
await send('Runtime.enable', {});
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: DPR, mobile: false });
for (let i = 0; !(await evaluate('!!window.READY && document.fonts.status === "loaded"')); i++) {
  if (i > 150) { console.error('页面没加载出来'); await quit(); process.exit(1); }
  await sleep(200);
}
const END = await evaluate('PROMO.END'), lines = await evaluate('PROMO.lines'), clicks = await evaluate('PROMO.clicks()'), swaps = await evaluate('PROMO.swaps()');
const shot = async (t, file) => {
  await evaluate(`PROMO.frame(${t.toFixed(4)}).then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1)))))`);
  const m = await send('Page.captureScreenshot', { format: file.endsWith('.png') ? 'png' : 'jpeg', quality: file.endsWith('.png') ? undefined : 92 });
  writeFileSync(file, Buffer.from(m.result.data, 'base64'));
};
if (still) {
  for (const s of still) await shot(+s, `/tmp/p7_${s}.png`);
  writeFileSync('/tmp/p7_lines.json', JSON.stringify(lines)); ws.close(); await quit(); console.log('stills done, END', END.toFixed(1)); process.exit(0);
}
mkdirSync(join(WORK, 'f'), { recursive: true }); mkdirSync(join(WORK, 'sub'), { recursive: true });   // 已截好的帧跳过（中断后接着截）
// 1) 画面（不带字幕）
const N = Math.round(END * FPS), t0 = Date.now();
for (let i = 0; i < N; i++) {
  const ff = join(WORK, 'f', `f${String(i).padStart(5, '0')}.jpg`);
  if (i < N - 2 && existsSync(join(WORK, 'f', `f${String(i + 1).padStart(5, '0')}.jpg`))) continue;
  await shot(i / FPS, ff);
  if (i % 90 === 0) process.stdout.write(`\r帧 ${i}/${N}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
}
// 2) 字幕：每句一张透明图（只画字幕条）
await evaluate(`document.body.classList.remove('nosub'); document.body.style.background='transparent'; document.documentElement.style.background='transparent';
  document.querySelectorAll('body > *').forEach(e => { if (e.id !== 'sub') e.style.visibility = 'hidden'; });`);
await send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });
for (const [k, l] of lines.entries()) {
  await evaluate(`(() => { const s = document.getElementById('sub'); s.style.visibility='visible'; s.textContent = ${JSON.stringify(l.text)}; s.style.opacity = 1; })()`);
  const m = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  writeFileSync(join(WORK, 'sub', `s${String(k).padStart(3, '0')}.png`), Buffer.from(m.result.data, 'base64'));
}
ws.close(); await quit();
console.log(`\n截完 ${N} 帧，${((Date.now() - t0) / 1000).toFixed(0)}s`);

// 3) 声音：旁白 + 轻音效（点击 Tink、换画面很轻的提示音；两个音效之间至少隔 0.35 秒，互不重叠）
const SR = 48000;
const readWav = f => { const b = readFileSync(f); let p = 12; while (b.toString('ascii', p, p + 4) !== 'data') p += 8 + b.readUInt32LE(p + 4);
  const n = b.readUInt32LE(p + 4) / 2, a = new Float32Array(n); for (let k = 0; k < n; k++) a[k] = b.readInt16LE(p + 8 + k * 2) / 32768; return a; };
const mix = new Float32Array(Math.ceil((END + 1) * SR));
const put = (at, a, g) => { const s0 = Math.round(at * SR); for (let k = 0; k < a.length && s0 + k < mix.length; k++) mix[s0 + k] += g * a[k]; };
for (const l of lines) put(l.start, readWav(join(HERE, 'voice', l.key + '.wav')), 1);
const TINK = readWav('/tmp/bub/Tink.wav'), ACK = readWav('/tmp/bub/acknowledgment_sent.wav');
const fx = [...clicks.map(t => [t, TINK, .22]), ...swaps.map(t => [t, ACK, .16])].sort((a, b) => a[0] - b[0]);
let last = -9;
for (const [t, a, g] of fx) { if (t - last < .35) continue; put(t, a, g); last = t; }
const wav = join(WORK, 'mix.wav'), wb = Buffer.alloc(44 + mix.length * 2);
wb.write('RIFF', 0); wb.writeUInt32LE(36 + mix.length * 2, 4); wb.write('WAVEfmt ', 8); wb.writeUInt32LE(16, 16); wb.writeUInt16LE(1, 20); wb.writeUInt16LE(1, 22);
wb.writeUInt32LE(SR, 24); wb.writeUInt32LE(SR * 2, 28); wb.writeUInt16LE(2, 32); wb.writeUInt16LE(16, 34); wb.write('data', 36); wb.writeUInt32LE(mix.length * 2, 40);
for (let k = 0; k < mix.length; k++) wb.writeInt16LE(Math.round(Math.max(-1, Math.min(1, mix[k])) * 32767), 44 + k * 2);
writeFileSync(wav, wb);

// 4) 字幕时间表：.srt 和叠图用的 concat 列表（空白 = 全透明图）
const ts = x => { const ms = Math.round(x * 1000), h = Math.floor(ms / 3600000), m = Math.floor(ms / 60000) % 60, s = Math.floor(ms / 1000) % 60;
  return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')},${String(ms % 1000).padStart(3, '0')}`; };
writeFileSync(SRT, lines.map((l, k) => `${k + 1}\n${ts(Math.max(0, l.start - .08))} --> ${ts(l.end + .25)}\n${l.text}\n`).join('\n'));
execFileSync('ffmpeg', ['-v', 'error', '-y', '-f', 'lavfi', '-i', `color=c=black@0.0:s=${W * DPR}x${H * DPR},format=rgba`, '-frames:v', '1', join(WORK, 'sub', 'blank.png')]);
let list = '', cur = 0;
lines.forEach((l, k) => { const a = Math.max(cur, l.start - .08), b = l.end + .25;
  if (a > cur) list += `file 'blank.png'\nduration ${(a - cur).toFixed(3)}\n`;
  list += `file 's${String(k).padStart(3, '0')}.png'\nduration ${(b - a).toFixed(3)}\n`; cur = b; });
list += `file 'blank.png'\nduration ${Math.max(.1, END - cur).toFixed(3)}\nfile 'blank.png'\n`;
writeFileSync(join(WORK, 'sub', 'list.txt'), list);

// 5) 合成：无字幕版 → 叠字幕的版本
execFileSync('ffmpeg', ['-v', 'error', '-y', '-framerate', String(FPS), '-i', join(WORK, 'f', 'f%05d.jpg'), '-i', wav,
  '-filter_complex', `[0:v]scale=${W}:${H}:flags=lanczos,format=yuv420p[v];[1:a]loudnorm=I=-16:TP=-1.5[a]`, '-map', '[v]', '-map', '[a]',
  '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-c:a', 'aac', '-b:a', '192k', '-t', END.toFixed(3), '-movflags', '+faststart', OUT_NS]);
execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', OUT_NS, '-f', 'concat', '-safe', '0', '-i', join(WORK, 'sub', 'list.txt'),
  '-filter_complex', `[1:v]fps=${FPS},scale=${W}:${H}:flags=lanczos,format=rgba[s];[0:v][s]overlay=0:0:shortest=0:eof_action=pass,format=yuv420p[v]`,
  '-map', '[v]', '-map', '0:a', '-c:v', 'libx264', '-preset', 'slow', '-crf', '15', '-c:a', 'copy', '-t', END.toFixed(3), '-movflags', '+faststart', OUT]);
console.log('OK', OUT, OUT_NS, SRT, 'END', END.toFixed(1));
