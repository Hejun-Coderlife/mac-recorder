录屏 · Screen Recorder（Windows 版）

双击「录屏.exe」即可使用（Windows 10 / 11，64 位）。整个文件夹要放在一起，别只拷 exe。
- 录整个屏幕、单个窗口，或框选一块区域（拖八个点调大小，点「录制」直接开录，Esc 取消）
- 可同时录电脑声音和麦克风（混成一条音轨），可选是否显示鼠标
- 全局快捷键 Ctrl+Alt+R 开始 / 停止
- 文件保存在「视频\录屏」，录完自动用默认播放器打开
- Studio 效果：打开开关正常录，录完自动生成——点击、打字的地方镜头自动推近，鼠标更顺滑、点击有波纹，白色背景加圆角阴影，出 mp4。
  原始素材在「视频\录屏\Studio 素材」；生成要一会儿，状态栏有进度，做完自动打开

第一次打开如果出现「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」（程序没有代码签名）。

Double-click 录屏.exe (Windows 10/11, 64-bit). Keep all files in the same folder.
Record a screen, a window or a selected area with system audio and/or microphone (mixed into one track).
Ctrl+Alt+R starts / stops. Videos are saved to Videos\录屏.
Studio effects: auto-zoom on clicks and typing, smoothed cursor, click ripples, white background with rounded corners and shadow (mp4).
If SmartScreen warns on first launch, click "More info → Run anyway" (the app is not code-signed).

Recording engine: ScreenRecorderLib (MIT, https://github.com/sskodje/ScreenRecorderLib).
msvcp140 / vcruntime140 / concrt140 DLLs: Microsoft Visual C++ Redistributable, included app-locally.
ffmpeg.exe: FFmpeg (https://ffmpeg.org), Windows build by BtbN (https://github.com/BtbN/FFmpeg-Builds), GPL v3 — see ffmpeg-LICENSE.txt; source at https://github.com/FFmpeg/FFmpeg.
  Used only as a separate program to decode / encode video for Studio effects.
