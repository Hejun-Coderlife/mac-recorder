# Mac → Windows 画面对照包

给 Windows 版补「W6」画面效果用：Mac 版已经做好的样子在 `mac/` 里，Windows 用**同一份素材 + 同一份工程**渲染**同一秒**，并排对比，直到看起来一样。

## 里面有什么

| 路径 | 是什么 |
|---|---|
| `footage/` | 测试素材（10 秒 1920×1080 静止画面：彩条、网格、粗细中英文字，方便看模糊和折射）。`raw.mp4` + `track.json` + `cursor-0.png`，没有 `project.json`；`bg.png` 是画面原图 |
| `cases/<case>.json` | 每个情况的完整工程（Mac 版 `get_project` 原样导出）。用法：把 `footage/` 复制一份，把这个 json 存成里面的 `project.json` |
| `mac/<case>@<画面>.png` | Mac 版渲染的标准答案（1920 宽，成片画面：含背景、边距、圆角、阴影、鼠标） |
| `index.json` | 每张图对应的 case、素材时间（秒）、文件名，Windows 照这个表渲染 |
| `make_mac_reference.py` | 在 Mac 上重新生成上面这些（要开着录屏.app）。Mac 版效果改了就重跑，再推上来 |

## 画面安排（素材时间）

鼠标 6 秒前停在右下角 (1728, 972)，6 秒起停在 (1100, 560)，7.0 秒在那里点一下。没有推近段（`segmentsEdited: true`）。

| 秒 | 内容 | 对应图 |
|---|---|---|
| 0–1 | 文字标签 11 种：pill / glass / glassDark / dark / gradient / outline / sticker / bubble / neon / marker / plain（`textStyle`） | `overlays@text-labels` |
| 1–2 | 箭头 5 种 plain / double / curved / thick / dashed（`arrow`）；框、正圆（无 `oval`）、椭圆（`oval: true`）、高亮、半透明框（`opacity: 0.5`） | `overlays@arrows-shapes` |
| 2–3 | 框里遮挡：blur / mosaic / solid / frosted | `overlays@redact-inside` |
| 3–4 | 聚光灯 spotlight（框外变暗） | `overlays@spotlight` |
| 4–5 | 聚焦 focus（框外模糊） | `overlays@focus` |
| 5–6 | 字幕，底样式 glass / dark / light / none 各一个 case | `caption-<box>@caption` |
| 7.0 | 点击波纹 ring / glass / double / pulse / glow / focus 各一个 case，点击后 0.10 / 0.25 / 0.40 秒各一张 | `ripple-<style>@t0.10` … |

## Mac 版怎么画的（照着移植，别凭感觉）

坐标：标注、遮挡的 x/y/w/h 是原画面比例、左上为原点；`unit` = 原画面「点」到当前画布像素的倍数。Core Image 是左下原点，移植时注意 y 翻转。

**玻璃透镜**（玻璃文字标签、玻璃字幕共用，必须一模一样）——`Overlays.swift` 的 `lens()`、`lensKernel`、`drawGlassChrome()`（约 145–205 行）：
- 圆角矩形距离场：`d` = 点到边的距离（里面为正）；`edge = 短边 × 0.5`，`depth = 短边 × 0.4`
- 取样位置 = `p + n × depth × t²`，`t = clamp(1 − d / edge)`，`n` = 指向外的法线 → 中间不变形，越靠边越往外取，背景线条在边上弯过去
- 之后：饱和度 ×1.15，亮度 +0.035（深色玻璃 −0.06）；圆角遮罩边缘高斯 σ = max(0.4, 短边 × 0.006)
- 体积感 `drawGlassChrome`（px = 字号像素）：浮起阴影 offset −0.16px、blur 1.0px、黑 20%（深色 32%）；底色 白 5%（深色 灰 0.06 × 30%）；边缘内阴影 黑 16%（深 35%）blur 0.35px；上沿柔光 白 75%（深 35%）blur 0.12px offset −0.06px。没有描边线
- 字颜色：glass / marker 用深灰 (0.11, 0.11, 0.12)，glassDark / gradient 用白（`Overlays.swift` 约 255–265 行）

**遮挡**——`Overlays.swift` 的 `redact()`（约 207–240 行），k = strength（0–1）：
- blur：高斯 σ = (3 + 22k) × unit
- mosaic：格子 = max(2, (6 + 30k) × unit)，格子从框左下角对齐
- solid：纯色
- frosted：高斯 σ = (8 + 26k) × unit → 饱和度 ×1.15、亮度 +0.04 → 上面蒙白 (0.28 + 0.2k)
- spotlight：框外蒙黑 (0.45 + 0.45k)，框里原样
- focus：框外高斯 σ = (4 + 22k) × unit，框里原样
- 圆角 = min(corner × unit, 短边 / 2)

**文字标签 11 种**——`Overlays.swift` 的 `textBox()`、`draw()`（约 241–430 行，每种一个 `case`）；编辑器里的小预览在 `OverlayEditor.swift` 约 636–646 行。

**箭头 5 种**——`Overlays.swift` `draw()` 里箭头部分（约 290–360 行）：curved 是二次曲线往外鼓、分 48 段画；dashed 虚线 `[线宽 × 2.2, 线宽 × 1.8]`；double 两头都有箭头；thick 是实心粗箭头。

**字幕玻璃底**——`Captions.swift` 约 110–175 行：透镜调 `OverlayLayer.lens(…, radius: 高 / 2, strength: 1)`，外框留白 `glassMargin` = 字号 × 0.9。

**点击波纹**——`Studio.swift` 约 1008–1045 行（动画）和 `drawRipple()`（约 1124 行，图案）。u = 经过时间 / 时长，e = 1 − (1 − u)³：
- 时长：focus 0.7 秒、double 0.65、glass 0.6、其他 0.5
- ring：半径 6 + 22e 点、透明度 1 − u；double：两圈 6 + 24e，第二圈晚 0.15 秒
- pulse：实心圆 10 + 14e、0.9(1 − u)；glow：暖黄光晕 14 + 16e、0.85(1 − u)；focus：细蓝圈从 46 收到 16、透明度 min(1, 4u)(1 − u)
- glass：没有图案，直接在画面上做两层凸 / 凹透镜（`rippleWarp`：外圈 bump +0.55a、里圈 −0.45a），r = (6 + 38e)k、宽 w = (10 − 3u)k、a = 1 − u，k = 每点像素 × 鼠标大小 / 1.5，再叠一圈 16% 白的亮边

## 对比的规矩

- 字体不同（Mac 苹方 / Windows 微软雅黑），字形对不上是正常的；比的是**样式**：底的形状、颜色、透明度、阴影、折射位置和强弱、线条粗细、动画到这一刻该多大
- 每张都出一张「左 Mac | 右 Windows」并排图给用户看；也可以算一下差值，但最后以人眼为准
- Windows 渲染的图放 `win/`，文件名和 `mac/` 一样；`win/` 和并排图不进仓库（`.gitignore`），GitHub Actions 的「W6 - 画面对照」一步把它们打包成附件 `reftest-mac-vs-windows`（`win/`、显卡版 `gpu/`、并排图 `side/`、差值 `ref_test.txt`）
- 本机跑：`REFTEST=这个目录 STUDIO_COMPOSE=gpu 录屏.exe`，结果在 `%TEMP%ef_test.txt`，每张按类有差值门槛（`SelfTestRef.Limit`）
