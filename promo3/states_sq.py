# 补一个方形 1:1 成片（薄荷绿渐变），做法同 promo2/states.py
import json, os, subprocess, shutil
SRC='/tmp/rd/录屏 2026-10-29 15.20.36'; OUT='/tmp/rd/states'; NAME='录屏 2026-10-29 15.20.36'
APP='/Applications/录屏.app/Contents/MacOS/Recorder'
rgb=lambda c:{'r':c[0],'g':c[1],'b':c[2]}
SEGS=[dict(id='8C2E0F2A-0000-4000-8000-000000000001',start=6.0,end=16.9,zoom=2.0,mode='follow',fixedX=0.5,fixedY=0.5),
      dict(id='8C2E0F2A-0000-4000-8000-000000000002',start=20.9,end=29.9,zoom=2.0,mode='follow',fixedX=0.5,fixedY=0.5)]
look={'aspect':'1:1','background':'gradient','gradient0':rgb((0.58,0.91,0.78)),'gradient1':rgb((0.24,0.60,0.86)),'gradientAngle':135}
d=os.path.join(OUT,'sq',NAME); shutil.rmtree(os.path.dirname(d),ignore_errors=True); os.makedirs(d)
for f in ('raw.mp4','track.json'): os.symlink(os.path.join(SRC,f),os.path.join(d,f))
json.dump({'look':look,'segments':SEGS,'segmentsEdited':True,'trimStart':0,'trimEnd':33},open(os.path.join(d,'project.json'),'w'))
r=subprocess.run([APP,'-AppleLanguages','(zh-Hans)','-ApplePersistenceIgnoreState','YES'],env={**os.environ,'STUDIO':d,'STUDIO_OUT':f'{OUT}/sq.mp4'},capture_output=True,text=True)
print([l for l in r.stdout.splitlines() if 'STUDIO' in l])
