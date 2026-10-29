/* 逐帧录制宣传视频：无界面 Chrome 打开 index.html?rec=1，按 30 帧/秒调 PROMO.frame(T) 截图，ffmpeg 合成 MP4
   用法：node promo/record.mjs [输出.mp4]        （预览单帧：node promo/record.mjs --still 秒数 输出.png） */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const still = args[0] === '--still' ? args.slice(1).map(String) : null;   // [秒数,...] 输出到 /tmp/promo_秒数.png
const EN = process.env.LANG_PROMO === 'en';     // 英文版：LANG_PROMO=en node promo/record.mjs → promo_en.mp4
const VDIR = EN ? 'voice_en' : 'voice';
const OUT = still ? null : (args[0] || join(HERE, EN ? 'promo_en.mp4' : 'promo.mp4'));
const W = 1080, H = 1920, FPS = 30;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const PORT = 9334;
const DIR = join(tmpdir(), 'promo-frames');
rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true });

const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
  '--force-device-scale-factor=1', '--allow-file-access-from-files', `--user-data-dir=${join(tmpdir(), 'promo-chrome')}`,
  pathToFileURL(join(HERE, 'index.html')).href + '?rec=1' + (EN ? '&lang=en' : '')], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
// 关 Chrome 要等它真正退出：不然紧接着再跑一次时调试端口还被占着，新的那次会连到旧页面上失败
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
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });
while (!(await evaluate('!!window.PROMO && document.readyState === "complete"'))) await sleep(200);
await evaluate('document.fonts.ready.then(() => 1)');
await sleep(500);

const shot = async (T, file) => {
  // 换帧后等图片（片子的逐帧图）解码完再截
  await evaluate(`PROMO.frame(${T.toFixed(4)}); Promise.all([...document.images].map(i => i.decode().catch(() => 0))).then(() => new Promise(r => requestAnimationFrame(() => r(1))))`);
  const m = await send('Page.captureScreenshot', { format: file.endsWith('.png') ? 'png' : 'jpeg', quality: file.endsWith('.png') ? undefined : 95 });
  writeFileSync(file, Buffer.from(m.result.data, 'base64'));
};
if (still) {
  for (const s of still) await shot(+s, `/tmp/promo_${EN ? 'en_' : ''}${s}.png`);
  ws.close(); await quit(); console.log('stills done'); process.exit(0);
}
const end = await evaluate('PROMO.END');
const n = Math.round(end * FPS);
const t0 = Date.now();
for (let i = 0; i < n; i++) {
  await shot(i / FPS, join(DIR, `f${String(i).padStart(5, '0')}.jpg`));
  if (i % 30 === 0) process.stdout.write(`\r帧 ${i}/${n}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
}
// 声音：时间线上的音效 + 片子原声播放的时段
const events = await evaluate('PROMO.events()'), clipAudio = await evaluate('PROMO.clipAudio()'), voice = await evaluate('PROMO.voiceTrack()');
ws.close(); await quit();
const timeline = join(DIR, 'timeline.json'), music = join(DIR, 'music.wav');
writeFileSync(timeline, JSON.stringify({ end, events, duck: [...clipAudio.filter(c => c.main).map(c => [c.at, c.at + c.dur, .22]), ...voice.map(v => [v.at, v.at + v.dur, .5])] }));
execFileSync('node', [join(HERE, 'audio.mjs'), timeline, music], { stdio: 'inherit' });
console.log('\n合成视频…');
// 片子原声：按时段裁出来、放到对应时间（用响度标准化过的原声 audio_norm.wav，再限幅）
const parts = clipAudio.map((c, i) => `[2]atrim=start=${c.from}:duration=${c.dur},asetpts=PTS-STARTPTS,volume=${(c.vol * 1.4).toFixed(2)},afade=t=in:d=${Math.min(c.fin, c.dur / 2).toFixed(3)},afade=t=out:st=${Math.max(0, c.dur - Math.min(c.fout, c.dur / 2)).toFixed(3)}:d=${Math.min(c.fout, c.dur / 2).toFixed(3)},adelay=${Math.round(c.at * 1000)}:all=1[c${i}]`);
// 旁白：每句一个文件（输入从第 3 个起），放到对应时间
const vparts = voice.map((v, i) => `[${3 + i}]volume=1.3,adelay=${Math.round(v.at * 1000)}:all=1[v${i}]`);
const filter = `[2]asplit=${clipAudio.length}${clipAudio.map((_, i) => `[s${i}]`).join('')};` +
  parts.map((p, i) => p.replace('[2]', `[s${i}]`)).join(';') + ';' + vparts.join(';') +
  `;[1]${clipAudio.map((_, i) => `[c${i}]`).join('')}${voice.map((_, i) => `[v${i}]`).join('')}amix=inputs=${clipAudio.length + voice.length + 1}:normalize=0:duration=first,alimiter=limit=0.85:level=false[a]`;
execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', String(FPS), '-i', join(DIR, 'f%05d.jpg'), '-i', music, '-i', join(HERE, 'clip', 'audio_norm.wav'), ...voice.flatMap(v => ['-i', join(HERE, VDIR, `${v.key}.wav`)]),
  '-filter_complex', filter, '-map', '0:v', '-map', '[a]',
  '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '16', '-preset', 'slow', '-c:a', 'aac', '-b:a', '192k', '-shortest', '-movflags', '+faststart', OUT], { stdio: 'inherit' });
console.log('完成：', OUT);
