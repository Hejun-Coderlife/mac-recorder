/* 逐帧录「宣传片样片」：本地起个 http 服务 → 无界面 Chrome 2 倍像素截图（3840×2160）→ 缩到 1920×1080 60 帧 → 配乐
   用法：node record.mjs              出 ~/Desktop/宣传片样片.mp4（配乐）和 宣传片样片-无声.mp4
         node record.mjs --still 秒…  静帧到 /tmp/p4_秒.png（3840 宽，检查清晰度用）
   配乐：Apple Loops「07 Chillwave」120 拍/分（见文末），先由 ffmpeg 转成 /tmp/p4m/*.wav */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir, homedir } from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const still = args[0] === '--still' ? args.slice(1) : null;
const DESK = join(homedir(), 'Desktop');
const OUT = join(DESK, '宣传片样片.mp4'), OUT_SILENT = join(DESK, '宣传片样片-无声.mp4');
const W = 1920, H = 1080, FPS = 60, DPR = 2;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const PORT = 9351, HTTP = 9352;
const DIR = join(tmpdir(), 'recorder-promo4-frames');
const A = '/tmp/p4a';

const http = spawn('python3', ['-m', 'http.server', String(HTTP), '--bind', '127.0.0.1'], { cwd: HERE, stdio: 'ignore' });
const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
  `--force-device-scale-factor=${DPR}`, `--user-data-dir=${join(tmpdir(), 'recorder-promo4-chrome')}`,
  `http://127.0.0.1:${HTTP}/index.html`], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const quit = async () => { http.kill(); await new Promise(r => { chrome.once('exit', r); chrome.kill(); }); };
let ws;
for (let i = 0; i < 60 && !ws; i++) {
  await sleep(300);
  try { const pg = (await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()).find(t => t.type === 'page'); if (pg) ws = new WebSocket(pg.webSocketDebuggerUrl); } catch {}
}
await new Promise(r => ws.addEventListener('open', r));
let id = 0; const pending = new Map();
ws.addEventListener('message', e => { const m = JSON.parse(e.data); pending.get(m.id)?.(m); pending.delete(m.id); });
const send = (method, params) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = expr => send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }).then(m => {
  if (m.result?.exceptionDetails) throw new Error(JSON.stringify(m.result.exceptionDetails).slice(0, 600));
  return m.result?.result?.value;
});
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: DPR, mobile: false });
for (let i = 0; !(await evaluate('!!window.READY && document.fonts.status === "loaded"')); i++) {
  if (i > 150) { console.error('页面没加载出来'); await quit(); process.exit(1); }
  await sleep(200);
}
const END = await evaluate('PROMO.END'), N = Math.round(END * FPS);
const shot = async (t, file) => {
  await evaluate(`PROMO.frame(${t.toFixed(4)}).then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1)))))`);
  const m = await send('Page.captureScreenshot', { format: file.endsWith('.png') ? 'png' : 'jpeg', quality: file.endsWith('.png') ? undefined : 95 });
  writeFileSync(file, Buffer.from(m.result.data, 'base64'));
};
if (still) {
  for (const s of still) await shot(+s, `/tmp/p4_${s}.png`);
  ws.close(); await quit(); console.log('stills done'); process.exit(0);
}
rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true });
const t0 = Date.now();
for (let i = 0; i < N; i++) {
  await shot(i / FPS, join(DIR, `f${String(i).padStart(5, '0')}.jpg`));
  if (i % 60 === 0) process.stdout.write(`\r帧 ${i}/${N}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
}
const clicks = await evaluate('PROMO.clicks()');
ws.close(); await quit();
console.log(`\n截完 ${N} 帧，${((Date.now() - t0) / 1000).toFixed(0)}s`);

execFileSync('ffmpeg', ['-v', 'error', '-y', '-framerate', String(FPS), '-i', join(DIR, 'f%05d.jpg'),
  '-vf', `scale=${W}:${H}:flags=lanczos,format=yuv420p`, '-c:v', 'libx264', '-preset', 'slow', '-crf', '14', '-movflags', '+faststart', OUT_SILENT]);

// 配乐：GarageBand 自带 Apple Loops「07 Chillwave」里 120 拍/分的「Landing in LA」一组（Synth 01 / Bass 01 / Guitar）+「Doubledown Beat 01」
// 一开始就全进（不拖），9 秒出图标时撤掉鼓和贝斯，只留合成器尾音淡出；点击处加一声很轻的「嗒」
const click = join(tmpdir(), 'p4_click.wav');
{ const sr = 48000, n = 1920, b = Buffer.alloc(44 + n * 2);
  b.write('RIFF', 0); b.writeUInt32LE(36 + n * 2, 4); b.write('WAVEfmt ', 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22);
  b.writeUInt32LE(sr, 24); b.writeUInt32LE(sr * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36); b.writeUInt32LE(n * 2, 40);
  for (let k = 0; k < n; k++) { const t = k / sr, env = Math.exp(-t / 0.0045); const v = env * (0.8 * (0.65 * Math.sin(2 * Math.PI * 3100 * t) + 0.35 * Math.sin(2 * Math.PI * 1700 * t)) + 0.2 * (Math.random() * 2 - 1)) * 0.32;
    b.writeInt16LE(Math.round(Math.max(-1, Math.min(1, v)) * 32767), 44 + k * 2); }
  writeFileSync(click, b); }
const ms = s => Math.round(s * 1000);
const M = '/tmp/p4m';
const inputs = ['-i', OUT_SILENT,
  '-stream_loop', '3', '-i', `${M}/Landing_in_LA_Synth_01.wav`,
  '-stream_loop', '3', '-i', `${M}/Landing_in_LA_Bass_01.wav`,
  '-stream_loop', '5', '-i', `${M}/Landing_in_LA_Guitar.wav`,
  '-stream_loop', '3', '-i', `${M}/Doubledown_Beat_01.wav`];
clicks.forEach(() => inputs.push('-i', click));
const DROP = 9.0;
const parts = [
  `[1:a]atrim=0:${END},volume=0.85[syn]`,
  `[2:a]atrim=0:${DROP + 0.05},afade=t=out:st=${DROP - 0.05}:d=0.1,volume=0.8[bas]`,
  `[3:a]atrim=0:${DROP + 0.05},afade=t=out:st=${DROP - 0.05}:d=0.1,volume=0.45[gtr]`,
  `[4:a]atrim=0:${DROP + 0.05},afade=t=out:st=${DROP - 0.05}:d=0.1,volume=0.7[bt]`,
  ...clicks.map((c, i) => `[${5 + i}:a]aformat=channel_layouts=stereo,adelay=${ms(c)}|${ms(c)},volume=0.25[c${i}]`),
  `[syn][bas][gtr][bt]${clicks.map((_, i) => `[c${i}]`).join('')}amix=inputs=${4 + clicks.length}:normalize=0,afade=t=in:st=0:d=0.08,afade=t=out:st=${(END - 0.8).toFixed(2)}:d=0.8,loudnorm=I=-15:TP=-1.5[a]`,
];
execFileSync('ffmpeg', ['-v', 'error', '-y', ...inputs, '-filter_complex', parts.join(';'), '-map', '0:v', '-map', '[a]',
  '-c:v', 'copy', '-c:a', 'aac', '-b:a', '256k', '-t', String(END), '-movflags', '+faststart', OUT]);
console.log('OK', OUT, OUT_SILENT);
