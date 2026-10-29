/* 逐帧录制「录屏介绍视频」：无界面 Chrome 打开 index.html?rec=1
   ① 先问页面每一帧要用到哪些成片画面（PROMO.needs），从 /tmp/rd/states/<名>.mp4 抽成 frames/<名>/<帧号>.jpg
   ② 按 30 帧/秒调 PROMO.frame(t)（等图片解码完）截图
   ③ 出三样：~/Desktop/录屏介绍视频.mp4（画面 + 字幕 + 旁白 + 轻音效）、录屏介绍视频-无声.mp4（只有画面）、录屏介绍视频.srt
   用法：node record.mjs            node record.mjs --still 秒数 [秒数…]（静帧到 /tmp/rp_秒数.png）
   旁白先跑 node voice.mjs；不加背景音乐（用户自己加） */
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir, homedir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const still = args[0] === '--still' ? args.slice(1) : null;
const DESK = join(homedir(), 'Desktop');
const OUT = join(DESK, '录屏介绍视频.mp4'), OUT_SILENT = join(DESK, '录屏介绍视频-无声.mp4'), OUT_SRT = join(DESK, '录屏介绍视频.srt');
const STATES = '/tmp/rd/states';
const W = 1920, H = 1080, FPS = 30;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const PORT = 9338;
const DIR = join(tmpdir(), 'recorder-promo2-frames');

const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
  '--force-device-scale-factor=1', '--allow-file-access-from-files', `--user-data-dir=${join(tmpdir(), 'recorder-promo2-chrome')}`,
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
for (let i = 0; !(await evaluate('!!window.PROMO && document.readyState === "complete"')); i++) {
  if (i > 100) { console.error('页面没加载出来'); await quit(); process.exit(1); }
  await sleep(200);
}
const END = await evaluate('PROMO.END'), N = Math.round(END * FPS);
const times = still ? still.map(Number) : [...Array(N).keys()].map(i => i / FPS);

// ① 要用的成片帧：每段成片按用到的最小、最大帧号整段抽出来（已经有的跳过）
const need = await evaluate(`(() => { const m = {}; for (const t of ${JSON.stringify(times)}) for (const [c, i] of PROMO.needs(t)) (m[c] ??= new Set()).add(i); return Object.fromEntries(Object.entries(m).map(([c, s]) => [c, [...s]])); })()`);
for (const [clip, idx] of Object.entries(need)) {
  const lo = Math.max(0, Math.min(...idx) - 2), hi = Math.max(...idx) + 2, dir = join(HERE, 'frames', clip);
  if (idx.every(i => existsSync(join(dir, `${String(i).padStart(5, '0')}.jpg`)))) continue;
  mkdirSync(dir, { recursive: true });
  console.log(`抽帧 ${clip}: ${lo}–${hi}`);
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-ss', (lo / FPS).toFixed(4), '-i', join(STATES, `${clip}.mp4`),
    '-vf', 'fps=30,scale=-2:1014:flags=lanczos', '-frames:v', String(hi - lo + 1), '-start_number', String(lo), '-q:v', '3', join(dir, '%05d.jpg')]);
}

