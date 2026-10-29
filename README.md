# 录屏 · Screen Recorder

<img src="docs/icon.png" width="96" align="right">

一个很小的 Mac 录屏工具：选整个屏幕、某个窗口或框一块区域，连同电脑播放的声音（可选麦克风）一起录成 MP4。

A tiny macOS screen recorder: record a whole screen, a single window or a selected area, together with system audio (and optionally your microphone), straight to MP4. The interface follows your system language (Chinese / English).

## 下载 Download

到 [Releases](../../releases/latest) 下载 `ScreenRecorder.dmg`，打开后把「录屏」拖进「应用程序」。

**要求：** Apple 芯片（M1 及以后）的 Mac，macOS 15 或更新。

**第一次打开会被系统拦下**（这个 App 没有经过苹果公证）：
1. 双击打开，看到提示后点「完成」或「取消」；
2. 打开 系统设置 → 隐私与安全性，往下找到「已阻止打开"录屏"」，点「仍要打开」；
3. 第一次录制时，按提示在 系统设置 → 隐私与安全性 → 屏幕与系统录音 里打开「录屏」，然后退出再重新打开。

> First launch is blocked by Gatekeeper because the app isn't notarized: open it once, then go to System Settings → Privacy & Security and click "Open Anyway". Requires an Apple silicon Mac with macOS 15+.

## Windows 版 Windows

Windows 10 / 11（64 位）可用：在 Releases 下载 `ScreenRecorder.exe`，**只有这一个文件**，放哪都行（比如改名叫「录屏.exe」放进 D:\软件），双击就用，不用安装、不用另装别的。第一次打开会把录制引擎和 ffmpeg 解到 `%LOCALAPPDATA%\录屏`（只解一次）。第一次打开如果出现「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」。

Windows: download the single `ScreenRecorder.exe` from Releases and double-click it — no installer, nothing else to install. On first launch it unpacks its recording engine and ffmpeg to `%LOCALAPPDATA%\录屏` once. If SmartScreen warns, click "More info → Run anyway" (not code-signed).

- 录整个屏幕、单个窗口，或框选一块区域（遮罩上拖八个点调大小，点「录制」直接开录，Esc 取消）
- 同时录电脑声音和麦克风（混成一条音轨），可选是否显示鼠标
- 全局快捷键 **Ctrl+Alt+R** 开始 / 停止；录屏窗口本身不会被录进画面
- **截图**：**Ctrl+Alt+X**（两个快捷键都能在窗口里点一下改；被 QQ 之类占用时右下角会提示），屏幕定格后拖框，松手就进剪贴板，直接 Ctrl+V 粘贴
- 截完 / 录完右下角浮出**缩略图**（跟 Mac 一样）：拖进微信、文件夹、剪辑软件就是发过去；单击打开；悬停不消失，× 不要了。截图没动它就自动存到**桌面**，录好的视频也存在桌面
- **后台静默更新**：开着的时候在后台查新版、悄悄下载好，退出程序时自动换上，下次打开就是新的（底部会说一句「已更新到 vX」）；正在录 / 生成时不动它；想马上用就点底部的「现在重启」
- **开录倒计时**（关 / 3 秒 / 5 秒，屏幕中间大号数字，不会被录进去，Esc 取消）；**录制时隐藏桌面图标**（录完自动恢复，程序意外退出的下次打开也会恢复）
- **Studio 效果 + 剪辑窗口**（跟 Mac 版一样，像 Screen Studio）：打开开关正常录，录完打开剪辑窗口——
  - 上面实时预览，中间播放栏（空格播放 / 暂停，←→ 逐帧），下面时间轴：按点击、打字自动排好的推近段（推近时镜头跟着鼠标走），拖两头改长短、拖中间挪、空白处双击新加、Delete 删掉；选中后调放大倍数、选「跟着鼠标 / 固定位置」（固定位置在预览上点一下设定）；两端黄色「[ ]」掐头去尾；Ctrl+Z / Ctrl+Y 撤销重做；Ctrl+滚轮或触控板捏合缩放时间轴
  - 右边样式：画面比例（原始 / 16:9 / 9:16 / 1:1 / 4:3）、背景（纯色 / 渐变 / Windows 自带壁纸 / 自己的图片，默认白色）、边距圆角阴影、鼠标（大小、顺滑、不动自动隐藏、点击波纹、6 种点击音效）、运动模糊（只在镜头快速移动时有一点拖影）、预设（4 个内置 + 自己存，设为新录制默认）
  - 改动自动存进素材文件夹的 project.json（格式和 Mac 版一样，同一份素材两边都能打开）；点右上角「导出」出 mp4 到桌面；主窗口「打开 Studio 素材…」能重新打开以前录的接着改
  - 导出优先用显卡：合成在显卡上画，编码用显卡编码器（NVIDIA / Intel / AMD / Windows 自带，自动挑能用的）；没有显卡时用 CPU，会慢不少。做完显示用时和用的显卡还是 CPU，详细记录在素材文件夹的 render.log。Windows 版没有透明背景（出普通 mp4）
