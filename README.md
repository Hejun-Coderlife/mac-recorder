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
- 会自动检查更新，有新版本窗口底部提示，点「更新」自动装好重开
- **Studio 效果**：打开开关正常录，录完自动出片——点击、打字处镜头自动推近、鼠标更顺滑、点击有波纹，**白色背景**加圆角和阴影，出普通 mp4（Mac 版是透明背景 .mov）。原始素材留在「视频 \ 录屏 \ Studio 素材」。生成优先用显卡：合成在显卡上画，编码用显卡编码器（NVIDIA / Intel / AMD / Windows 自带，自动挑能用的）；没有显卡时用 CPU，会慢不少。做完状态栏显示用时和用的显卡还是 CPU，详细记录在素材文件夹的 render.log
- 程序里带了 ffmpeg（Studio 效果读写视频用），所以 exe 有几十 MB

> **Windows:** run the single `ScreenRecorder.exe` (Windows 10/11 x64; no extra runtime needed). Records a screen, a window or a selected area with system audio and/or microphone mixed into one track; **Ctrl+Alt+R** starts / stops; **Ctrl+Alt+X** takes a screenshot straight to the clipboard (both shortcuts can be changed in the window; you get a notification if another app already uses one). A Mac-style floating thumbnail appears after each screenshot / recording (drag it into any app, click to open, × to discard); screenshots you leave alone and all recordings are saved to the Desktop. Checks for updates automatically. **Studio effects** work too: auto-zoom on clicks/typing, smoothed cursor, click ripples, **white background** with rounded corners and shadow, exported as a regular mp4 (the Mac version uses a transparent .mov). Rendering uses the GPU when there is one (Direct3D 11 compositing + NVENC / Quick Sync / AMF / Media Foundation hardware encoding, picked automatically), falling back to the CPU; the status bar shows how long it took and render.log in the footage folder has the details. The exe bundles ffmpeg for Studio rendering, hence a few dozen MB. If SmartScreen warns on first launch, click "More info → Run anyway".
>
> 源码在 `windows/`（C# WinForms，.NET Framework 4.8 + [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)；Studio 合成在 `windows/StudioCore.cs`（CPU）和 `windows/StudioGpu.cs`（显卡，[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) D3D11，MIT），读写视频用 [BtbN 的 FFmpeg Windows 版](https://github.com/BtbN/FFmpeg-Builds)，GPL v3，作为单独的程序调用；许可证随程序解到 `%LOCALAPPDATA%\录屏\runtime\…\ffmpeg-LICENSE.txt`，源码 https://github.com/FFmpeg/FFmpeg；VC++ 运行库 dll 随程序带上），GitHub Actions 自动编译打包并真录几秒自测（含模拟点击、打字的 Studio 自测）。Source in `windows/`, built and self-tested by GitHub Actions.

## 功能 Features

- 录整个屏幕、单个窗口，或者像系统截图（⌘⇧5）那样框选一块区域，拖八个点调大小
- 同时录电脑声音和麦克风（各一条音轨），可选是否显示鼠标
- 全局快捷键 **⌥⌘R** 开始 / 停止（游戏全屏、点不到按钮时也能用）
- 按屏幕原始像素录制，不缩放；颜色和苹果自带录屏一致
- 文件保存在「影片 / 录屏」
- **Studio 效果**（像 Screen Studio）：打开开关后正常录就行，录完自动出片——点击、打字的地方镜头自动推近，鼠标轨迹更顺滑、点击有波纹，背景透明（存成带透明通道的 .mov，放进剪辑软件可以叠在任意画面上），带圆角和阴影。原始素材留在「影片 / 录屏 / Studio 素材」

  **Studio effects** (Screen Studio–style): record as usual and get a polished video automatically — auto-zoom on clicks and typing, smoothed cursor with click ripples, transparent background (saved as HEVC-with-alpha .mov, ready to overlay in any editor) with rounded corners and shadow.

## 自己编译 Build from source

只需要命令行开发工具（`xcode-select --install`），不用 Xcode：

```sh
./build.sh      # 编译并装到 /Applications
./package.sh    # 打安装包 dist/ScreenRecorder.dmg
```
