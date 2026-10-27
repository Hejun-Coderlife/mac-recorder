#!/bin/sh
# 打安装包：先编译，再做成 dist/录屏.dmg（打开后把「录屏」拖进「应用程序」）
set -e
cd "$(dirname "$0")"
./build.sh
rm -rf dist && mkdir -p dist/stage
cp -R "build/录屏.app" dist/stage/
ln -s /Applications dist/stage/Applications
hdiutil create -volname "录屏" -srcfolder dist/stage -fs HFS+ -format UDZO -ov "dist/ScreenRecorder.dmg" >/dev/null
rm -rf dist/stage
echo "OK: dist/ScreenRecorder.dmg"
