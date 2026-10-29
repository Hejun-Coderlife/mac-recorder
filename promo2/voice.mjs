/* 旁白：每句合成一个 wav，量出时长，写 voice/voice.js（网页按时长排每段画面、出字幕）
   用法：node voice.mjs                 微软云希（男声）（uvx edge-tts，要联网；连不上会自动走本机代理 7897 重试）
        ONLY=c,d node voice.mjs         只重做某几句
        ENGINE=say node voice.mjs       用 macOS 自带语音（Tingting，机器人味重，只在 edge 用不了时临时顶一下）
   （录屏介绍视频；照搬 mac-unpack/promo/voice.mjs）
   scene = 哪一段画面说；at = 从该段开头第几秒开始说；spoken = 实际念的（和字幕不一样时才写） */
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const DIR = join(HERE, 'voice');
const EDGE = process.env.ENGINE !== 'say';
const VOICE = process.env.VOICE || (EDGE ? 'zh-CN-YunxiNeural' : 'Tingting');   // 男声云希（用户要男声；云扬 YunyangNeural 偏新闻播音腔）
const RATE = process.env.RATE || (EDGE ? '+4%' : '190');

export const LINES = [
  { key: 'a', scene: 'A', at: .3, text: '这是我自己做的录屏软件，Mac 和 Windows 都能用。' },
  { key: 'r1', scene: 'R1', at: .3, text: '想录屏幕？选整个屏幕、一个窗口，或者框出一小块，点「开始录制」就行。' },
  { key: 'r2', scene: 'R2', at: .25, text: '电脑里的声音、你说的话，都能一起录下来。还能先倒数三秒，再开始。' },
  { key: 's', scene: 'S', at: .3, text: '打开「Studio 效果」，它会帮你做一件事：你点到哪里，镜头就自动凑过去，让人看清你在点什么。' },
  { key: 'e1', scene: 'E1', at: .4, text: '录完，会打开这个剪辑窗口。上面是预览，就是最后视频的样子。' },
  { key: 'e2', scene: 'E2', at: .2, text: '下面是时间轴，从左到右，就是视频从头到尾。右边，是调样式的地方。' },
  { key: 'e3', scene: 'E3', at: .2, text: '时间轴上的紫色块，表示这段时间，镜头凑近了看。' },
  { key: 'e4', scene: 'E4', at: .2, text: '觉得凑近太久了？拖住它的一头，往回拉，就变短了。' },
  { key: 'e5', scene: 'E5', at: .2, text: '拖住中间，可以整段挪个位置。' },
  { key: 'e6', scene: 'E6', at: .2, text: '空白的地方双击，就多加一段；不要了，点「删除」。' },
  { key: 'e7', scene: 'E7', at: .2, text: '选中一段，拖这个滑块，能调放大多少倍。越往右，凑得越近。' },
  { key: 'e8', scene: 'E8', at: .2, text: '镜头默认跟着鼠标走。选「固定位置」，在预览上点一下，镜头就一直盯着那里。' },
  { key: 'e9', scene: 'E9', at: .2, text: '开头结尾有不要的？拖动两头的黄色括号，剪掉就行。' },
  { key: 'e10', scene: 'E10', at: .2, text: '点中间的播放键，随时看看效果。改错了？点撤销，退回去。' },
  { key: 'p1', scene: 'P1', at: .3, text: '右边的样式，最省事的是选一个预设。比如「抖音竖屏」，画面马上变成竖的，背景也换好了。' },
  { key: 'p2', scene: 'P2', at: .2, text: '背景还能自己换：纯色、渐变、系统壁纸，或者你自己的图片。' },
  { key: 'p3', scene: 'P3', at: .2, text: '边距、圆角、阴影，拖一拖就能调。' },
  { key: 'p4', scene: 'P4', at: .2, text: '鼠标可以放大一点；不动的时候，自动藏起来；点一下有个小波纹，还能配上点击声。' },
  { key: 'p5', scene: 'P5', at: .2, text: '运动模糊打开后，镜头快速移动时，会带一点拖影，看着更顺。停下来，字还是清清楚楚。' },
  { key: 'x1', scene: 'X1', at: .2, text: '都调好了，点右上角「导出」，成片就出来了。' },
  { key: 'x2', scene: 'X2', at: .3, text: '同一段录屏，可以是横屏的教程，也可以是抖音的竖屏。' },
  { key: 'w', scene: 'W', at: .3, text: '在 Windows 上，还能一键截图。框一块，截好的图出现在右下角，直接拖进微信，就发出去了。' },
  { key: 'u', scene: 'U', at: .3, text: '有新版本，它会自己在后台悄悄更新，你什么都不用管。' },
  { key: 'z', scene: 'Z', at: .4, text: '录屏。录完，就是成片。' },
];

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const ONLY = process.env.ONLY?.split(',');
  let old = [];
  if (ONLY && existsSync(join(DIR, 'voice.js'))) { globalThis.window = {}; await import(join(DIR, 'voice.js') + `?${Date.now()}`); old = window.VOICE; }
  else { rmSync(DIR, { recursive: true, force: true }); mkdirSync(DIR, { recursive: true }); }
  const out = [];
  for (const l of LINES) {
    const prev = old.find(o => o.key === l.key);
    if (ONLY && !ONLY.includes(l.key) && prev) { out.push({ ...prev, scene: l.scene, at: l.at, text: l.text }); console.log(l.key, '沿用'); continue; }
    const raw = join(DIR, `${l.key}.${EDGE ? 'mp3' : 'aiff'}`), wav = join(DIR, `${l.key}.wav`);
    if (EDGE) {
      for (let i = 0; ; i++) {
        try {
          execFileSync('uvx', ['edge-tts', '--voice', VOICE, `--rate=${RATE}`, '--text', l.spoken || l.text, '--write-media', raw,
            ...(i ? ['--proxy', 'http://127.0.0.1:7897'] : [])], { stdio: ['ignore', 'ignore', 'pipe'] });
          break;
        } catch (e) {
          if (i === 3) throw e;
          console.log(`  ${l.key} 连接失败，重试（${i + 1}）…`);
          execFileSync('sleep', ['2']);
        }
      }
    } else execFileSync('say', ['-v', VOICE, '-r', RATE, '-o', raw, l.spoken || l.text]);
    // 转 48k 立体声，去掉首尾静音，时长才准
    execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-i', raw, '-af',
      'silenceremove=start_periods=1:start_threshold=-50dB,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse',
      '-ar', '48000', '-ac', '2', wav]);
    rmSync(raw);
    const dur = +execFileSync('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', wav]).toString().trim();
    out.push({ key: l.key, scene: l.scene, at: l.at, text: l.text, dur: +dur.toFixed(3) });
    console.log(l.key, dur.toFixed(2) + 's', l.text);
  }
  writeFileSync(join(DIR, 'voice.js'), `// voice.mjs 生成（${VOICE}，语速 ${RATE}）\nwindow.VOICE = ${JSON.stringify(out, null, 1)};\n`);
  console.log('写好了 voice/voice.js，接着跑 node record.mjs 出片');
}
