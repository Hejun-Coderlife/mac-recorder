#!/usr/bin/env python3
# 在 Mac 上生成 Windows 对照包的「标准答案图」：
#   用 Mac 版录屏.app 的本机控制口，把 footage/ 这份测试素材按每个 case 改好工程、渲染几帧，存进 mac/<case>@<秒>.png，
#   并把每个 case 的完整工程存成 cases/<case>.json（Windows 那边拿同一份 json 渲染同样的秒数来对比）。
# 用法：先打开 /Applications/录屏.app，然后  python3 windows/tests/reference/make_mac_reference.py
import base64, json, os, shutil, sys, time, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
FOOT = os.path.join(HERE, "footage")
# 在副本上改，仓库里的 footage/ 保持干净（不写 project.json）；每次换新目录，免得剪辑窗口还留着上次读进去的 track.json
WORK = f"/tmp/recorder-reference-{int(time.time())}"
INFO = os.path.expanduser("~/Library/Application Support/com.hejun.screenrecorder/control.json")


def call(name, args):
    info = json.load(open(INFO))
    req = urllib.request.Request(f"http://127.0.0.1:{info['port']}/", data=json.dumps({"name": name, "arguments": args}).encode(),
                                 headers={"x-token": info["token"], "Content-Type": "application/json"})
    out = json.load(urllib.request.urlopen(req, timeout=120))
    if "error" in out:
        sys.exit(f"{name}: {out['error']}")
    return out


RED, ORANGE, YELLOW, GREEN, BLUE, PURPLE, WHITE, BLACK = (
    {"r": 1.0, "g": 0.27, "b": 0.23}, {"r": 1.0, "g": 0.62, "b": 0.04}, {"r": 1.0, "g": 0.84, "b": 0.04}, {"r": 0.2, "g": 0.78, "b": 0.35},
    {"r": 0.04, "g": 0.52, "b": 1.0}, {"r": 0.69, "g": 0.32, "b": 0.87}, {"r": 1, "g": 1, "b": 1}, {"r": 0.11, "g": 0.11, "b": 0.12})

# 时间安排（素材时间，秒）：每样东西 start 后 0.6 秒拍（淡入 0.25 秒、箭头画出 0.4 秒都已结束）
# 鼠标 6 秒前停在右下角、6 秒起停在 (1100, 560)，7.0 秒在那里点一下
# 0–1 文字标签 11 种 | 1–2 箭头 5 种 + 框 / 正圆 / 椭圆 / 高亮 | 2–3 框里遮挡 4 种 | 3–4 聚光灯 | 4–5 聚焦 | 5–6 字幕 | 7.0 点击波纹
TEXT_LOOKS = ["pill", "glass", "glassDark", "dark", "gradient", "outline", "sticker", "bubble", "neon", "marker", "plain"]
texts = []
for i, look in enumerate(TEXT_LOOKS):
    col, row = i % 4, i // 4
    texts.append({"kind": "text", "start": 0, "end": 1, "x": 0.04 + col * 0.24, "y": 0.2 + row * 0.25, "w": 0, "h": 0,
                  "text": f"{look} 标签", "fontSize": 40, "color": YELLOW if look == "neon" else [RED, BLUE, GREEN, PURPLE][i % 4], "textStyle": look, "drawOn": False})

ARROWS = ["plain", "double", "curved", "thick", "dashed"]
shapes = [{"kind": "arrow", "start": 1, "end": 2, "x": 0.06, "y": 0.14 + i * 0.16, "w": 0.3, "h": 0.06, "arrow": a,
           "color": [RED, ORANGE, BLUE, GREEN, PURPLE][i], "width": 6, "drawOn": False} for i, a in enumerate(ARROWS)]
shapes += [
    {"kind": "rect", "start": 1, "end": 2, "x": 0.45, "y": 0.1, "w": 0.18, "h": 0.22, "color": RED, "width": 5},
    {"kind": "ellipse", "start": 1, "end": 2, "x": 0.68, "y": 0.1, "w": 0.12, "h": 0.22, "color": BLUE, "width": 5},             # 没有 oval = 正圆
    {"kind": "ellipse", "start": 1, "end": 2, "x": 0.45, "y": 0.42, "w": 0.3, "h": 0.16, "color": GREEN, "width": 5, "oval": True},
    {"kind": "highlight", "start": 1, "end": 2, "x": 0.45, "y": 0.68, "w": 0.32, "h": 0.08, "color": YELLOW},
    {"kind": "rect", "start": 1, "end": 2, "x": 0.82, "y": 0.42, "w": 0.12, "h": 0.16, "color": WHITE, "width": 3, "opacity": 0.5},
]

