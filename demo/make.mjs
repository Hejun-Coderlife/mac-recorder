/* 做 Studio 效果演示片：无界面 Chrome 打开 demo/index.html，按 30 帧/秒逐帧截图（2 倍像素，和 Retina 屏录的一样、不带鼠标）
   → raw.mp4 + track.json，再用录屏.app 的 Studio 导出成片。全程在后台，不碰真鼠标
   用法：node demo/make.mjs [成片.mp4]       （预览单帧：node demo/make.mjs --still 秒数 …）
        node demo/make.mjs --post [成片.mp4]  画面不重做，只重配音乐、旁白、字幕（改了 voice.mjs 的词以后用）
        node demo/make.mjs --studio …         不重新截图，从 raw.mp4 重新 Studio 导出
        加 --vertical = 竖屏 9:16 手机版（推近 2.6 倍，成片叫 Studio效果演示-竖屏.mp4） */
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir, homedir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const still = args[0] === '--still' ? args.slice(1) : null;
const flag = f => { const i = args.indexOf(f); if (i < 0) return false; args.splice(i, 1); return true; };
const POST = flag('--post'), STUDIO_ONLY = flag('--studio'), VERTICAL = flag('--vertical');
const OUT = still ? null : (args[0] || join(homedir(), 'Desktop', VERTICAL ? 'Studio效果演示-竖屏.mp4' : 'Studio效果演示.mp4'));
const W = 1440, H = 900, FPS = 30;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const APP = '/Applications/录屏.app/Contents/MacOS/Recorder';
const PORT = 9447;
const PROJ = join(tmpdir(), 'studio-demo'), FRAMES = join(PROJ, 'frames'), VIDEO = join(PROJ, VERTICAL ? 'studio-v.mp4' : 'studio.mp4');
if (!POST && !STUDIO_ONLY) {
rmSync(FRAMES, { recursive: true, force: true }); mkdirSync(FRAMES, { recursive: true });

const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
  '--allow-file-access-from-files', `--user-data-dir=${join(tmpdir(), 'studio-demo-chrome')}`,
  pathToFileURL(join(HERE, 'index.html')).href], { stdio: 'ignore' });
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
const evaluate = expr => send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }).then(m => m.result?.result?.value);
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 2, mobile: false });
while (!(await evaluate('!!window.DEMO && document.readyState === "complete"'))) await sleep(200);
await evaluate('document.fonts.ready.then(() => DEMO.init())');

