# 完整宣传片用的真实界面状态：用 /tmp/recapp 里冻结的一份录屏.app（避免别的助手中途重装导致前后界面不一致）离屏截图
# python shots.py [名字…]  → /tmp/p5/s/<名字>.png
import json, os, subprocess, shutil, sys
APP = '/tmp/recapp/录屏.app/Contents/MacOS/Recorder'
SRC = '/tmp/rd/录屏 2026-10-29 15.20.36'; NAME = '录屏 2026-10-29 15.20.36'
OUT = '/tmp/p5/s'; os.makedirs(OUT, exist_ok=True)
rgb = lambda c: {'r': c[0], 'g': c[1], 'b': c[2]}
BLUE = (rgb((0.56, 0.82, 0.98)), rgb((0.36, 0.48, 0.94)))
PINK = (rgb((1.00, 0.76, 0.62)), rgb((0.97, 0.46, 0.62)))
MINT = (rgb((0.58, 0.91, 0.78)), rgb((0.24, 0.60, 0.86)))
def look(**kw):
    d = {'aspect': '16:9', 'background': 'gradient', 'gradient0': BLUE[0], 'gradient1': BLUE[1], 'gradientAngle': 135, 'padding': 0.0601}
    d.update(kw); return d
SEGS = [dict(id='8C2E0F2A-0000-4000-8000-000000000001', start=6.0, end=16.9, zoom=2.0, mode='follow', fixedX=0.5, fixedY=0.5),
        dict(id='8C2E0F2A-0000-4000-8000-000000000002', start=20.9, end=29.9, zoom=2.0, mode='follow', fixedX=0.5, fixedY=0.5)]
PEACH = (rgb((1.00, 0.76, 0.62)), rgb((0.97, 0.46, 0.62)))
DP = '/System/Library/Desktop Pictures/'
DY = dict(aspect='9:16', gradient0=PINK[0], gradient1=PINK[1], gradientAngle=160, cursorScale=1.8, padding=0.05)
def dy(**kw): d = dict(DY); d.update(kw); return look(**d)
MINTL = dict(aspect='16:9', gradient0=MINT[0], gradient1=MINT[1])
FIN = dict(MINTL, padding=0.12, cornerRadius=32, shadow=0.85)
STATES = {
  'tut':    dict(look=look(padding=0.06)),
  'douyin': dict(look=dy()),
  'd11':    dict(look=dy(aspect='1:1')),
  'd43':    dict(look=dy(aspect='4:3')),
  'd169':   dict(look=dy(aspect='16:9')),
  'colr':   dict(look=dy(aspect='16:9', background='color', color=rgb((0.96, 0.95, 0.93)))),
  'wallp':  dict(look=dy(aspect='16:9', background='wallpaper', wallpaper=DP + 'iMac Blue.heic')),
  'img':    dict(look=dy(aspect='16:9', background='image', imagePath=DP + 'Sonoma.heic')),
  'mint':   dict(look=dy(**MINTL)),
  'mintF':  dict(look=dy(**FIN)),
  'cur0':   dict(look=dy(**FIN, **{'cursorScale': 2.6})),
  'cur1':   dict(look=dy(**FIN, **{'cursorScale': 2.6, 'hideIdleCursor': True})),
  'cur2':   dict(look=dy(**FIN, **{'cursorScale': 2.6, 'hideIdleCursor': True, 'clickSound': True})),
  'cur3':   dict(look=dy(**FIN, **{'cursorScale': 2.6, 'hideIdleCursor': True, 'clickSound': True, 'clickStyle': 'wood'})),
  'cur3b':  dict(look=dy(**FIN, **{'cursorScale': 2.6, 'hideIdleCursor': True, 'clickSound': True, 'clickStyle': 'wood'}), open='cursor,blur'),
  'blrb':   dict(look=dy(**FIN, **{'cursorScale': 2.6, 'hideIdleCursor': True, 'clickSound': True, 'clickStyle': 'wood', 'blur': 'strong'}), open='cursor,blur'),
  'blroff': dict(look=dy(**FIN, **{'cursorScale': 2.6, 'hideIdleCursor': True, 'clickSound': True, 'clickStyle': 'wood', 'blur': 'off'}), open='cursor,blur'),
}
def shot(name, width='1470'):
    st = STATES[name]
    lk = {k: v for k, v in st['look'].items() if not k.endswith('_')}
    d = os.path.join('/tmp/p5/proj_' + name, NAME); shutil.rmtree(os.path.dirname(d), ignore_errors=True); os.makedirs(d)
    for f in ('raw.mp4', 'track.json'): os.symlink(os.path.join(SRC, f), os.path.join(d, f))
    pj = {'look': lk, 'segments': st.get('segs', SEGS), 'segmentsEdited': not st.get('undo'), 'trimStart': 0, 'trimEnd': 33}
    json.dump(pj, open(os.path.join(d, 'project.json'), 'w'))
    env = {**os.environ, 'EDITOR': d, 'EDITORSHOT': f'{OUT}/{name}.png', 'EDITOR_WIDTH': width, 'EDITOR_APPEARANCE': 'light',
           'EDITOR_OPEN': st.get('open', 'preset,aspect,bg,frame,cursor,blur')}
    if st.get('undo'): env['EDITOR_UNDOCHECK'] = '1'
    r = subprocess.run([APP, '-AppleLanguages', '(zh-Hans)', '-ApplePersistenceIgnoreState', 'YES'], env=env, capture_output=True, text=True, timeout=120)
    print(name, 'OK' if 'EDITORSHOT OK' in r.stdout else (r.stdout[-300:] + r.stderr[-300:]))
for n in (sys.argv[1:] or STATES): shot(n)
