/* 旁白：每句（=一行字幕）合成一个 wav 并量时长，写 voice/voice.json。edge-tts 云希男声，连不上走本机代理 7897 */
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { SCENES } from './lines.mjs';
const HERE = dirname(fileURLToPath(import.meta.url)), DIR = join(HERE, 'voice');
mkdirSync(DIR, { recursive: true });
const out = [];
let n = 0;
for (const [scene, lines] of SCENES) for (const [text, spoken] of lines) {
  const key = `l${String(++n).padStart(3, '0')}`, mp3 = join(DIR, key + '.mp3'), wav = join(DIR, key + '.wav');
  if (!existsSync(wav)) {
    for (let i = 0; ; i++) {
      try {
        execFileSync('uvx', ['edge-tts', '--voice', 'zh-CN-YunxiNeural', '--rate=-4%', '--text', spoken || text, '--write-media', mp3,
          ...(i % 2 ? ['--proxy', 'http://127.0.0.1:7897'] : [])], { stdio: ['ignore', 'ignore', 'pipe'] });
        break;
      } catch (e) { if (i > 5) throw e; }
    }
    // 去掉首尾静音，转 48k 单声道
    execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', mp3, '-af', 'silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse',
      '-ar', '48000', '-ac', '1', wav]);
  }
  const dur = +execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', wav]).toString();
  out.push({ key, scene, text, dur: Math.round(dur * 1000) / 1000 });
  process.stdout.write(`\r${n}`);
}
writeFileSync(join(DIR, 'voice.json'), JSON.stringify(out, null, 1));
console.log('\nOK', out.length, 'lines', out.reduce((a, b) => a + b.dur, 0).toFixed(1), 's');
