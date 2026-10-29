# 生成剪辑窗口的各个状态：每个状态一份 project.json → 离屏截图（中文、浅色、1440 宽）和/或导出成片
# 用法：python3 states.py [名字…]   （不写名字 = 全部）；素材 = demo/make.mjs 生成的待办 App 录像（不涉及用户屏幕）
import json, os, subprocess, sys, shutil, copy
SRC = '/tmp/rd/录屏 2026-10-29 15.20.36'          # raw.mp4 + track.json（由 make.mjs 生成后复制过来）
OUT = '/tmp/rd/states'
APP = '/Applications/录屏.app/Contents/MacOS/Recorder'
NAME = '录屏 2026-10-29 15.20.36'                # 剪辑窗口左上角显示的名字
G = [((0.36, 0.40, 0.96), (0.86, 0.43, 0.78)), ((0.56, 0.82, 0.98), (0.36, 0.48, 0.94)), ((1.00, 0.76, 0.62), (0.97, 0.46, 0.62)), ((0.58, 0.91, 0.78), (0.24, 0.60, 0.86))]
rgb = lambda c: {'r': c[0], 'g': c[1], 'b': c[2]}
TUT = {'aspect': '16:9', 'background': 'gradient', 'gradient0': rgb(G[1][0]), 'gradient1': rgb(G[1][1]), 'gradientAngle': 135}
DY = {'aspect': '9:16', 'background': 'gradient', 'gradient0': rgb(G[2][0]), 'gradient1': rgb(G[2][1]), 'gradientAngle': 160, 'cursorScale': 1.8, 'padding': 0.05}
SEGS = [dict(id='8C2E0F2A-0000-4000-8000-000000000001', start=6.0, end=16.9, zoom=2.0, mode='follow', fixedX=0.5, fixedY=0.5),
        dict(id='8C2E0F2A-0000-4000-8000-000000000002', start=20.9, end=29.9, zoom=2.0, mode='follow', fixedX=0.5, fixedY=0.5)]
def seg(**kw):
    s = copy.deepcopy(SEGS); s[0].update(kw); return s
STATES = {   # 名字: (look, segments, 要截图, 要导出)
    'tut':     (TUT, SEGS, True, True),
    'short':   (TUT, seg(end=13.6), False, True),
    'z3':      (TUT, seg(zoom=3.0), False, True),
    'fixed':   (TUT, seg(mode='fixed', fixedX=0.33, fixedY=0.42), False, True),
    'dy':      (DY, SEGS, True, True),
    'color':   ({**TUT, 'background': 'color', 'color': rgb((0.95, 0.93, 0.89))}, SEGS, True, True),
    'wall':    ({**TUT, 'background': 'wallpaper', 'wallpaper': '/System/Library/Desktop Pictures/Sonoma.heic'}, SEGS, True, True),
    'frame':   ({**TUT, 'padding': 0.12, 'cornerRadius': 32, 'shadow': 0.75, 'shadowBlur': 0.6}, SEGS, True, True),
    'cursor':  ({**TUT, 'cursorScale': 2.6, 'hideIdleCursor': True, 'hideAfter': 2.0, 'clickSound': True, 'clickStyle': 'soft'}, SEGS, True, True),
    'blur':    ({**TUT, 'blur': 'strong'}, SEGS, True, True),
    'blur0':   ({**TUT, 'blur': 'off'}, SEGS, False, True),
}
env0 = {**os.environ}
for k in [a for a in sys.argv[1:]] or list(STATES):
    look, segs, shot, export = STATES[k]
    d = os.path.join(OUT, k, NAME); shutil.rmtree(os.path.dirname(d), ignore_errors=True); os.makedirs(d)
    for f in ('raw.mp4', 'track.json'): os.symlink(os.path.join(SRC, f), os.path.join(d, f))
    json.dump({'look': look, 'segments': segs, 'segmentsEdited': True, 'trimStart': 0, 'trimEnd': 33}, open(os.path.join(d, 'project.json'), 'w'))
    base = [APP, '-AppleLanguages', '(zh-Hans)', '-ApplePersistenceIgnoreState', 'YES']
    if shot:
        r = subprocess.run(base, env={**env0, 'EDITOR': d, 'EDITORSHOT': f'{OUT}/{k}.png', 'EDITOR_WIDTH': '1440', 'EDITOR_APPEARANCE': 'light'}, capture_output=True, text=True)
        print(k, 'shot', 'OK' if 'EDITORSHOT OK' in r.stdout else r.stdout[-300:] + r.stderr[-300:])
    if export:
        # 截图会改写 project.json（打开时可能重排）：导出前重新写一遍
        json.dump({'look': look, 'segments': segs, 'segmentsEdited': True, 'trimStart': 0, 'trimEnd': 33}, open(os.path.join(d, 'project.json'), 'w'))
        r = subprocess.run(base, env={**env0, 'STUDIO': d, 'STUDIO_OUT': f'{OUT}/{k}.mp4'}, capture_output=True, text=True)
        print(k, 'export', [l for l in r.stdout.splitlines() if 'STUDIO' in l])
