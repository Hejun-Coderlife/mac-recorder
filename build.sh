#!/bin/sh
# 编译成 录屏.app（只要命令行开发工具，不用 Xcode），装到 /Applications
set -e
cd "$(dirname "$0")"
APP="build/录屏.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swiftc -O -parse-as-library -swift-version 5 -target arm64-apple-macos15.0 \
  -framework ScreenCaptureKit -framework AVFoundation -framework Carbon -framework CoreImage -framework VideoToolbox -framework MediaToolbox -framework Speech -framework Metal \
  Recorder.swift Studio.swift StudioEditor.swift Captions.swift Denoise.swift Keys.swift Recents.swift Camera.swift Overlays.swift OverlayEditor.swift Music.swift SpeedSuggest.swift EditorExtras.swift Pieces.swift Control.swift Speak.swift AIConnect.swift TTSEngines.swift DTest.swift Updater.swift -o "$APP/Contents/MacOS/Recorder"
cp AppIcon.icns "$APP/Contents/Resources/"
# 本机 AI 配音（sherpa-onnx，Apache-2.0；onnxruntime，MIT）：程序打进 App，声音模型第一次用时再下载
if [ ! -f vendor/sherpa/sherpa-onnx-offline-tts ]; then
  mkdir -p vendor/sherpa /tmp/sherpa-dl
  curl -sSL -o /tmp/sherpa-dl/b.tar.bz2 https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.1/sherpa-onnx-v2.0.1-osx-arm64-shared.tar.bz2
  tar xjf /tmp/sherpa-dl/b.tar.bz2 -C /tmp/sherpa-dl
  cp /tmp/sherpa-dl/sherpa-onnx-v2.0.1-osx-arm64-shared/bin/sherpa-onnx-offline-tts /tmp/sherpa-dl/sherpa-onnx-v2.0.1-osx-arm64-shared/lib/libonnxruntime.dylib vendor/sherpa/
fi
mkdir -p "$APP/Contents/Helpers/tts"
cp vendor/sherpa/sherpa-onnx-offline-tts vendor/sherpa/libonnxruntime.dylib "$APP/Contents/Helpers/tts/"
rm -rf "$APP/Contents/Resources/keys"; mkdir -p "$APP/Contents/Resources/keys"; cp sounds/keys/*.wav "$APP/Contents/Resources/keys/"   # 打字声（真录音，来源见 sounds/keys/README.md）   # 图标原稿是 tools/icon.svg（1024 画布）
# App 名字和系统菜单（退出、窗口…）跟随系统语言：中文叫「录屏」，其他语言叫 Screen Recorder
mkdir -p "$APP/Contents/Resources/zh-Hans.lproj" "$APP/Contents/Resources/en.lproj"
printf '"CFBundleName" = "录屏";\n"CFBundleDisplayName" = "录屏";\n"NSMicrophoneUsageDescription" = "录屏时同时录下你的声音（需要时才打开）";\n"NSSpeechRecognitionUsageDescription" = "把视频里说的话在本机变成字幕";\n"NSCameraUsageDescription" = "录屏时把你的摄像头画面一起录下来（需要时才打开）";\n' > "$APP/Contents/Resources/zh-Hans.lproj/InfoPlist.strings"
printf '"CFBundleName" = "Screen Recorder";\n"CFBundleDisplayName" = "Screen Recorder";\n' > "$APP/Contents/Resources/en.lproj/InfoPlist.strings"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>录屏</string>
  <key>CFBundleDisplayName</key><string>录屏</string>
  <key>CFBundleIdentifier</key><string>com.hejun.screenrecorder</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string><string>zh-Hans</string></array>
  <key>LSHasLocalizedDisplayName</key><true/>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleExecutable</key><string>Recorder</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.5</string>
  <key>CFBundleVersion</key><string>6</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Records your voice along with the screen (only when turned on)</string>
  <key>NSCameraUsageDescription</key><string>Records your camera along with the screen (only when turned on)</string>
  <key>CFBundleDocumentTypes</key><array><dict>
    <key>CFBundleTypeName</key><string>Studio Footage Folder</string>
    <key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSHandlerRank</key><string>None</string>
    <key>LSItemContentTypes</key><array><string>public.folder</string></array>
  </dict></array>
  <key>NSSpeechRecognitionUsageDescription</key><string>Turns what you say in the video into captions, on this Mac</string>
</dict></plist>
PLIST
# 用固定的本机证书签名（钥匙串里的 Hejun Local Code Signing）：重新编译后系统仍认得是同一个程序，录屏权限不会失效；没有证书时退回临时签名
SIGN="-"; security find-identity -v -p codesigning | grep -q "Hejun Local Code Signing" && SIGN="Hejun Local Code Signing"
for f in "$APP/Contents/Helpers/tts/"*; do codesign --force -s "$SIGN" "$f"; done   # 里面的程序先签，App 整体校验才过
codesign --force --deep -s "$SIGN" "$APP"
mkdir -p /Applications
rm -rf "/Applications/录屏.app" "$HOME/Applications/录屏.app"
cp -R "$APP" /Applications/
echo "OK: /Applications/录屏.app"
