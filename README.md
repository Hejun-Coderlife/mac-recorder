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

## 功能 Features

- 录整个屏幕、单个窗口，或者像系统截图（⌘⇧5）那样框选一块区域，拖八个点调大小
- 同时录电脑声音和麦克风（各一条音轨），可选是否显示鼠标
- 全局快捷键 **⌥⌘R** 开始 / 停止（游戏全屏、点不到按钮时也能用）
- 按屏幕原始像素录制，不缩放；颜色和苹果自带录屏一致
- 文件保存在「影片 / 录屏」

## 自己编译 Build from source

只需要命令行开发工具（`xcode-select --install`），不用 Xcode：

```sh
./build.sh      # 编译并装到 /Applications
./package.sh    # 打安装包 dist/ScreenRecorder.dmg
```
