/* 逐帧录「录屏宣传片」（完整版）：本地 http 服务 → 无界面 Chrome 2 倍像素截图（3840×2160）→ 缩到 1920×1080 60 帧 → 配乐
   用法：node record.mjs              出 ~/Desktop/录屏宣传片.mp4（配乐 + 轻音效）和 录屏宣传片-无声.mp4
         node record.mjs --still 秒…  静帧到 /tmp/p5_秒.png（检查用）
         node record.mjs --audio      只重新配乐（用已有的无声版）
   配乐：Apple Loops「07 Chillwave」120 拍/分（Landing in LA 一组 + Doubledown Beat），/tmp/p5m/*.wav 是去掉 AAC 前导静音后正好 2 小节长的版本 */
import { spawn, execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir, homedir } from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2), CAP = argv.includes('--cap'), args = argv.filter(a => a !== '--cap');   // --cap = 动作字幕版
const still = args[0] === '--still' ? args.slice(1) : null;
const audioOnly = args[0] === '--audio';
const DESK = join(homedir(), 'Desktop');
const OUT = join(DESK, CAP ? '录屏宣传片-字幕版.mp4' : '录屏宣传片.mp4'), OUT_SILENT = join(DESK, CAP ? '录屏宣传片-字幕版-无声.mp4' : '录屏宣传片-无声.mp4');
const W = 1920, H = 1080, FPS = 60, DPR = 2;
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const PORT = 9361, HTTP = 9362;
const DIR = join(tmpdir(), CAP ? 'recorder-promo5cap-frames' : 'recorder-promo5-frames');

let END, clicks, sounds, keys, countdown;
if (!audioOnly) {
  const http = spawn('python3', ['-m', 'http.server', String(HTTP), '--bind', '127.0.0.1'], { cwd: HERE, stdio: 'ignore' });
  const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--window-size=${W},${H}`, '--hide-scrollbars',
    `--force-device-scale-factor=${DPR}`, `--user-data-dir=${join(tmpdir(), 'recorder-promo5-chrome')}`,
    `http://127.0.0.1:${HTTP}/full.html${CAP ? '?cap=1' : ''}`], { stdio: 'ignore' });
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const quit = async () => { http.kill(); await new Promise(r => { chrome.once('exit', r); chrome.kill(); }); };
  let ws;
  for (let i = 0; i < 60 && !ws; i++) {
    await sleep(300);
    try { const pg = (await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()).find(t => t.type === 'page'); if (pg) ws = new WebSocket(pg.webSocketDebuggerUrl); } catch {}
  }
  await new Promise(r => ws.addEventListener('open', r));
  let id = 0; const pend = new Map();
  ws.addEventListener('message', e => { const m = JSON.parse(e.data);
    if (m.method === 'Runtime.exceptionThrown') console.error('页面出错：', JSON.stringify(m.params.exceptionDetails).slice(0, 700));
    if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') console.error('console.error', JSON.stringify(m.params.args).slice(0, 400));
    pend.get(m.id)?.(m); pend.delete(m.id); });
  const send = (method, params) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const evaluate = expr => send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }).then(m => {
    if (m.result?.exceptionDetails) throw new Error(JSON.stringify(m.result.exceptionDetails).slice(0, 900));
    return m.result?.result?.value;
  });
  await send('Runtime.enable', {}); await send('Page.reload', {});
  await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: DPR, mobile: false });
  for (let i = 0; !(await evaluate('!!window.READY && document.fonts.status === "loaded"')); i++) {
    if (i > 150) { console.error('页面没加载出来'); await quit(); process.exit(1); }
    await sleep(200);
  }
  END = await evaluate('PROMO.END'); clicks = await evaluate('PROMO.clicks()'); sounds = await evaluate('PROMO.sounds()'); keys = await evaluate('PROMO.keys()'); countdown = await evaluate('PROMO.countdown()');
  const N = Math.round(END * FPS);
  const shot = async (t, file) => {
    await evaluate(`PROMO.frame(${t.toFixed(4)}).then(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(1)))))`);
    const m = await send('Page.captureScreenshot', { format: file.endsWith('.png') ? 'png' : 'jpeg', quality: file.endsWith('.png') ? undefined : 95 });
    writeFileSync(file, Buffer.from(m.result.data, 'base64'));
  };
  if (still) {
    for (const s of still) await shot(+s, `/tmp/p5${CAP ? 'c' : ''}_${s}.png`);
    ws.close(); await quit(); console.log('stills done'); process.exit(0);
  }
  rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true });
  const t0 = Date.now();
  for (let i = 0; i < N; i++) {
    await shot(i / FPS, join(DIR, `f${String(i).padStart(5, '0')}.jpg`));
    if (i % 60 === 0) process.stdout.write(`\r帧 ${i}/${N}  ${((Date.now() - t0) / 1000).toFixed(0)}s`);
  }
  ws.close(); await quit();
  console.log(`\n截完 ${N} 帧，${((Date.now() - t0) / 1000).toFixed(0)}s`);
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-framerate', String(FPS), '-i', join(DIR, 'f%05d.jpg'),
    '-vf', `scale=${W}:${H}:flags=lanczos,format=yuv420p`, '-c:v', 'libx264', '-preset', 'slow', '-crf', '14', '-movflags', '+faststart', OUT_SILENT]);
  writeFileSync(join(tmpdir(), CAP ? 'p5c_events.json' : 'p5_events.json'), JSON.stringify({ END, clicks, sounds, keys, countdown }));
} else {
  ({ END, clicks, sounds, keys, countdown } = JSON.parse((await import('node:fs')).readFileSync(join(tmpdir(), CAP ? 'p5c_events.json' : 'p5_events.json'), 'utf8')));
}

