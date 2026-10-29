# 功能介绍视频用的真实界面状态：用 /tmp/recapp5 里冻结的录屏.app（含 A–D 批全部新功能）离屏截图
# python3 shots.py [名字…]  → /tmp/p7/s/<名字>.png（2940 宽，中文浅色）
import json, os, subprocess, shutil, sys, copy
APP = '/tmp/recapp5/录屏.app/Contents/MacOS/Recorder'
SRC = '/tmp/rd/录屏 2026-10-29 15.20.36'; NAME = '录屏 2026-10-29 15.20.36'
OUT = '/tmp/p7/s'; os.makedirs(OUT, exist_ok=True)
rgb = lambda c: {'r': c[0], 'g': c[1], 'b': c[2]}
BLUE = (rgb((0.56, 0.82, 0.98)), rgb((0.36, 0.48, 0.94)))
PINK = (rgb((1.00, 0.76, 0.62)), rgb((0.97, 0.46, 0.62)))
MINT = (rgb((0.58, 0.91, 0.78)), rgb((0.24, 0.60, 0.86)))
DP = '/System/Library/Desktop Pictures/'
def look(**kw):
    d = {'aspect': '16:9', 'background': 'gradient', 'gradient0': BLUE[0], 'gradient1': BLUE[1], 'gradientAngle': 135, 'padding': 0.06}
    d.update(kw); return d
def seg(i, s, e, z=2.0, mode='follow', fx=0.5, fy=0.5, **kw):
    d = dict(id=f'8C2E0F2A-0000-4000-8000-00000000000{i}', start=s, end=e, zoom=z, mode=mode, fixedX=fx, fixedY=fy); d.update(kw); return d
SEGS = [seg(1, 6.0, 16.9), seg(2, 20.9, 29.9)]
def segs(**kw):
    s = copy.deepcopy(SEGS); s[0].update(kw); return s
DY = dict(aspect='9:16', gradient0=PINK[0], gradient1=PINK[1], gradientAngle=160, cursorScale=1.8, padding=0.05)
CAPS = [dict(id='C0000000-0000-4000-8000-000000000001', start=9.6, end=11.2, text='先在这里写下今天要做的事'),
        dict(id='C0000000-0000-4000-8000-000000000002', start=11.2, end=13.0, text='写完按回车就加进去了'),
        dict(id='C0000000-0000-4000-8000-000000000003', start=13.4, end=15.2, text='做完的事情点一下打勾')]
