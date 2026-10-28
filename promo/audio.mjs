/* 给宣传视频合成声音：背景音乐 + 音效，全部用程序算出来（不用任何现成素材，没有版权问题）
   用法：node promo/audio.mjs 时间线.json 输出.wav
   时间线.json = { end, events:[{t,type}], duck:[[开始,结束,压到多少],...] }（duck = 片子原声、旁白的时段，音乐压低） */
import { readFileSync, writeFileSync } from 'node:fs';

const [, , IN, OUT] = process.argv;
const { end, events, duck, drop } = JSON.parse(readFileSync(IN, 'utf8'));
const SR = 48000, N = Math.ceil(end * SR);
const L = new Float32Array(N), R = new Float32Array(N);

// 伪随机（每次结果一样）
let seed = 1;
const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647) * 2 - 1;
const add = (i, v, pan = 0) => { if (i >= 0 && i < N) { L[i] += v * (1 - pan) ; R[i] += v * (1 + pan); } };
const hz = m => 440 * Math.pow(2, (m - 69) / 12);   // MIDI 音高 → 频率

// ---------------- 乐器 ----------------
// 电钢琴：几个泛音的正弦 + 指数衰减，左右稍微错开
function keys(t, m, dur, vol) {
  const f = hz(m), i0 = Math.floor(t * SR), n = Math.floor((dur + 1.2) * SR);
  for (let k = 0; k < n; k++) {
    const x = k / SR, rel = x > dur ? Math.exp(-(x - dur) * 5) : 1;
    const env = Math.min(1, x * 200) * Math.exp(-x * 1.6) * rel;
    const v = (Math.sin(2 * Math.PI * f * x) + .35 * Math.sin(2 * Math.PI * 2 * f * x) * Math.exp(-x * 4) + .12 * Math.sin(2 * Math.PI * 3.01 * f * x) * Math.exp(-x * 7)) * env * vol;
    add(i0 + k, v, Math.sin(m) * .3);
  }
}
// 柔和铺底（开头和结尾）：慢起慢落的锯齿感正弦叠加
function pad(t, m, dur, vol) {
  const f = hz(m), i0 = Math.floor(t * SR), n = Math.floor(dur * SR);
  for (let k = 0; k < n; k++) {
    const x = k / SR, env = Math.min(1, x / .6) * Math.min(1, (dur - x) / .8);
    let v = 0;
    for (const d of [-.08, 0, .07]) v += Math.sin(2 * Math.PI * f * (1 + d / 100) * x) + .3 * Math.sin(4 * Math.PI * f * (1 + d / 100) * x);
    add(i0 + k, v * env * vol / 3);
  }
}
// 贝斯：正弦 + 一点失真
function bass(t, m, dur, vol) {
  const f = hz(m), i0 = Math.floor(t * SR), n = Math.floor(dur * SR);
  for (let k = 0; k < n; k++) {
    const x = k / SR, env = Math.min(1, x * 300) * Math.min(1, (dur - x) * 60) * (.75 + .25 * Math.exp(-x * 6));
    add(i0 + k, Math.tanh(Math.sin(2 * Math.PI * f * x) * 1.6) * env * vol);
  }
}
// 底鼓：频率从 130 掉到 45 的正弦
function kick(t, vol) {
  const i0 = Math.floor(t * SR), n = Math.floor(.35 * SR); let ph = 0;
  for (let k = 0; k < n; k++) {
    const x = k / SR, f = 45 + 85 * Math.exp(-x * 30);
    ph += 2 * Math.PI * f / SR;
    add(i0 + k, Math.sin(ph) * Math.exp(-x * 9) * vol);
  }
}
// 噪声类（拍手、镲）：高通噪声 + 衰减
function noise(t, dur, decay, vol, hp = .9, pan = 0) {
  const i0 = Math.floor(t * SR), n = Math.floor(dur * SR); let prev = 0, y = 0;
  for (let k = 0; k < n; k++) {
    const s = rnd(); y = hp * (y + s - prev); prev = s;
    add(i0 + k, y * Math.exp(-k / SR * decay) * vol, pan);
  }
}
function clap(t, vol) { for (const d of [0, .012, .024]) noise(t + d, .2, 22, vol * .6, .8); }