// ② 逐帧截图
const shot = async (t, file) => {
  await evaluate(`PROMO.frame(${t.toFixed(4)}).then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1)))))`);
  const m = await send('Page.captureScreenshot', { format: file.endsWith('.png') ? 'png' : 'jpeg', quality: file.endsWith('.png') ? undefined : 94 });
  writeFileSync(file, Buffer.from(m.result.data, 'base64'));
};
if (still) {
  for (const s of still) await shot(+s, `/tmp/rp_${s}.png`);
  ws.close(); await quit(); console.log('stills done'); process.exit(0);
}
rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true });
const t0 = Date.now();
for (let i = 0; i < N; i++) {
  await shot(i / FPS, join(DIR, `f${String(i).padStart(5, '0')}.jpg`));
  if (i % 30 === 0) process.stdout.write(`\r帧 ${i}/${N}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
}
const clicks = await evaluate('PROMO.clicks()'), demo = await evaluate('PROMO.demoClicks()');
const voice = (await evaluate('window.VOICE || []')).map(v => ({ ...v, at: v.at }));
const scenes = await evaluate('PROMO.scenes');
ws.close(); await quit();
for (const v of voice) v.t = scenes[v.scene].start + v.at;

// ③ 音效：界面点击 = 很轻的「嗒」；P4 里演示的点击音效 = 成片里那种轻点声，稍大一点
const SR = 48000, len = Math.ceil(END * SR), sfx = new Float32Array(len);
const tap = (t, gain) => {
  const s0 = Math.round(t * SR);
  for (let k = 0; k < 1920 && s0 + k < len; k++) {
    const x = k / SR, env = Math.exp(-x / 0.0045);
    sfx[s0 + k] += gain * env * (0.8 * (0.65 * Math.sin(2 * Math.PI * 3100 * x) + 0.35 * Math.sin(2 * Math.PI * 1700 * x)) + 0.2 * (Math.random() * 2 - 1)) * 0.32;
  }
};
for (const t of clicks) tap(t, 0.35);
for (const t of demo) tap(t, 1.0);
const wav = join(DIR, 'sfx.wav'), buf = Buffer.alloc(44 + len * 2);
buf.write('RIFF', 0); buf.writeUInt32LE(36 + len * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(len * 2, 40);
for (let i = 0; i < len; i++) buf.writeInt16LE(Math.round(Math.max(-1, Math.min(1, sfx[i])) * 32767), 44 + i * 2);
writeFileSync(wav, buf);

// 字幕：每句按标点拆成短句，一次一行；拆开的位置对准旁白里真实的停顿（照 demo/make.mjs）
function captions(v) {
  const parts = v.text.split(/[，。！？；：]/).map(x => x.trim()).filter(Boolean);
  const log = spawnSync('ffmpeg', ['-hide_banner', '-i', join(HERE, 'voice', `${v.key}.wav`), '-af', 'silencedetect=n=-35dB:d=0.1', '-f', 'null', '-']).stderr.toString();
  const st = [...log.matchAll(/silence_start: ([\d.]+)/g)].map(m => +m[1]), en = [...log.matchAll(/silence_end: ([\d.]+)/g)].map(m => +m[1]);
  const pauses = st.map((s, i) => (s + (en[i] ?? s)) / 2);
  const total = parts.reduce((a, p) => a + p.length, 0), cuts = [0];
  let acc = 0;
  for (let k = 0; k < parts.length - 1; k++) {
    acc += parts[k].length;
    const guess = v.dur * acc / total;
    const near = pauses.filter(p => p > cuts.at(-1) + 0.3 && Math.abs(p - guess) < 0.8).sort((a, b) => Math.abs(a - guess) - Math.abs(b - guess))[0];
    cuts.push(near ?? guess);
  }
  cuts.push(v.dur + 0.3);
  return parts.map((text, k) => ({ text: text.replace(/[「」]/g, m => m), a: v.t + cuts[k], b: v.t + cuts[k + 1] }));
}
const subs = voice.flatMap(captions);
const tA = x => { const cs = Math.round(x * 100); return `${Math.floor(cs / 360000)}:${String(Math.floor(cs / 6000) % 60).padStart(2, '0')}:${String(Math.floor(cs / 100) % 60).padStart(2, '0')}.${String(cs % 100).padStart(2, '0')}`; };
const tS = x => { const ms = Math.round(x * 1000); return `${String(Math.floor(ms / 3600000)).padStart(2, '0')}:${String(Math.floor(ms / 60000) % 60).padStart(2, '0')}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')},${String(ms % 1000).padStart(3, '0')}`; };
writeFileSync(OUT_SRT, subs.map((c, i) => `${i + 1}\n${tS(c.a)} --> ${tS(c.b)}\n${c.text}\n`).join('\n'));
const fs = 46, ass = join(DIR, 'subs.ass');
writeFileSync(ass, `[Script Info]
ScriptType: v4.00+
PlayResX: ${W}
PlayResY: ${H}
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,PingFang SC,${fs},&H00FFFFFF,&H00FFFFFF,&H70000000,&H70000000,1,0,0,0,100,100,2,0,3,14,0,2,0,0,28,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
${subs.map(c => `Dialogue: 0,${tA(c.a)},${tA(c.b)},Default,,0,0,0,,{\\fad(80,80)}${c.text}`).join('\n')}
`);
subs.forEach(c => console.log(`  ${c.a.toFixed(2)}–${c.b.toFixed(2)}  ${c.text}`));

console.log('\n合成视频…');
// 无声版：只有画面
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', String(FPS), '-i', join(DIR, 'f%05d.jpg'),
  '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '16', '-preset', 'slow', '-movflags', '+faststart', OUT_SILENT], { stdio: 'inherit' });
// 正式版：画面 + 字幕 + 旁白 + 音效
const vin = voice.flatMap(v => ['-i', join(HERE, 'voice', `${v.key}.wav`)]);
const filter = voice.map((v, i) => `[${2 + i}]volume=1.5,adelay=${Math.round(v.t * 1000)}:all=1[v${i}]`).join(';') + ';' +
  `[1]aformat=channel_layouts=stereo[fx];[fx]${voice.map((_, i) => `[v${i}]`).join('')}amix=inputs=${voice.length + 1}:normalize=0:duration=first,alimiter=limit=0.9:level=false[a]`;
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', OUT_SILENT, '-i', wav, ...vin, '-filter_complex', filter,
  '-map', '0:v', '-map', '[a]', '-vf', `ass=${ass}`, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '16', '-preset', 'slow',
  '-c:a', 'aac', '-b:a', '192k', '-movflags', '+faststart', OUT], { stdio: 'inherit' });
rmSync(DIR, { recursive: true, force: true });
console.log('完成：', OUT, '\n      ', OUT_SILENT, '\n      ', OUT_SRT, `\n时长 ${END.toFixed(1)}s`);