- 程序里带了 ffmpeg（Studio 效果读写视频用），所以 exe 有几十 MB

> **Windows:** run the single `ScreenRecorder.exe` (Windows 10/11 x64; no extra runtime needed). Records a screen, a window or a selected area with system audio and/or microphone mixed into one track; **Ctrl+Alt+R** starts / stops; **Ctrl+Alt+X** takes a screenshot straight to the clipboard (both shortcuts can be changed in the window; you get a notification if another app already uses one). A Mac-style floating thumbnail appears after each screenshot / recording (drag it into any app, click to open, × to discard); screenshots you leave alone and all recordings are saved to the Desktop. Updates itself silently in the background (downloads the new version and swaps it in when you quit; "Restart Now" applies it immediately). Optional countdown before recording and hiding desktop icons while recording. **Studio effects** with the same editor as the Mac version: after recording, an editor opens with a live preview, a timeline of auto-generated zooms (drag to resize / move, double-click to add, follow-cursor or fixed position, trim with the yellow brackets, undo / redo, Ctrl+scroll or pinch to zoom the timeline) and a style panel (aspect ratio, solid / gradient / wallpaper / image background, padding, corners, shadow, cursor size / smoothing / hide when idle / ripple / 6 click sounds, motion blur, presets). Projects are saved as project.json in the footage folder, compatible with the Mac version; Export writes an mp4 to the Desktop (no transparent background on Windows). Rendering uses the GPU when there is one (Direct3D 11 compositing + NVENC / Quick Sync / AMF / Media Foundation hardware encoding, picked automatically), falling back to the CPU; the status bar shows how long it took and render.log in the footage folder has the details. The exe bundles ffmpeg for Studio rendering, hence a few dozen MB. If SmartScreen warns on first launch, click "More info → Run anyway".
>
> 源码在 `windows/`（C# WinForms，.NET Framework 4.8 + [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)；Studio 合成在 `windows/StudioCore.cs`（CPU）和 `windows/StudioGpu.cs`（显卡，[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) D3D11，MIT），读写视频用 [BtbN 的 FFmpeg Windows 版](https://github.com/BtbN/FFmpeg-Builds)，GPL v3，作为单独的程序调用；许可证随程序解到 `%LOCALAPPDATA%\录屏\runtime\…\ffmpeg-LICENSE.txt`，源码 https://github.com/FFmpeg/FFmpeg；VC++ 运行库 dll 随程序带上），GitHub Actions 自动编译打包并真录几秒自测（含模拟点击、打字的 Studio 自测）。Source in `windows/`, built and self-tested by GitHub Actions.

## 功能 Features

