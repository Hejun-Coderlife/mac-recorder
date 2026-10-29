/* 逐帧录制「录屏宣传片」（第三版）：无界面 Chrome 打开 index.html?rec=1，按 30 帧/秒调 PROMO.frame(t) 截图
   出两样：~/Desktop/录屏宣传片.mp4（开头一句旁白 + 很轻的点击声）、录屏宣传片-无声.mp4（只有画面）
   用法：node record.mjs            node record.mjs --still 秒数 [秒数…]（静帧到 /tmp/p3_秒数.png）
   素材：frames/<剪辑>/ 由 README 里的 ffmpeg 命令从 /tmp/rd/states/*.mp4 抽出；旁白 voice/a.wav（uvx edge-tts 云希） */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir, homedir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const still = args[0] === '--still' ? args.slice(1) : null;
const DESK = join(homedir(), 'Desktop');
const OUT = join(DESK, '录屏宣传片.mp4'), OUT_SILENT = join(DESK, '录屏宣传片-无声.mp4');
const W = 1920, H = 1080, FPS = 30;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const PORT = 9341;
const DIR = join(tmpdir(), 'recorder-promo3-frames');

const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
  '--force-device-scale-factor=1', '--allow-file-access-from-files', `--user-data-dir=${join(tmpdir(), 'recorder-promo3-chrome')}`,
  pathToFileURL(join(HERE, 'index.html')).href + '?rec=1'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const quit = () => new Promise(r => { chrome.once('exit', r); chrome.kill(); });
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
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });
for (let i = 0; !(await evaluate('!!window.PROMO && document.readyState === "complete" && document.fonts.status === "loaded"')); i++) {
  if (i > 100) { console.error('页面没加载出来'); await quit(); process.exit(1); }
  await sleep(200);
}
const END = await evaluate('PROMO.END'), N = Math.round(END * FPS);
const shot = async (t, file) => {
  await evaluate(`PROMO.frame(${t.toFixed(4)}).then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1)))))`);
  const m = await send('Page.captureScreenshot', { format: file.endsWith('.png') ? 'png' : 'jpeg', quality: file.endsWith('.png') ? undefined : 94 });
  writeFileSync(file, Buffer.from(m.result.data, 'base64'));
};
if (still) {
  for (const s of still) await shot(+s, `/tmp/p3_${s}.png`);
  ws.close(); await quit(); console.log('stills done'); process.exit(0);
}
rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true });
const t0 = Date.now();
for (let i = 0; i < N; i++) {
  await shot(i / FPS, join(DIR, `f${String(i).padStart(5, '0')}.jpg`));
  if (i % 30 === 0) process.stdout.write(`\r帧 ${i}/${N}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
}
const clicks = await evaluate('PROMO.clicks()');
ws.close(); await quit();

// 很轻的「嗒」（和软件里「轻点」同一公式），只放在演示里的点击处
const SR = 48000, len = Math.ceil(END * SR), sfx = new Float32Array(len);
for (const t of clicks) {
  const s0 = Math.round(t * SR);
  for (let k = 0; k < 1920 && s0 + k < len; k++) {
    const x = k / SR, env = Math.exp(-x / 0.0045);
    sfx[s0 + k] += 0.3 * env * (0.8 * (0.65 * Math.sin(2 * Math.PI * 3100 * x) + 0.35 * Math.sin(2 * Math.PI * 1700 * x)) + 0.2 * (Math.random() * 2 - 1)) * 0.32;
  }
}
const wav = join(HERE, 'voice', 'sfx.wav'), buf = Buffer.alloc(44 + len * 2);
buf.write('RIFF', 0); buf.writeUInt32LE(36 + len * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(len * 2, 40);
for (let i = 0; i < len; i++) buf.writeInt16LE(Math.round(Math.max(-1, Math.min(1, sfx[i])) * 32767), 44 + i * 2);
writeFileSync(wav, buf);

console.log('\n合成视频…');
const video = ['-framerate', String(FPS), '-i', join(DIR, 'f%05d.jpg')];
const enc = ['-c:v', 'libx264', '-preset', 'slow', '-crf', '16', '-pix_fmt', 'yuv420p', '-color_primaries', 'bt709', '-color_trc', 'bt709', '-colorspace', 'bt709', '-movflags', '+faststart'];
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...video, ...enc, OUT_SILENT]);
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...video, '-i', join(HERE, 'voice', 'a.wav'), '-i', wav,
  '-filter_complex', '[1:a]adelay=500|500,aresample=48000,pan=stereo|c0=c0|c1=c0[v];[2:a]pan=stereo|c0=c0|c1=c0[s];[v][s]amix=inputs=2:normalize=0,apad[a]',
  '-map', '0:v', '-map', '[a]', '-shortest', ...enc, '-c:a', 'aac', '-b:a', '192k', OUT]);
console.log('OK', OUT, OUT_SILENT);
