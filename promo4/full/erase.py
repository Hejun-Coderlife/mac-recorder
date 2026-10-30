# 剪辑窗口底图：tut 真截图，把会动的部分抹成背景色（网页里按原位置重画）；右侧样式面板各状态单独裁出来
from PIL import Image, ImageDraw
import numpy as np, os
S = '/tmp/p5/s'; O = 'img'
im = Image.open(f'{S}/tut.png').convert('RGB'); d = ImageDraw.Draw(im)
W = (255, 255, 255)
d.rectangle((20, 112, 2318, 1136), fill=W)            # 预览区（自己画）
d.rectangle((1120, 1176, 1460, 1256), fill=W)          # 播放键 + 时间
d.rectangle((630, 1186, 830, 1252), fill=W)            # 撤销 / 重做（自己画，能变亮）
d.rectangle((2018, 1196, 2222, 1246), fill=W)          # 时间轴缩放滑块
d.rectangle((96, 1298, 302, 1362), fill=W)             # 推近段的起止时间
d.rectangle((400, 1298, 845, 1362), fill=W)            # 放大滑块 + 数值
d.rectangle((916, 1298, 1228, 1362), fill=W)           # 跟着鼠标 / 固定位置
d.rectangle((18, 1371, 2322, 1618), fill=W)            # 时间轴内容（底线 1619 保留）
im.crop((0, 0, 2330, 1638)).save(f'{O}/ed_left.png')
im.crop((2330, 0, 2940, 90)).save(f'{O}/ed_topright.png')
for n in ['tut', 'douyin', 'd11', 'd43', 'd169', 'colr', 'wallp', 'img', 'mint', 'mintF', 'cur0', 'cur1', 'cur2', 'cur3', 'cur3b', 'blrb']:
    Image.open(f'{S}/{n}.png').convert('RGB').crop((2330, 90, 2940, 1638)).save(f'{O}/pn_{n}.png')
# 滚动用的长面板：cur3 到「声音」为止 + cur3b 从「声音」往下（blrb 同理）
def find(img, tpl):
    a = np.asarray(img.convert('L')).astype(float); t = np.asarray(tpl.convert('L')).astype(float)
    best = None
    for y in range(0, a.shape[0] - t.shape[0]):
        e = ((a[y:y + t.shape[0]] - t) ** 2).mean()
        if best is None or e < best[0]: best = (e, y)
    return best
c3 = Image.open(f'{O}/pn_cur3.png'); c3b = Image.open(f'{O}/pn_cur3b.png'); bb = Image.open(f'{O}/pn_blrb.png')
# cur3b 里「点击音效」下拉那一行（两张都有），用它对齐
for y in range(c3b.height):
    pass
tpl = c3b.crop((0, 520, 610, 600))   # 大致是 木头 下拉 + 声音 标题那一段
e, y3 = find(c3, tpl)
print('align err', round(e, 1), 'cur3b y520 ↔ cur3 y', y3)
off = y3 - 520
for name, src in (('tall_cur3', c3b), ('tall_blrb', bb)):
    h = max(c3.height, src.height + off)
    out = Image.new('RGB', (610, h), W); out.paste(c3.crop((0, 0, 610, y3)), (0, 0)); out.paste(src.crop((0, 520, 610, src.height)), (0, y3))
    out.save(f'{O}/{name}.png'); print(name, out.size)
