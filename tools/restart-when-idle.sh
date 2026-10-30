#!/bin/zsh
export LC_ALL=en_US.UTF-8   # 路径里有中文：别让 ps 把它转义成 \M- 那种
# 装好新版后：等用户没在用录屏（录屏不在最前面，或 2 分钟没碰键盘鼠标），而且没在录制 / 导出 / 录讲解，
# 就退出录屏再在后台打开（不抢前台），原来开着的剪辑窗口也重新打开。最多等 6 小时。
APP=/Applications/录屏.app
BID=com.hejun.screenrecorder
EXE="$APP/Contents/MacOS/Recorder"
# 录屏本体在不在跑（不算 AI 用的 Recorder --mcp / ctl 小进程）
app_running() { LC_ALL=en_US.UTF-8 ps -axo args= | grep -F "$EXE" | grep -v -e "--mcp" -e " ctl " -e grep >/dev/null; }
for i in $(seq 1 4320); do
  if ! app_running; then echo "not running, nothing to do"; exit 0; fi
  idle=$(( $(ioreg -c IOHIDSystem | awk '/HIDIdleTime/ {print $NF; exit}') / 1000000000 ))
  front=$(osascript -e 'tell application "System Events" to get bundle identifier of first application process whose frontmost is true' 2>/dev/null)
  st=$("$EXE" ctl get_status 2>/dev/null)
  busy=$(echo "$st" | python3 -c 'import sys,json
try:
  j=json.load(sys.stdin)
  print("?" if "error" in j or "recording" not in j else ("1" if j.get("recording") or j.get("busy") else "0"))
except Exception: print("?")')
  # 问不到状态（旧版没有控制口）时更保守：必须 2 分钟没碰电脑，免得在录制中被重启
  if { [[ "$busy" == "0" ]] && { [[ "$front" != "$BID" ]] || (( idle >= 120 )); }; } || { [[ "$busy" == "?" ]] && (( idle >= 120 )); }; then
    projects=$(echo "$st" | python3 -c 'import sys,json
try:
  [print(p) for p in json.load(sys.stdin).get("open_projects",[])]
except Exception: pass')
    osascript -e "tell application id \"$BID\" to quit" 2>/dev/null
    for k in $(seq 1 50); do app_running || break; sleep 0.2; done
    open -g "$APP"
    sleep 3
    echo "$projects" | while read -r p; do [[ -n "$p" ]] && "$EXE" ctl open_project "{\"project\": \"$p\"}" >/dev/null 2>&1; done
    echo "restarted (front=$front idle=${idle}s, reopened: $(echo "$projects" | grep -c .))"
    exit 0
  fi
  sleep 5
done
echo "gave up waiting"
