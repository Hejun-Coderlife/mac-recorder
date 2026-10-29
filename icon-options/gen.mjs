/* 生成 5 个图标方案：每个一张 1024×1024 透明 PNG + 一张对比图 compare.png
   用法：node icon-options/gen.mjs        （用无界面 Chrome 把 SVG 画成 PNG） */
import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const CHROME = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';

// macOS 图标网格：1024 画布，内容 824×824，圆角约 185，下方留阴影
const shell = (id, bg, body) => `<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">
<defs>
  <filter id="sh${id}" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="12" stdDeviation="14" flood-opacity=".28"/></filter>
  <clipPath id="cl${id}"><rect x="100" y="100" width="824" height="824" rx="185"/></clipPath>
  ${bg.defs || ''}
</defs>
<rect x="100" y="100" width="824" height="824" rx="185" fill="${bg.fill}" filter="url(#sh${id})"/>
<g clip-path="url(#cl${id})">${bg.extra || ''}${body}
  <rect x="100" y="100" width="824" height="824" rx="185" fill="none" stroke="rgba(255,255,255,.25)" stroke-width="3"/>
</g></svg>`;

// 红色录制圆点（带光泽）
const dot = (cx, cy, r, id) => `
  <defs><radialGradient id="dg${id}" cx="38%" cy="32%" r="75%"><stop offset="0" stop-color="#ff8a80"/><stop offset=".55" stop-color="#ff3b30"/><stop offset="1" stop-color="#c81e14"/></radialGradient></defs>
  <circle cx="${cx}" cy="${cy}" r="${r}" fill="url(#dg${id})"/>
  <ellipse cx="${cx - r * .22}" cy="${cy - r * .42}" rx="${r * .5}" ry="${r * .26}" fill="#fff" opacity=".28"/>`;

const ICONS = [
  { name: '镜头', note: '浅色底 + 相机镜头，最像苹果自带 App', svg: shell(1,
    { fill: 'url(#bg1)', defs: '<linearGradient id="bg1" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff"/><stop offset="1" stop-color="#e3e3e8"/></linearGradient><radialGradient id="ln1" cx="50%" cy="40%" r="60%"><stop offset="0" stop-color="#3a3a3e"/><stop offset="1" stop-color="#121214"/></radialGradient>' },
    `<circle cx="512" cy="512" r="290" fill="#d1d1d6"/><circle cx="512" cy="512" r="270" fill="url(#ln1)"/>
     <circle cx="512" cy="512" r="228" fill="none" stroke="#48484c" stroke-width="6"/>
     ${[0, 60, 120, 180, 240, 300].map(a => `<path d="M512 512 L512 300" stroke="#2a2a2d" stroke-width="4" transform="rotate(${a + 15} 512 512)"/>`).join('')}
     ${dot(512, 512, 140, 1)}`) },
  { name: '彩色屏幕', note: '活泼的渐变底 + 白色屏幕框', svg: shell(2,
    { fill: 'url(#bg2)', defs: '<linearGradient id="bg2" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff7a45"/><stop offset=".5" stop-color="#ff2e63"/><stop offset="1" stop-color="#7b3dff"/></linearGradient>' },
    `<rect x="232" y="292" width="560" height="380" rx="52" fill="rgba(255,255,255,.14)" stroke="#fff" stroke-width="40"/>
     <path d="M432 762 h160" stroke="#fff" stroke-width="40" stroke-linecap="round"/>
     <circle cx="512" cy="482" r="92" fill="#fff"/>`) },
  { name: '显示器', note: '蓝色底 + 显示器，右上角红点 = 正在录', svg: shell(3,
    { fill: 'url(#bg3)', defs: '<linearGradient id="bg3" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#4aa3ff"/><stop offset="1" stop-color="#1757f0"/></linearGradient>' },
    `<rect x="212" y="262" width="600" height="410" rx="44" fill="#fff"/>
     <rect x="248" y="298" width="528" height="338" rx="18" fill="#e8f1ff"/>
     <path d="M462 672 L448 752 h128 L562 672z" fill="#fff"/><rect x="372" y="744" width="280" height="36" rx="18" fill="#fff"/>
     ${[[318, 60], [358, 110], [398, 80], [438, 140], [478, 70]].map(([x, h]) => `<rect x="${x}" y="${560 - h}" width="22" height="${h}" rx="11" fill="#1757f0" opacity=".85"/>`).join('')}
     ${dot(700, 380, 54, 3)}`) },
  { name: '声波', note: '深蓝底 + 红点和声波，突出能录声音', svg: shell(4,
    { fill: 'url(#bg4)', defs: '<linearGradient id="bg4" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2b3a67"/><stop offset="1" stop-color="#0d1226"/></linearGradient>' },
    `${dot(400, 512, 140, 4)}
     ${[[205, .95], [285, .62], [365, .32]].map(([r, o]) => `<path d="M${400 + r * .5} ${512 - r * .87} A ${r} ${r} 0 0 1 ${400 + r * .5} ${512 + r * .87}" fill="none" stroke="#fff" stroke-width="34" stroke-linecap="round" opacity="${o}"/>`).join('')}`) },
  { name: '红色按钮', note: '经典录制键：红底 + 白环白点，最好认', svg: shell(5,
    { fill: 'url(#bg5)', defs: '<linearGradient id="bg5" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ff6b5e"/><stop offset="1" stop-color="#e0241a"/></linearGradient>' },
    `<circle cx="512" cy="512" r="236" fill="none" stroke="#fff" stroke-width="46"/>
     <circle cx="512" cy="512" r="150" fill="#fff"/>
     <circle cx="512" cy="512" r="150" fill="none" stroke="rgba(0,0,0,.06)" stroke-width="6"/>`) },
];