REDS = [{"style": s, "start": 2, "end": 3, "x": 0.05 + i * 0.235, "y": 0.3, "w": 0.2, "h": 0.4, "strength": 0.6, "corner": 12, "color": BLACK}
        for i, s in enumerate(["blur", "mosaic", "solid", "frosted"])]
REDS += [{"style": "spotlight", "start": 3, "end": 4, "x": 0.35, "y": 0.3, "w": 0.3, "h": 0.35, "strength": 0.6, "corner": 16},
         {"style": "focus", "start": 4, "end": 5, "x": 0.35, "y": 0.3, "w": 0.3, "h": 0.35, "strength": 0.6, "corner": 16}]

CAPTIONS = [{"start": 5, "end": 6, "text": "玻璃字幕 Glass caption 测试"}]

BASE = {
    "segments": [], "segmentsEdited": True, "cuts": [], "splits": [], "keyMutes": [], "layouts": [], "voiceovers": [],
    "trimStart": 0, "trimEnd": 10,
    "look": {"aspect": "original", "background": "gradient", "padding": 0.06, "cornerRadius": 14, "shadow": 0.45, "shadowBlur": 0.33,
             "blur": "off", "ripple": True, "rippleStyle": "ring", "rippleWidth": 1, "cursorScale": 1.5, "smoothing": 0.7,
             "hideIdleCursor": False, "clickSound": False, "typeSound": False, "showKeys": False},
    "annotations": texts + shapes, "redactions": REDS, "captions": CAPTIONS,
    "captionStyle": {"show": True, "box": "glass", "weight": "semibold", "size": 0.05, "color": WHITE, "outline": True, "shadow": True,
                     "position": "bottom", "margin": 0.07},
}

OVERLAY_TIMES = {"text-labels": 0.6, "arrows-shapes": 1.6, "redact-inside": 2.6, "spotlight": 3.6, "focus": 4.6}


def merge(a, b):
    out = dict(a)
    for k, v in b.items():
        out[k] = merge(a[k], v) if isinstance(v, dict) and isinstance(a.get(k), dict) else v
    return out


CASES = []   # (case 名, 对 BASE 的改动, [(画面名, 秒)])
CASES.append(("overlays", {}, list(OVERLAY_TIMES.items())))
for box in ["glass", "dark", "light", "none"]:
    CASES.append((f"caption-{box}", {"captionStyle": {"box": box}}, [("caption", 5.6)]))
for rs in ["ring", "glass", "double", "pulse", "glow", "focus"]:
    CASES.append((f"ripple-{rs}", {"look": {"rippleStyle": rs}}, [("t0.10", 7.1), ("t0.25", 7.25), ("t0.40", 7.4)]))


def main():
    shutil.rmtree(WORK, ignore_errors=True)
    shutil.copytree(FOOT, WORK, ignore=shutil.ignore_patterns("project.json", "bg.png"))
    os.makedirs(os.path.join(HERE, "mac"), exist_ok=True)
    os.makedirs(os.path.join(HERE, "cases"), exist_ok=True)
    call("open_project", {"project": WORK})
    index = []
    for name, patch, shots in CASES:
        call("update_project", {"project": WORK, "patch": merge(BASE, patch)})
        proj = call("get_project", {"project": WORK})["project"]
        json.dump(proj, open(os.path.join(HERE, "cases", f"{name}.json"), "w"), ensure_ascii=False, indent=1, sort_keys=True)
        for shot, t in shots:
            png = base64.b64decode(call("render_frame", {"project": WORK, "time": t, "max_width": 1920})["image_base64"])
            fn = f"{name}@{shot}.png"
            open(os.path.join(HERE, "mac", fn), "wb").write(png)
            index.append({"case": name, "shot": shot, "time": t, "png": f"mac/{fn}"})
            print("✓", fn)
    json.dump(index, open(os.path.join(HERE, "index.json"), "w"), ensure_ascii=False, indent=1)


if __name__ == "__main__":
    main()