OPEN = 'preset,aspect,bg,frame'
FLAT = [seg(1, 9.0, 14.0, z=1.0), SEGS[1]]   # 截图那一刻（这段中点）不推近：预览是全景
S = {
  # 剪辑窗口总览 / 推近段
  'ov':      dict(),
  'z3':      dict(segs=segs(zoom=3.0)),
  'short':   dict(segs=segs(end=12.4)),
  'moved':   dict(segs=segs(start=8.0, end=18.9)),
  'added':   dict(segs=SEGS + [seg(3, 31.0, 33.0)]),
  'deleted': dict(segs=[SEGS[0]]),
  'fixed':   dict(segs=segs(mode='fixed', fx=0.78, fy=0.3)),
  'zspeed':  dict(open='zoom,cursor'),
  # 剪辑
  'trim':    dict(env={'EDITOR_AT_TRIM': '1'}),
  'cuts':    dict(env={'EDITOR_DEMOCUTS': '1'}),
  'suggest': dict(env={'EDITOR_MUSICDEMO': 'suggest'}),
  # 声音 / 字幕
  'audio':   dict(segs=FLAT, open='audio', raw='/tmp/p7/rawa/raw.mp4'),
  'audioN':  dict(segs=FLAT, open='audio', raw='/tmp/p7/rawa/raw.mp4', audio={'system': 0.6, 'mic': 1.4, 'denoise': 'medium'}),
  'voice':   dict(segs=FLAT, env={'EDITOR_VOICEDEMO': '1'}, open='audio'),
  'voicerec':dict(segs=FLAT, env={'EDITOR_VOICEREC': '1'}, open='audio'),
  'music':   dict(segs=FLAT, env={'EDITOR_MUSICDEMO': 'music'}, open='music'),
  'caption': dict(segs=FLAT, caps=True, open='caption'),
  # 样式
  'douyin':  dict(look=look(**DY)),
  'd11':     dict(look=look(**dict(DY, aspect='1:1'))),
  'colr':    dict(look=look(**dict(DY, aspect='16:9', background='color', color=rgb((0.96, 0.95, 0.93))))),
  'wallp':   dict(look=look(**dict(DY, aspect='16:9', background='wallpaper', wallpaper=DP + 'iMac Blue.heic'))),
  'mintF':   dict(look=look(aspect='16:9', gradient0=MINT[0], gradient1=MINT[1], padding=0.12, cornerRadius=32, shadow=0.85)),
  'cursor':  dict(look=look(aspect='16:9', gradient0=MINT[0], gradient1=MINT[1], padding=0.12, cornerRadius=32, shadow=0.85,
                            cursorScale=2.6, hideIdleCursor=True, clickSound=True, clickStyle='wood', cursorLoop=True), open='cursor'),
  'blur':    dict(look=look(aspect='16:9', gradient0=MINT[0], gradient1=MINT[1], padding=0.12, cornerRadius=32, shadow=0.85, blur='strong'), open='cursor,blur'),
  # 遮挡 / 标注 / 快捷键
  'redact':  dict(segs=FLAT, env={'EDITOR_OVERLAYDEMO': 'blur'}),
  'mosaic':  dict(segs=FLAT, env={'EDITOR_OVERLAYDEMO': 'mosaic'}),
  'arrow':   dict(segs=FLAT, env={'EDITOR_OVERLAYDEMO': 'arrow'}),
  'text':    dict(segs=FLAT, env={'EDITOR_OVERLAYDEMO': 'text'}),
  'keys':    dict(segs=FLAT, keys=True, look=look(showKeys=True, keysAt='bottomRight'), open='keys'),
}
def shot(name, width='1470'):
    st = S[name]
    d = os.path.join('/tmp/p7/proj_' + name, NAME); shutil.rmtree(os.path.dirname(d), ignore_errors=True); os.makedirs(d)
    os.symlink(st.get('raw', os.path.join(SRC, 'raw.mp4')), os.path.join(d, 'raw.mp4'))
    tr = json.load(open(os.path.join(SRC, 'track.json')))
    if st.get('keys'): tr['shortcuts'] = [{'t': 10.6, 'keys': ['⌘', 'N']}, {'t': 11.3, 'keys': ['⌘', '⇧', 'S']}]
    json.dump(tr, open(os.path.join(d, 'track.json'), 'w'))
    for f in os.listdir(SRC):
        if f.startswith('cursor-'): os.symlink(os.path.join(SRC, f), os.path.join(d, f))
    pj = {'look': st.get('look', look()), 'segments': st.get('segs', SEGS), 'segmentsEdited': True, 'trimStart': 0, 'trimEnd': 33}
    if st.get('caps'): pj['captions'] = CAPS
    if st.get('audio'): pj['audio'] = st['audio']
    json.dump(pj, open(os.path.join(d, 'project.json'), 'w'))
    env = {**os.environ, 'EDITOR': d, 'EDITORSHOT': f'{OUT}/{name}.png', 'EDITOR_WIDTH': width, 'EDITOR_APPEARANCE': 'light',
           'EDITOR_OPEN': st.get('open', OPEN), **st.get('env', {})}
    r = subprocess.run([APP, '-AppleLanguages', '(zh-Hans)', '-ApplePersistenceIgnoreState', 'YES'], env=env, capture_output=True, text=True, timeout=180)
    print(name, 'OK' if 'EDITORSHOT OK' in r.stdout else ('FAIL ' + r.stdout[-300:] + r.stderr[-300:]), flush=True)
for n in (sys.argv[1:] or S): shot(n)