const shot = (file, url, w, h) => execFileSync(CHROME, ['--headless=new', '--hide-scrollbars', '--default-background-color=00000000',
  `--window-size=${w},${h}`, '--force-device-scale-factor=1', `--screenshot=${file}`, url], { stdio: 'ignore' });

ICONS.forEach((ic, i) => {
  const svg = join(HERE, `icon${i + 1}.svg`);
  writeFileSync(svg, ic.svg);
  shot(join(HERE, `icon${i + 1}.png`), pathToFileURL(svg).href, 1024, 1024);
});

// 对比图：每个方案在浅色、深色背景上各一行（大图 + 程序坞 64 + 小图标 32）
const cell = (i, dark) => `<div class="c ${dark ? 'd' : ''}"><img src="icon${i + 1}.png" width="220"><div class="s"><img src="icon${i + 1}.png" width="64"><img src="icon${i + 1}.png" width="32"></div><b>${dark ? '' : `${i + 1}. ${ICONS[i].name}`}</b>${dark ? '' : `<i>${ICONS[i].note}</i>`}</div>`;
writeFileSync(join(HERE, 'compare.html'), `<!doctype html><meta charset="utf-8"><style>
body{margin:0;font-family:"PingFang SC",-apple-system,sans-serif;background:#fff}
.r{display:flex}.c{width:300px;padding:26px 0 18px;display:flex;flex-direction:column;align-items:center;background:#f5f5f7}
.c.d{background:#1e1e20;padding-top:22px}.s{display:flex;gap:22px;align-items:center;margin-top:6px}
b{font-size:22px;margin-top:10px;color:#1d1d1f}i{font-style:normal;font-size:15px;color:#6e6e73;margin-top:6px;text-align:center;padding:0 18px;line-height:1.4}
</style><div class="r">${ICONS.map((_, i) => cell(i, false)).join('')}</div><div class="r">${ICONS.map((_, i) => cell(i, true)).join('')}</div>`);
shot(join(HERE, 'compare.png'), pathToFileURL(join(HERE, 'compare.html')).href, 1500, 760);
console.log('done');