// ———— 声音 ————
const SR = 48000;
function wav(file, samples) {
  const n = samples.length, b = Buffer.alloc(44 + n * 2);
  b.write('RIFF', 0); b.writeUInt32LE(36 + n * 2, 4); b.write('WAVEfmt ', 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22);
  b.writeUInt32LE(SR, 24); b.writeUInt32LE(SR * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36); b.writeUInt32LE(n * 2, 40);
  for (let k = 0; k < n; k++) b.writeInt16LE(Math.round(Math.max(-1, Math.min(1, samples[k])) * 32767), 44 + k * 2);
  writeFileSync(file, b);
}
const noise = () => Math.random() * 2 - 1;
const burst = (t, d, f, a) => a * Math.exp(-t / d) * (0.6 * Math.sin(2 * Math.PI * f * t) + 0.4 * noise());
// 软件里的 6 种点击音效（和 Studio.swift 的 ClickSound.wave 同一套公式）
const SND = [
  [1920, t => Math.exp(-t / 0.0045) * (0.8 * (0.65 * Math.sin(2 * Math.PI * 3100 * t) + 0.35 * Math.sin(2 * Math.PI * 1700 * t)) + 0.2 * noise()) * 0.32],
  [4800, t => (t < 0.02 ? burst(t, 0.0018, 2600, 0.45) : 0) + (t >= 0.06 && t < 0.08 ? burst(t - 0.06, 0.0015, 3200, 0.25) : 0)],
  [4800, t => Math.exp(-t / 0.012) * (0.6 * Math.sin(2 * Math.PI * 190 * t) + 0.3 * Math.sin(2 * Math.PI * 620 * t)) * 0.5 + burst(t, 0.002, 1800, 0.12)],
  [5760, t => Math.exp(-t / 0.018) * (0.6 * Math.sin(2 * Math.PI * 880 * t) + 0.25 * Math.sin(2 * Math.PI * 2350 * t)) * 0.42],
  [2160, t => 0.4 * Math.pow(Math.sin(Math.PI * t / 0.045), 2) * Math.sin(2 * Math.PI * (500 + 1200 * t / 0.045) * t)],
  [1440, t => Math.exp(-t / 0.0025) * (0.7 * Math.sin(2 * Math.PI * 5200 * t) + 0.3 * Math.sin(2 * Math.PI * 7400 * t)) * 0.3]];
