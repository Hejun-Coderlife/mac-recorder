using System;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    // 全局快捷键：录屏、截图各一个，主窗口里点一下就能改；存在 settings.txt（hotkeyRecord / hotkeyShot = 修饰键,键码）
    // 截图默认 Ctrl+Alt+X：Ctrl+Alt+S 被 QQ 录屏占着（用户真机上踩到），老设置里没有这两项就用新默认值
    struct Hotkey
    {
        public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;
        public uint Mods;
        public Keys Key;

        public Hotkey(uint mods, Keys key) { Mods = mods; Key = key; }

        public static readonly Hotkey DefaultRecord = new Hotkey(Ctrl | Alt, Keys.R);
        public static readonly Hotkey DefaultShot = new Hotkey(Ctrl | Alt, Keys.X);
        public const string RecordSetting = "hotkeyRecord", ShotSetting = "hotkeyShot";

        public static Hotkey Load(string setting, Hotkey def)
        {
            var p = Settings.GetString(setting, "").Split(',');
            if (p.Length == 2 && uint.TryParse(p[0], out var m) && int.TryParse(p[1], out var k) && m != 0 && k != 0) return new Hotkey(m, (Keys)k);
            return def;
        }

        public void Save(string setting) => Settings.SetString(setting, $"{Mods},{(int)Key}");

        public bool SameAs(Hotkey o) => Mods == o.Mods && Key == o.Key;

        public override string ToString()
        {
            var s = "";
            if ((Mods & Ctrl) != 0) s += "Ctrl+";
            if ((Mods & Alt) != 0) s += "Alt+";
            if ((Mods & Shift) != 0) s += "Shift+";
            if ((Mods & Win) != 0) s += "Win+";
            return s + KeyName(Key);
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num" + (k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Snapshot: return "PrtScn";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
                default: return k.ToString();
            }
        }

        // 只按了修饰键（还在等真正的键）
        public static bool IsModifierKey(Keys k) =>
            k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey ||
            k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu || k == Keys.LWin || k == Keys.RWin;
    }

    // 快捷键被占时右下角弹系统通知（窗口底部那行字可能被挡住、没人看见）
    static class Notice
    {
        public static string Last;   // 自测用：最后一次通知的内容

        public static void Show(Form owner, string title, string text)
        {
            Last = title + " " + text;
            var icon = new NotifyIcon { Icon = App.Icon, Text = Name, Visible = true };
            icon.BalloonTipClicked += (s, e) => { owner.Show(); owner.WindowState = FormWindowState.Normal; owner.Activate(); };
            icon.ShowBalloonTip(10000, title, text, ToolTipIcon.Warning);
            var t = new Timer { Interval = 15000 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); icon.Visible = false; icon.Dispose(); };
            t.Start();
        }
    }
}
