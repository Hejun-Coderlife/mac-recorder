# 样片用的样式面板状态：跟 base.png 同宽（EDITOR_WIDTH=1470）离屏截图，只取右侧样式面板拼到 base 上
import json, os, subprocess, shutil, copy
SRC = '/tmp/rd/录屏 2026-10-29 15.20.36'
OUT = '/tmp/p4s'
APP = '/Applications/录屏.app/Contents/MacOS/Recorder'
NAME = '录屏 2026-10-29 15.20.36'
rgb = lambda c: {'r': c[0], 'g': c[1], 'b': c[2]}
BLUE = (rgb((0.56, 0.82, 0.98)), rgb((0.36, 0.48, 0.94)))
PINK = (rgb((1.00, 0.76, 0.62)), rgb((0.97, 0.46, 0.62)))
MINT = (rgb((0.58, 0.91, 0.78)), rgb((0.24, 0.60, 0.86)))
def look(aspect, g): return {'aspect': aspect, 'background': 'gradient', 'gradient0': g[0], 'gradient1': g[1], 'gradientAngle': 135, 'padding': 0.0601}
SEGS = [dict(id='8C2E0F2A-0000-4000-8000-000000000001', start=6.0, end=16.9, zoom=2.0, mode='follow', fixedX=0.5, fixedY=0.5),
        dict(id='8C2E0F2A-0000-4000-8000-000000000002', start=20.9, end=29.9, zoom=2.0, mode='follow', fixedX=0.5, fixedY=0.5)]
STATES = {'A': look('16:9', BLUE), 'B': look('9:16', BLUE), 'C': look('9:16', PINK), 'D': look('9:16', MINT), 'E': look('1:1', MINT), 'F': look('16:9', MINT)}
for k, lk in STATES.items():
    d = os.path.join(OUT, k, NAME); shutil.rmtree(os.path.dirname(d), ignore_errors=True); os.makedirs(d)
    for f in ('raw.mp4', 'track.json'): os.symlink(os.path.join(SRC, f), os.path.join(d, f))
    json.dump({'look': lk, 'segments': SEGS, 'segmentsEdited': True, 'trimStart': 0, 'trimEnd': 33}, open(os.path.join(d, 'project.json'), 'w'))
    r = subprocess.run([APP, '-AppleLanguages', '(zh-Hans)', '-ApplePersistenceIgnoreState', 'YES'],
                       env={**os.environ, 'EDITOR': d, 'EDITORSHOT': f'{OUT}/{k}.png', 'EDITOR_WIDTH': '1470', 'EDITOR_APPEARANCE': 'light'}, capture_output=True, text=True)
    print(k, 'OK' if 'EDITORSHOT OK' in r.stdout else (r.stdout[-200:] + r.stderr[-200:]))
