/* 旁白：把每句话合成成 wav，量出时长，写 voice/voice.js（网页按时长排画面、出字幕）
   用法：ENGINE=edge node promo/voice.mjs        微软神经网络语音（uvx edge-tts，联网；默认晓晓 zh-CN-XiaoxiaoNeural）
        VOICE=语音名 RATE=语速 node promo/voice.mjs   macOS 自带 say（语音名看 say -v '?'；默认 Tingting，语速 200 字/分）
   只重做某几句：ONLY=g ENGINE=edge node promo/voice.mjs
   英文版：LANG_PROMO=en ENGINE=edge node promo/voice.mjs（默认 Ava，存 voice_en/）
   第几段说哪句（scene + at = 从该段开头第几秒开始说）在下面 LINES 里改                                    */
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const EN = process.env.LANG_PROMO === 'en';     // 英文版：LANG_PROMO=en，旁白存 voice_en/
const DIR = join(HERE, EN ? 'voice_en' : 'voice');
const EDGE = process.env.ENGINE === 'edge';
const VOICE = process.env.VOICE || (EDGE ? (EN ? 'en-US-AvaNeural' : 'zh-CN-XiaoxiaoNeural') : (EN ? 'Samantha' : 'Tingting'));
const RATE = process.env.RATE || (EDGE ? '+6%' : '200');

// say 读的文字（spoken）可以和字幕（text）不一样：比如英文名要写成它好念的样子
const LINES_EN = [
  { key: 'a', scene: 'A', at: .2, text: 'Can’t record your Mac’s system audio?' },
  { key: 'b', scene: 'B', at: .15, text: 'Try Screen Recorder. It’s free, and just 1\u00a0MB.', spoken: 'Try Screen Recorder. It’s free, and just one megabyte.' },
  { key: 'c', scene: 'C', at: .2, text: 'Pick what to record with one click: the whole screen, or any window.' },
  { key: 'd', scene: 'D', at: .2, text: 'Or select any area, just like a screenshot. Drag to resize.' },
  { key: 'e', scene: 'E', at: .15, text: 'Turn on the mic and talk while you record. Your voice gets captured too.' },
  { key: 'f1', scene: 'F', at: .1, text: 'Hit stop, and the video opens right away.' },
  { key: 'f2', scene: 'F', at: 4.6, text: 'Picture and sound, all there.' },
  { key: 'g', scene: 'G', at: .5, text: 'Free and easy to use. Give it a try!' },
];
const LINES_ZH = [
  { key: 'a', scene: 'A', at: .2, text: 'Mac 自带的录屏，录不到电脑声音？' },
  { key: 'b', scene: 'B', at: .15, text: '试试这个小工具，录屏。免费，只有 1MB。', spoken: '试试这个小工具，录屏。免费，只有一兆。' },
  { key: 'c', scene: 'C', at: .2, text: '想录什么，点一下就行。整个屏幕、任意窗口，都可以。' },
  { key: 'd', scene: 'D', at: .2, text: '还能像截图一样框选区域，拖一拖，大小随便调。' },
  { key: 'e', scene: 'E', at: .15, text: '还能打开麦克风，边录边讲解，你说的话也一起录进去。' },
  { key: 'f1', scene: 'F', at: .1, text: '一停下，视频马上打开。' },
  { key: 'f2', scene: 'F', at: 4.6, text: '画面和声音，都录下来了。' },
  { key: 'g', scene: 'G', at: .5, text: '免费又好用，你也试试吧。' },
];
const LINES = EN ? LINES_EN : LINES_ZH;

// ONLY=g,f2 → 只重新合成这几句，其他句子沿用上次的结果
const ONLY = process.env.ONLY?.split(',');
let old = [];
if (ONLY) { globalThis.window = {}; await import(join(DIR, 'voice.js') + `?${Date.now()}`); old = window.VOICE; }
else { rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true }); }
// 音量包络：每 1/30 秒一个值（0~1），网页里麦克风的声波跟着旁白跳
function envelope(wav) {
  const pcm = execFileSync('ffmpeg', ['-v', 'error', '-i', wav, '-ac', '1', '-ar', '48000', '-f', 's16le', '-'], { maxBuffer: 1 << 28 });
  const n = 1600, vals = [];
  for (let i = 0; i + n * 2 <= pcm.length; i += n * 2) {
    let sum = 0;
    for (let k = 0; k < n; k++) { const v = pcm.readInt16LE(i + k * 2) / 32768; sum += v * v; }
    vals.push(Math.sqrt(sum / n));
  }
  const peak = Math.max(...vals, 1e-6);
  return vals.map(v => +(v / peak).toFixed(2));
}

const out = [];
for (const l of LINES) {
  const prev = old.find(o => o.key === l.key);
  if (ONLY && !ONLY.includes(l.key) && prev) { out.push({ ...prev, scene: l.scene, at: l.at, text: l.text, env: envelope(join(DIR, `${l.key}.wav`)) }); console.log(l.key, '沿用'); continue; }
  const aiff = join(DIR, `${l.key}.${EDGE ? 'mp3' : 'aiff'}`), wav = join(DIR, `${l.key}.wav`);
  if (EDGE) {
    // 微软的服务偶尔断连：最多试 4 次，第 2 次起走本机代理
    for (let i = 0; ; i++) {
      try {
        execFileSync('uvx', ['edge-tts', '--voice', VOICE, `--rate=${RATE}`, '--text', l.spoken || l.text, '--write-media', aiff,
          ...(i ? ['--proxy', 'http://127.0.0.1:7897'] : [])], { stdio: ['ignore', 'ignore', 'pipe'] });
        break;
      } catch (e) {
        if (i === 3) throw e;
        console.log(`  ${l.key} 连接失败，重试（${i + 1}）…`);
        execFileSync('sleep', ['2']);
      }
    }
  }
  else execFileSync('say', ['-v', VOICE, '-r', RATE, '-o', aiff, l.spoken || l.text]);
  // 转成 48k 立体声；去掉首尾静音，时长才准
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', aiff, '-af',
    'silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse',
    '-ar', '48000', '-ac', '2', wav]);
  rmSync(aiff);
  const dur = +execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', wav]).toString().trim();
  out.push({ key: l.key, scene: l.scene, at: l.at, text: l.text, dur: +dur.toFixed(3), env: envelope(wav) });
  console.log(l.key, dur.toFixed(2) + 's', l.text);
}
writeFileSync(join(DIR, 'voice.js'), `// voice.mjs 生成（${VOICE}，语速 ${RATE}）\nwindow.VOICE = ${JSON.stringify(out, null, 1)};\n`);