const shot = async (T, file) => {
  await evaluate(`DEMO.frame(${T.toFixed(4)}); new Promise(r => requestAnimationFrame(() => r(1)))`);
  const m = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(file, Buffer.from(m.result.data, 'base64'));
};
if (still) {
  for (const s of still) await shot(+s, `/tmp/demo_${s}.png`);
  ws.close(); await quit(); console.log('stills done'); process.exit(0);
}
const end = await evaluate('DEMO.END'), n = Math.round(end * FPS), t0 = Date.now();
for (let i = 0; i < n; i++) {
  await shot(i / FPS, join(FRAMES, `f${String(i).padStart(5, '0')}.png`));
  if (i % 30 === 0) process.stdout.write(`\r帧 ${i}/${n}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
}
writeFileSync(join(PROJ, 'track.json'), JSON.stringify(await evaluate('DEMO.track()')));
ws.close(); await quit();
console.log('\n合成原始画面…');
// 按 709 矩阵转 YUV（和录屏一样），解码回来颜色不偏
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', String(FPS), '-i', join(FRAMES, 'f%05d.png'),
  '-vf', 'scale=out_color_matrix=bt709:out_range=tv', '-colorspace', 'bt709',
  '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '10', '-preset', 'medium', join(PROJ, 'raw.mp4')], { stdio: 'inherit' });
}
if (!POST) {
  console.log('Studio 导出…');
  const extra = VERTICAL ? { STUDIO_ASPECT: '9:16', STUDIO_ZOOM: '2.6' } : {};
  execFileSync(APP, ['-ApplePersistenceIgnoreState', 'YES'], { stdio: 'inherit', env: { ...process.env, ...extra, STUDIO: PROJ, STUDIO_OUT: VIDEO } });
}
const probe = (f, what) => execFileSync('ffprobe', ['-v', 'error', ...what, '-of', 'csv=p=0', f]).toString().trim();
const end = +probe(VIDEO, ['-show_entries', 'format=duration']);
const [VW, VH] = probe(VIDEO, ['-select_streams', 'v:0', '-show_entries', 'stream=width,height']).split(',').map(Number);

// 声音：背景音乐（第一次点击前进鼓）+ 点击、打字声 + 旁白（说话时音乐压到一半）
const voice = JSON.parse(readFileSync(join(HERE, 'voice', 'voice.json'), 'utf8'));
voice.forEach((v, i) => { const nx = voice[i + 1]; if (nx && v.at + v.dur > nx.at - 0.2) console.log(`⚠ 旁白 ${v.key} 和 ${nx.key} 挨太近`); });
const track = JSON.parse(readFileSync(join(PROJ, 'track.json'), 'utf8'));
const timeline = join(PROJ, 'timeline.json'), music = join(PROJ, 'music.wav');
writeFileSync(timeline, JSON.stringify({
  end, drop: track.clicks[0][0] - 0.6,
  events: [...track.clicks.map(c => ({ t: c[0], type: 'click' })), ...track.keys.map(t => ({ t, type: 'tap' }))],
  duck: voice.map(v => [v.at, v.at + v.dur, 0.45]),
}));
execFileSync('node', [join(HERE, '..', 'promo', 'audio.mjs'), timeline, music], { stdio: 'inherit' });

// 字幕：每句按逗号、句号拆成短句，一次只显示一行；拆开的地方对准旁白里真实的停顿（silencedetect），不是平均分
function captions(v) {
  const parts = v.text.split(/[，。！？]/).map(x => x.trim()).filter(Boolean);
  const log = spawnSync('ffmpeg', ['-hide_banner', '-i', join(HERE, 'voice', `${v.key}.wav`), '-af', 'silencedetect=n=-35dB:d=0.1', '-f', 'null', '-']).stderr.toString();
  const starts = [...log.matchAll(/silence_start: ([\d.]+)/g)].map(m => +m[1]), ends = [...log.matchAll(/silence_end: ([\d.]+)/g)].map(m => +m[1]);
  const pauses = starts.map((s, i) => (s + (ends[i] ?? s)) / 2);
  const total = parts.reduce((a, p) => a + p.length, 0);
  const cuts = [0];
  let acc = 0;
  for (let k = 0; k < parts.length - 1; k++) {
    acc += parts[k].length;
    const guess = v.dur * acc / total;
    const near = pauses.filter(p => p > cuts.at(-1) + 0.3 && Math.abs(p - guess) < 0.8).sort((a, b) => Math.abs(a - guess) - Math.abs(b - guess))[0];
    cuts.push(near ?? guess);
  }
  cuts.push(v.dur + 0.35);
  return parts.map((text, k) => ({ text, a: v.at + cuts[k], b: v.at + cuts[k + 1] }));
}
const ts = x => { const cs = Math.round(x * 100); return `${Math.floor(cs / 360000)}:${String(Math.floor(cs / 6000) % 60).padStart(2, '0')}:${String(Math.floor(cs / 100) % 60).padStart(2, '0')}.${String(cs % 100).padStart(2, '0')}`; };
const subs = voice.flatMap(captions);
// 字号：横屏按高度；竖屏还要保证最长那句一行放得下（英文字母按半个字宽算）
const widest = Math.max(...subs.map(c => [...c.text].reduce((a, ch) => a + (/[\x00-\x7f]/.test(ch) ? 0.55 : 1), 0)));
const fs = Math.round(Math.min(VH * 0.055, VW * 0.86 / (widest + 0.6)));
const ass = join(PROJ, 'subs.ass');
// WrapStyle 2 = 不自动换行（保证一行）；BorderStyle 3 = 字底下垫半透明黑底
writeFileSync(ass, `[Script Info]
ScriptType: v4.00+
PlayResX: ${VW}
PlayResY: ${VH}
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,PingFang SC,${fs},&H00FFFFFF,&H00FFFFFF,&H70000000,&H70000000,1,0,0,0,100,100,${Math.round(fs * 0.04)},0,3,${Math.round(fs * 0.3)},0,2,0,0,${Math.round(VH * 0.06)},1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
${subs.map(c => `Dialogue: 0,${ts(c.a)},${ts(c.b)},Default,,0,0,0,,{\\fad(100,100)}${c.text}`).join('\n')}
`);
subs.forEach(c => console.log(`  ${c.a.toFixed(2)}–${c.b.toFixed(2)}  ${c.text}`));
const vparts = voice.map((v, i) => `[${2 + i}]volume=1.35,adelay=${Math.round(v.at * 1000)}:all=1[v${i}]`);
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', VIDEO, '-i', music, ...voice.flatMap(v => ['-i', join(HERE, 'voice', `${v.key}.wav`)]),
  '-filter_complex', `[1]volume=0.55[m];${vparts.join(';')};[m]${voice.map((_, i) => `[v${i}]`).join('')}amix=inputs=${voice.length + 1}:normalize=0:duration=first,alimiter=limit=0.9:level=false[a]`,
  '-map', '0:v', '-map', '[a]', '-vf', `ass=${ass}`, '-c:v', 'libx264', '-crf', '16', '-preset', 'medium', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '192k', '-movflags', '+faststart', OUT], { stdio: 'inherit' });
console.log('完成：', OUT);
