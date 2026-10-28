/* 演示片旁白：每句合成一个 wav（去掉首尾静音），时长写进 voice/voice.json，make.mjs 按 at（第几秒开始说）放进片子
   用法：node demo/voice.mjs            微软晓晓（uvx edge-tts，联网，失败自动走本机代理 7897）
        ONLY=c,e node demo/voice.mjs   只重做这几句                                                       */
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const DIR = join(HERE, 'voice');
const VOICE = process.env.VOICE || 'zh-CN-XiaoxiaoNeural', RATE = process.env.RATE || '+6%';
// at = 片子第几秒开始说（对着 index.html 里的时间表 S）
export const LINES = [
  { key: 'a', at: 0.5, text: '这是我自己做的 Mac 录屏软件，最近给它加了 Studio 效果。', spoken: '这是我自己做的Mac录屏软件，最近给它加了Studio效果。' },
  { key: 'b', at: 6.3, text: '录的时候正常操作就行。点到哪里，镜头就自动推到哪里。' },
  { key: 'c', at: 11.9, text: '鼠标轨迹会修得很顺滑，每次点击还有个小波纹。' },
  { key: 'd', at: 16.9, text: '停下来不操作，画面自动拉回全景。' },
  { key: 'e', at: 21.6, text: '背景、圆角和阴影，都是录完自动加上的。' },
  { key: 'f', at: 28.9, text: '不用再剪辑，录完直接就是成片。' },
];

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const ONLY = process.env.ONLY?.split(',');
  const old = ONLY && existsSync(join(DIR, 'voice.json')) ? JSON.parse(readFileSync(join(DIR, 'voice.json'), 'utf8')) : [];
  if (!ONLY) rmSync(DIR, { recursive: true, force: true });
  mkdirSync(DIR, { recursive: true });
  const out = [];
  for (const l of LINES) {
    const prev = old.find(o => o.key === l.key);
    if (ONLY && !ONLY.includes(l.key) && prev) { out.push({ ...prev, at: l.at, text: l.text }); continue; }
    const mp3 = join(DIR, `${l.key}.mp3`), wav = join(DIR, `${l.key}.wav`);
    for (let i = 0; ; i++) {
      try {
        execFileSync('uvx', ['edge-tts', '--voice', VOICE, `--rate=${RATE}`, '--text', l.spoken || l.text, '--write-media', mp3,
          ...(i ? ['--proxy', 'http://127.0.0.1:7897'] : [])], { stdio: ['ignore', 'ignore', 'pipe'] });
        break;
      } catch (e) { if (i === 3) throw e; console.log(`  ${l.key} 连接失败，重试（${i + 1}）…`); execFileSync('sleep', ['2']); }
    }
    execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', mp3, '-af',
      'silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse',
      '-ar', '48000', '-ac', '2', wav]);
    rmSync(mp3);
    const dur = +execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', wav]).toString().trim();
    out.push({ key: l.key, at: l.at, text: l.text, dur: +dur.toFixed(3) });
    console.log(l.key, dur.toFixed(2) + 's', l.text);
  }
  writeFileSync(join(DIR, 'voice.json'), JSON.stringify(out, null, 1));
}