// ---------------- 音乐：112 拍/分，C – G – Am – F ----------------
const BPM = 112, BEAT = 60 / BPM, BAR = BEAT * 4;
const CHORDS = [[60, 64, 67, 72], [55, 59, 62, 67], [57, 60, 64, 69], [53, 57, 60, 65]];
const ROOTS = [36, 31, 33, 29];
const DROP = drop ?? 3.55;   // 开场之后（图标出现）进鼓；时间线里可以指定
const OUTRO = end - 3.2;     // 结尾鼓停，只留和弦
for (let bar = 0; bar * BAR < end; bar++) {
  const t0 = bar * BAR, c = CHORDS[bar % 4], r = ROOTS[bar % 4];
  if (t0 < DROP - .01) { for (const m of c) pad(t0, m, BAR + .3, .1); keys(t0, c[0] + 12, BAR, .06); continue; }
  if (t0 >= OUTRO) { for (const m of c) pad(t0, m, end - t0, .05); for (const m of c) keys(t0, m, BAR, .06); continue; }
  // 和弦：第 1 拍和第 2.5 拍（切分）
  for (const m of c) { keys(t0, m, BEAT * 1.4, .07); keys(t0 + BEAT * 1.5, m, BEAT * 1.2, .05); keys(t0 + BEAT * 3, m + 0, BEAT * .9, .04); }
  // 贝斯：八分音符跳动
  [0, 1, 1.5, 2, 3, 3.5].forEach(b => bass(t0 + b * BEAT, r + (b === 3.5 ? 12 : 0), BEAT * .45, .2));
  for (let b = 0; b < 4; b++) {
    kick(t0 + b * BEAT, b % 2 ? .45 : .6);
    if (b % 2) clap(t0 + b * BEAT, .22);
    noise(t0 + (b + .5) * BEAT, .06, 60, .1, .97, .3);
    noise(t0 + b * BEAT, .04, 80, .05, .97, -.3);
  }
  // 高音拨弦点缀：和弦音往上琶音
  for (let s = 0; s < 8; s++) keys(t0 + s * BEAT / 2, c[s % 4] + 12, BEAT * .3, .025);
}
// 刚进鼓那一下加个镲
noise(DROP, 1.5, 2.5, .12, .95);

// 音乐整体压低：duck 里每段 [开始, 结束, 压到多少]（播片子原声压到 22%，旁白压到 50%），重叠时取最低
const duckAt = x => {
  let g = 1;
  for (const [a, b, lv = .22] of duck) { const k = Math.min(1, Math.max(0, Math.min((x - a + .3) / .3, (b + .3 - x) / .3))); g = Math.min(g, 1 - (1 - lv) * k); }
  return g;
};
for (let i = 0; i < N; i++) { const g = duckAt(i / SR); L[i] *= g; R[i] *= g; }

// ---------------- 音效 ----------------
const sfx = {
  click(t) { noise(t, .03, 220, .5, .5); for (let k = 0; k < .02 * SR; k++) add(Math.floor(t * SR) + k, Math.sin(2 * Math.PI * 2200 * k / SR) * Math.exp(-k / SR * 300) * .25); },
  key(t) { kick(t, .25); noise(t, .05, 120, .35, .6); },
  tap(t) { noise(t, .025, 300, .12, .6); },   // 轻轻的打字声
  whoosh(t) {   // 噪声，音量先升后降，像东西划过
    const i0 = Math.floor(t * SR), n = Math.floor(.5 * SR); let lp = 0;
    for (let k = 0; k < n; k++) { const x = k / n, a = .04 + .5 * Math.sin(Math.PI * x); lp += (rnd() - lp) * a; add(i0 + k, lp * Math.sin(Math.PI * x) * .22, (x - .5) * .8); }
  },
  pop(t) { const i0 = Math.floor(t * SR); let ph = 0; for (let k = 0; k < .12 * SR; k++) { const x = k / SR; ph += 2 * Math.PI * (300 + 900 * Math.exp(-x * 40)) / SR; add(i0 + k, Math.sin(ph) * Math.exp(-x * 30) * .35); } },
  ding(t, notes) { notes.forEach((m, j) => { const i0 = Math.floor((t + j * .09) * SR); for (let k = 0; k < .9 * SR; k++) { const x = k / SR; add(i0 + k, (Math.sin(2 * Math.PI * hz(m) * x) + .3 * Math.sin(2 * Math.PI * hz(m) * 2.76 * x) * Math.exp(-x * 8)) * Math.exp(-x * 5) * .18); } }); },
  recstart(t) { sfx.ding(t, [84, 91]); },
  recstop(t) { sfx.ding(t, [91, 84]); },
};
for (const e of events) sfx[e.type]?.(e.t);

// ---------------- 收尾：限幅、淡出、写 WAV ----------------
const fade = Math.floor(1.2 * SR);
let peak = 0;
for (let i = 0; i < N; i++) { L[i] = Math.tanh(L[i] * 1.1); R[i] = Math.tanh(R[i] * 1.1); peak = Math.max(peak, Math.abs(L[i]), Math.abs(R[i])); }
const g = .89 / peak;
const buf = Buffer.alloc(44 + N * 4);
buf.write('RIFF', 0); buf.writeUInt32LE(36 + N * 4, 4); buf.write('WAVEfmt ', 8);
buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(2, 22); buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 4, 28);
buf.writeUInt16LE(4, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(N * 4, 40);
for (let i = 0; i < N; i++) {
  const f = i > N - fade ? (N - i) / fade : 1;
  buf.writeInt16LE(Math.round(Math.max(-1, Math.min(1, L[i] * g * f)) * 32767), 44 + i * 4);
  buf.writeInt16LE(Math.round(Math.max(-1, Math.min(1, R[i] * g * f)) * 32767), 46 + i * 4);
}
writeFileSync(OUT, buf);
console.log('音乐+音效：', OUT, `${end}s, ${events.length} 个音效`);