const fx = new Float32Array(Math.ceil((END + 1) * SR));
const put = (at, n, f, g) => { const s0 = Math.round(at * SR); for (let k = 0; k < n; k++) if (s0 + k < fx.length) fx[s0 + k] += g * f(k / SR); };
for (const c of clicks) put(c, SND[0][0], SND[0][1], 0.55);           // 普通点击：很轻的「嗒」
sounds.forEach((s, i) => put(s, SND[i][0], SND[i][1], 1.0));           // 音效菜单逐个划过：每个响一声
for (const k of keys) put(k, SND[2][0], SND[2][1], 0.8);               // 按快捷键
// 倒计时：3、2、1 各一声干净的「嘀」（880Hz 正弦、很快衰减），开录时一声高一点、两个音叠起来的「叮」
const beep = (f, d, a) => t => a * Math.sin(Math.PI * Math.min(1, t / 0.004)) ** 2 * Math.exp(-t / d) * (0.8 * Math.sin(2 * Math.PI * f * t) + 0.2 * Math.sin(4 * Math.PI * f * t));
(countdown || []).forEach((c, i) => i < 3
  ? put(c, Math.round(0.18 * SR), beep(880, 0.045, 0.34), 1)
  : put(c, Math.round(0.45 * SR), t => beep(1318.5, 0.11, 0.26)(t) + beep(1975.5, 0.08, 0.14)(t), 1));
const FXW = join(tmpdir(), 'p5_fx.wav'); wav(FXW, fx);

// 配乐编排（2 秒一小节）：开头只有合成器 → 2 秒鼓和贝斯进 → 12.5 剪辑窗口加吉他 → 24 换一组合成器 + 长贝斯 →
// 36–39.5 运动模糊慢放段：收掉鼓、贝斯、吉他，只留合成器 → 39.5 起鼓组再起 → 53.5 收尾只留合成器淡出
const M = '/tmp/p5m', L = n => `${M}/${n}.wav`;
const lay = [ // [文件, 从, 到, 音量]
  ['Landing_in_LA_Synth_01', 0, 24, 0.8], ['Landing_in_LA_Synth_02', 24, 39.5, 0.8], ['Landing_in_LA_Synth_01', 39.5, 53.5, 0.8], ['Landing_in_LA_Synth_03', 53.5, END, 0.75],
  ['Doubledown_Beat_01', 2, 24, 0.7], ['Doubledown_Beat_02', 24, 36, 0.7], ['Doubledown_Beat_01', 39.5, 53.5, 0.75],
  ['Landing_in_LA_Bass_01', 2, 24, 0.8], ['Landing_in_LA_Bass_02', 24, 36, 0.8], ['Landing_in_LA_Bass_01', 39.5, 53.5, 0.8],
  ['Landing_in_LA_Guitar', 12, 36, 0.42], ['Landing_in_LA_Guitar', 41.5, 53.5, 0.42]];
const inputs = ['-i', OUT_SILENT];
const parts = [];
lay.forEach(([f, a, b, v], i) => {
  inputs.push('-stream_loop', '-1', '-i', L(f));
  const len = b - a;
  parts.push(`[${i + 1}:a]atrim=0:${len},asetpts=PTS-STARTPTS,afade=t=out:st=${Math.max(0, len - 0.04)}:d=0.04,volume=${v},adelay=${a * 1000}|${a * 1000}[m${i}]`);
});
inputs.push('-i', FXW);
const fi = lay.length + 1;
parts.push(`[${fi}:a]aformat=channel_layouts=stereo[fx]`);
parts.push(`${lay.map((_, i) => `[m${i}]`).join('')}amix=inputs=${lay.length}:normalize=0,afade=t=in:st=0:d=0.3,afade=t=out:st=${(END - 2.2).toFixed(2)}:d=2.2[mus]`);
parts.push(`[mus][fx]amix=inputs=2:normalize=0,loudnorm=I=-15:TP=-1.5[a]`);
execFileSync('ffmpeg', ['-v', 'error', '-y', ...inputs, '-filter_complex', parts.join(';'), '-map', '0:v', '-map', '[a]',
  '-c:v', 'copy', '-c:a', 'aac', '-b:a', '256k', '-t', String(END), '-movflags', '+faststart', OUT]);
console.log('OK', OUT, OUT_SILENT);
