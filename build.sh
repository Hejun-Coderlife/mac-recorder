#!/bin/sh
# 编译成 录屏.app（只要命令行开发工具，不用 Xcode），装到 ~/Applications
set -e
cd "$(dirname "$0")"
APP="build/录屏.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swiftc -O -parse-as-library -swift-version 5 -target arm64-apple-macos15.0 \
  -framework ScreenCaptureKit -framework AVFoundation -framework Carbon \
  Recorder.swift -o "$APP/Contents/MacOS/Recorder"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>录屏</string>
  <key>CFBundleDisplayName</key><string>录屏</string>
  <key>CFBundleIdentifier</key><string>com.hejun.screenrecorder</string>
  <key>CFBundleExecutable</key><string>Recorder</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>录屏时同时录下你的声音（需要时才打开）</string>
</dict></plist>
PLIST
# 用固定的本机证书签名（钥匙串里的 Hejun Local Code Signing）：重新编译后系统仍认得是同一个程序，录屏权限不会失效；没有证书时退回临时签名
if security find-identity -v -p codesigning | grep -q "Hejun Local Code Signing"; then codesign --force --deep -s "Hejun Local Code Signing" "$APP"; else codesign --force --deep -s - "$APP"; fi
mkdir -p "$HOME/Applications"
rm -rf "$HOME/Applications/录屏.app"
cp -R "$APP" "$HOME/Applications/"
echo "OK: $HOME/Applications/录屏.app"
