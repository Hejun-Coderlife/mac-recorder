// 主窗口给 AI 控制（Control.cs）用的口子：录没录、在不在忙、选哪个来源、开关、开录 / 停、打开剪辑窗口；
// 以及「AI 控制」链接（打开 AIConnect 窗口）
using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenRecorder
{
    sealed partial class MainForm
    {
        internal bool CtlRecording => session != null;
        internal bool CtlBusy => busy || counting;
        internal double CtlElapsed => session != null ? Math.Round((DateTime.Now - startedAt).TotalSeconds, 1) : 0;
        internal bool CtlStudio => studio.Checked;
        internal string CtlMessage => status.Text;
        internal string CtlLastFile => lastFile;
        internal string CtlLastProject;   // 最近一次 Studio 录制的素材文件夹（打开剪辑窗口时记下，同 Mac 版 lastProject）

        internal void CtlSet(bool? studioOn, bool? micOn, bool? systemOn)
        {
            if (studioOn is bool s) studio.Checked = s;
            if (micOn is bool m) mic.Checked = m;
            if (systemOn is bool y) sysAudio.Checked = y;
        }
        internal List<Source> CtlSources() { Refresh_(); return sources; }
        internal void CtlSelect(string id) { selectedId = id; Refresh_(); }
        internal void CtlToggle() => Toggle();
        internal void CtlOpen(string dir) => ShowEditor(dir);
    }
}
