录屏 · Screen Recorder（Windows 版）

双击「录屏.exe」即可使用（Windows 10 / 11，64 位）。旁边的「运行库」文件夹是它要用的零件（录制引擎、ffmpeg 等），要和 录屏.exe 放在一起，别删。
- 录整个屏幕、单个窗口，或框选一块区域（拖八个点调大小，点「录制」直接开录，Esc 取消）
- 可同时录电脑声音和麦克风（混成一条音轨），可选是否显示鼠标
- 全局快捷键 Ctrl+Alt+R 开始 / 停止录屏
- 截图：Ctrl+Alt+S（或点「截图」），屏幕定格后拖一个框，松手就截好，已经放进剪贴板，直接 Ctrl+V 粘贴；Esc 或右键取消
- 截完 / 录完，右下角浮出缩略图（和 Mac 一样）：
  · 拖进微信、文件夹、剪辑软件 = 直接发过去 / 放过去
  · 单击 = 用默认程序打开；鼠标停在上面它就不消失，右上角 × = 不要了
  · 截图没动它，几秒后自动存到桌面；拖走了或点了 × 就不存
  · 录好的视频总是存在桌面（Studio 原始素材在「视频\录屏\Studio 素材」）
- Studio 效果：打开开关正常录，录完自动生成——点击、打字的地方镜头自动推近，鼠标更顺滑、点击有波纹，白色背景加圆角阴影，出 mp4。
  原始素材在「视频\录屏\Studio 素材」；生成要一会儿，状态栏有进度，做完右下角出缩略图

- 会自动检查更新：有新版本时窗口底部出现「有新版本 vX.Y · 更新」，点一下自动下载、装好、重新打开（也可以点「检查更新」手动查）

第一次打开如果出现「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」（程序没有代码签名）。

Double-click 录屏.exe (Windows 10/11, 64-bit). Keep the 运行库 folder (recording engine, ffmpeg, runtime DLLs) next to it.
Record a screen, a window or a selected area with system audio and/or microphone (mixed into one track).
Ctrl+Alt+R starts / stops recording; Ctrl+Alt+S takes a screenshot (drag a box; it's copied to the clipboard).
After a screenshot or recording a thumbnail floats in the bottom-right corner, like on a Mac: drag it into any app, click to open, hover to keep it, × to discard.
Screenshots you leave alone are saved to the Desktop; recordings always go to the Desktop.
Studio effects: auto-zoom on clicks and typing, smoothed cursor, click ripples, white background with rounded corners and shadow (mp4).
Checks for updates automatically; when a new version is out, click "Update" at the bottom of the window to download, install and reopen.
If SmartScreen warns on first launch, click "More info → Run anyway" (the app is not code-signed).

Recording engine: ScreenRecorderLib (MIT, https://github.com/sskodje/ScreenRecorderLib).
msvcp140 / vcruntime140 / concrt140 DLLs: Microsoft Visual C++ Redistributable, included app-locally.
运行库\ffmpeg.exe: FFmpeg (https://ffmpeg.org), Windows build by BtbN (https://github.com/BtbN/FFmpeg-Builds), GPL v3 — see ffmpeg-LICENSE.txt; source at https://github.com/FFmpeg/FFmpeg.
  Used only as a separate program to decode / encode video for Studio effects.