- 录整个屏幕、单个窗口，或者像系统截图（⌘⇧5）那样框选一块区域，拖八个点调大小
- 同时录电脑声音和麦克风（各一条音轨），可选是否显示鼠标
- 全局快捷键 **⌥⌘R** 开始 / 停止（游戏全屏、点不到按钮时也能用）
- 按屏幕原始像素录制，不缩放；颜色和苹果自带录屏一致
- 文件保存在「影片 / 录屏」
- **开录倒计时**：关 / 3 秒 / 5 秒，屏幕中间出大号数字（不会被录进去），倒计时中按 Esc 或再按一次快捷键取消
- **隐藏桌面图标**：录整个屏幕或区域时，画面里不出现桌面上的图标（只是不录那一层，桌面本身不动）
- **Studio 效果**（像 Screen Studio）：打开开关后正常录就行，录完打开**剪辑窗口**——点击、打字的地方已经自动排好推近段，推近时镜头跟着鼠标走，鼠标更顺滑、点击有波纹，背景透明（存成带透明通道的 .mov，放进剪辑软件可以叠在任意画面上），带圆角和阴影。原始素材留在「影片 / 录屏 / Studio 素材」
  - 时间轴上紫色块是推近段：拖两头改长短、拖中间挪位置，空白处双击（或点「推近」）在那里加一段，选中后按 Delete 删
  - 选中一段可以调放大倍数（1.2–4 倍），选「跟着鼠标」或「固定位置」（点「选位置」后在预览上点一下）
  - 两头的黄色竖条是修剪：拖进来就剪掉开头 / 结尾
  - 剪掉中间一段 / 快放：点「剪掉」在播放头处放一段红色斜纹块，拖两头盖住不要的部分；选中后可改成「加速」2× / 4× / 8×（蓝色斜纹）。预览直接跳过 / 快放，导出时剪掉的删掉、快放的按倍数压短，推近、点击声跟着对齐（工程文件里的 `cuts`；Windows 版还没有这个功能，用 Windows 打开会忽略，原样保留）
  - 录讲解：点「录讲解」（麦克风），默认先倒数 3 秒，然后从播放头开始边播边录你的讲解；再点一次、空格或 Esc 停。录好的讲解在时间轴最下面一条（带波形）：拖中间挪位置、拖两头剪掉开头结尾，选中后调音量、Delete 删，都能撤销。新录的和旧讲解重叠时，重叠部分以新录的为准。「声音」里有讲解总音量、「讲解时自动压低原声音」（约 -12 dB，默认开）、「录讲解前倒数 3 秒」「录讲解时原声音静音」（只影响录的时候，免得被麦克风录进去）。录音存在素材夹的 `voice/` 里（工程文件里的 `voiceovers`；Windows 版还没有，打开会原样保留）
  - 字幕：点「字幕」选识别哪段声音（录到的声音 / 讲解 / 两样都要），在本机识别普通话（macOS 26 用系统的 SpeechAnalyzer，第一次会下载识别资源；老系统用本机的 SFSpeechRecognizer），按标点和停顿断句，**一次只显示一行**（每行最多 16–18 字，按画面宽度自动定）。字幕在时间轴最下面一条：双击改字、拖两头改时间、拖中间挪、选中后可在播放头处拆开 / 和下一句合并 / 删除，空白处双击新加，都能撤销。样式在「字幕」分组：粗细、字号、颜色、描边、阴影、底色条、底部 / 顶部和离边距离；竖屏时字自动大一号，放底部时让开抖音等 App 底部的按钮。预览里实时显示，导出（含 GIF）烧进画面，也能「存 .srt」（成片时间）。字幕时间存的是素材时间：之后加剪掉、快放，字幕跟着它那段画面走，整句被剪掉的不出（工程文件里的 `captions`、`captionStyle`；Windows 版还没有，打开会原样保留）
    - 讲解和剪辑的关系：讲解「挂在它开头那一刻的画面上」——之后在它前面加 / 改剪掉、快放，讲解跟着那一刻的画面一起挪；讲解中间那段画面再被剪掉或快放，讲解本身不剪、不变速，照样按真实时间连着放完（录的时候本来就是看着剪过的预览说的）
  - 「声音」里调录到的电脑声音 / 麦克风各自的音量（0–200%，太响自动压住不爆音）、一键静音、点击声音量；预览和导出一样
  - 空格播放 / 暂停，← → 逐帧，⌘Z / ⌘⇧Z 撤销重做；改动自动保存，点「导出」出成片（可以反复导出）
  - 「导出」左边的设置：分辨率（原始 / 4K / 1440p / 1080p / 720p，默认 1440p）、帧率（60 / 30）、画质（高 / 标准 / 小文件）、格式（视频 / GIF），以及导出后复制到剪贴板（直接粘贴进微信、访达）。分辨率越小越快：同一段 35 秒的 Retina 录屏，原始大小约 26 秒、1440p 约 12 秒、1080p 约 7 秒、1080p 30 帧约 4 秒。导出中可以取消
  - 以前录的素材：主窗口「打开 Studio 素材…」选那个文件夹，接着改
  - 右边「样式」面板：
    - **画面比例**：原始 / 16:9 / 9:16（抖音竖屏）/ 1:1 / 4:3
    - **背景**：透明 / 纯色 / 渐变（6 套配色，也能自己挑两色和角度）/ 系统壁纸 / 自己的图片；边距、圆角、阴影深浅和虚化。透明背景导出 .mov，其他背景导出普通 .mp4
    - **鼠标**：大小（0.5–3 倍）、顺滑程度（可关）、不动几秒后自动隐藏、点击波纹、点击音效（导出时每次点击加一声轻响）
    - **运动模糊**：关 / 轻 / 中 / 强。只在镜头快速推拉、移动的那几帧沿方向轻微拖影，镜头停着时一点不糊
    - **预设**：把整套样式存起来、下拉切换；「设为默认」后新录的素材都用它（存在「~/资源库/Application Support/com.hejun.screenrecorder/presets.json」）

  **Studio effects** (Screen Studio–style): record as usual, then a **Studio editor** opens with zooms already placed on clicks and typing (the camera follows the cursor while zoomed). On the timeline, drag a zoom's ends to resize, drag its middle to move, double-click empty space to add one, Delete to remove; set scale (1.2–4×) and Follow cursor / Fixed position (click the preview to set it). Yellow bars trim the start / end. “Cut” marks a part to remove (red stripes) or, once selected, to speed up 2× / 4× / 8× (blue stripes); the preview skips / fast-forwards it and the export removes / compresses it with zooms and click sounds kept in sync (stored as `cuts` in project.json — not in the Windows version yet, which leaves them untouched). The Sound section sets computer-audio / microphone volume (0–200%, with a soft limiter), mute, and click volume, in both preview and export. **Voiceover**: click “Voiceover” (mic) to narrate while the video plays from the playhead (optional 3 s countdown; click again, Space or Esc to stop). Takes land on the bottom lane with a waveform — drag to move, drag the ends to trim, set volume, Delete; a new take replaces any overlapping part of older ones. Sound adds voiceover volume, “Lower recorded sound under voiceover” (about -12 dB, on by default) and “Mute recorded sound while recording”. A take is anchored to the frame where it starts: cuts / speed-ups added before it move it along with that frame, while cutting or speeding up footage underneath it doesn't cut or speed up the narration — it keeps playing in real time. Takes are stored in `voice/` as `voiceovers` in project.json (not in the Windows version yet, which leaves them untouched). **Captions**: click “Captions”, choose the source (recorded sound / voiceover / both) and Mandarin is transcribed on-device (SpeechAnalyzer on macOS 26, which downloads its model on first use; on-device SFSpeechRecognizer otherwise). Lines break at punctuation and pauses and show **one line at a time** (16–18 characters, by canvas width). On the caption lane: double-click to edit, drag ends / middle, split at the playhead, merge with the next, delete, double-click empty space to add — all undoable. The Captions style section sets weight, size, color, outline, shadow, background bar, top / bottom and margin (bigger and kept clear of app buttons in 9:16). Shown live in the preview, burned into exports (including GIF), and “Save .srt” writes output-time subtitles. Times are stored in footage time, so later cuts / speed-ups carry them along; fully cut lines are dropped (`captions` / `captionStyle` in project.json — not in the Windows version yet, which leaves them untouched). Space plays, ← → steps a frame, ⌘Z / ⌘⇧Z undo / redo. Edits save automatically. The **Style** panel sets the aspect ratio (original / 16:9 / 9:16 / 1:1 / 4:3), background (none, color, gradient, macOS wallpaper or your own image) with padding, corner radius and shadow, cursor size / smoothing / hide-when-idle / click ripple / click sound, and shutter-style motion blur (only on fast camera moves, so text stays sharp when still); styles can be saved as presets and one set as the default for new recordings. Export writes a transparent HEVC-with-alpha .mov, or an H.264 .mp4 for any other background. Export settings next to the button: resolution (original / 4K / 1440p / 1080p / 720p, default 1440p), 30 or 60 fps, quality, video or GIF, and copy-to-clipboard after export; smaller sizes export much faster (a 35 s Retina clip: ~26 s original, ~12 s at 1440p, ~7 s at 1080p). Exports can be cancelled. Reopen old footage with “Open Studio Footage…”. The main window also has a recording countdown (off / 3 / 5 s, Esc cancels) and “Hide desktop icons”.

## 自己编译 Build from source

只需要命令行开发工具（`xcode-select --install`），不用 Xcode：

```sh
./build.sh      # 编译并装到 /Applications
./package.sh    # 打安装包 dist/ScreenRecorder.dmg
```
