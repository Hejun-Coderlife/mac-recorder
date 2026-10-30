// W5 的核心自测（不碰界面，Mac 上的 .NET 8 小工具和 Windows 程序共用；用例照 Mac 版 FLOWTEST）：
//   Rules() Mac 新字段（splits、keyMutes、自定义比例、打字声、新遮挡 / 字幕底 / 标注样式）存了再读不丢；
//           分割 / 2× / 删掉后半截（跨段快放被切开、导出跳过、没有残留小片段）、恢复原速；可见时间合拢、跨剪刀拖动换算；
//           打字分阵、去掉 / 恢复一阵打字声、给一阵打字改速度
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ScreenRecorder
{
    static class StudioTestsW5
    {
        sealed class Report
        {
            public readonly StringBuilder Text = new StringBuilder();
            public bool Ok = true;
            public void Check(string name, bool cond, string info = "") { Text.AppendLine((cond ? "PASS " : "FAIL ") + name + (info.Length > 0 ? "  " + info : "")); Ok &= cond; }
            public string Done(string what) { Text.AppendLine(what + (Ok ? " OK" : " FAIL")); return Text.ToString(); }
        }
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static bool Near(double a, double b, double e = 0.01) => Math.Abs(a - b) < e;

        // Mac 版这两天写的工程（新字段都在）
        const string MacProject = @"{
  ""audio"": {""clicks"": 1, ""mic"": 1, ""system"": 1},
  ""annotations"": [{""id"": ""A1"", ""kind"": ""arrow"", ""start"": 1, ""end"": 3, ""x"": 0.2, ""y"": 0.7, ""w"": 0.2, ""h"": -0.2, ""color"": {""r"": 1, ""g"": 0.27, ""b"": 0.23}, ""width"": 5, ""text"": """", ""fontSize"": 28, ""pill"": true, ""drawOn"": true, ""arrow"": ""curved"", ""opacity"": 0.6},
                    {""id"": ""A2"", ""kind"": ""text"", ""start"": 2, ""end"": 4, ""x"": 0.5, ""y"": 0.5, ""w"": 0, ""h"": 0, ""color"": {""r"": 1, ""g"": 0.27, ""b"": 0.23}, ""width"": 5, ""text"": ""hello"", ""fontSize"": 40, ""pill"": true, ""drawOn"": false, ""textStyle"": ""glass""},
                    {""id"": ""A3"", ""kind"": ""ellipse"", ""start"": 2, ""end"": 4, ""x"": 0.1, ""y"": 0.1, ""w"": 0.2, ""h"": 0.3, ""color"": {""r"": 1, ""g"": 0, ""b"": 0}, ""width"": 5, ""text"": """", ""fontSize"": 28, ""pill"": true, ""drawOn"": true, ""oval"": true}],
  ""captionStyle"": {""box"": ""glass"", ""cx"": 0.4, ""cy"": 0.8, ""show"": true},
  ""captions"": [],
  ""cuts"": [{""id"": ""C1"", ""start"": 5, ""end"": 8, ""mode"": ""cut"", ""rate"": 4}],
  ""keyMutes"": [[6.5, 7.5]],
  ""look"": {""aspect"": ""custom"", ""aspectW"": 4, ""aspectH"": 5, ""zoomSpeed"": 2.2, ""typeSound"": true, ""typeStyle"": ""thock"", ""typeMuteFast"": true, ""rippleStyle"": ""glass"", ""rippleWidth"": 1.8, ""background"": ""gradient""},
  ""redactions"": [{""id"": ""R1"", ""start"": 1, ""end"": 2, ""x"": 0.1, ""y"": 0.1, ""w"": 0.2, ""h"": 0.2, ""style"": ""spotlight"", ""strength"": 0.6, ""color"": {""r"": 0.1, ""g"": 0.1, ""b"": 0.1}, ""corner"": 8},
                   {""id"": ""R2"", ""start"": 2, ""end"": 3, ""x"": 0.4, ""y"": 0.1, ""w"": 0.2, ""h"": 0.2, ""style"": ""frosted"", ""strength"": 0.6, ""color"": {""r"": 0.1, ""g"": 0.1, ""b"": 0.1}, ""corner"": 8}],
  ""segments"": [], ""segmentsEdited"": true,
  ""splits"": [2, 5],
  ""trimStart"": 0, ""trimEnd"": 14,
  ""voiceovers"": []
}";

        public static string Rules()
        {
            var r = new Report();
            const double D = 20;

            // 1) Mac 新字段：读进来、存回去不丢
            var mp = StudioProject.FromJson(MacProject);
            r.Check("custom aspect read", mp.Look.Aspect == "custom" && Near(mp.Look.Ratio ?? 0, 0.8), mp.Look.Aspect + " " + F(mp.Look.Ratio ?? 0));
            r.Check("zoomSpeed kept", Near(mp.Look.ZoomSpeed, 2.2));
            r.Check("splits / keyMutes read", mp.Splits.Count == 2 && mp.KeyMutes.Count == 1 && Near(mp.KeyMutes[0][0], 6.5));
            r.Check("new redaction styles kept", mp.Redactions.Select(x => x.Style).SequenceEqual(new[] { "spotlight", "frosted" }), string.Join(",", mp.Redactions.Select(x => x.Style)));
            r.Check("glass caption box kept", mp.CaptionStyle.Box == "glass");
            var back = StudioProject.FromJson(mp.ToJson());
            var o = Json.Parse(mp.ToJson()) as Dictionary<string, object>;
            var look = o["look"] as Dictionary<string, object>;
            var anns = (o["annotations"] as List<object>).OfType<Dictionary<string, object>>().ToList();
            var cs = o["captionStyle"] as Dictionary<string, object>;
            r.Check("look extras round-trip", look["typeSound"] is bool ts && ts && (string)look["typeStyle"] == "thock" && (string)look["rippleStyle"] == "glass" && look.ContainsKey("rippleWidth") && look.ContainsKey("typeMuteFast"));
            r.Check("aspectW/H round-trip", look.ContainsKey("aspectW") && Near(back.Look.AspectW, 4) && Near(back.Look.AspectH, 5));
            r.Check("annotation extras round-trip", (string)anns[0]["arrow"] == "curved" && anns[0].ContainsKey("opacity") && (string)anns[1]["textStyle"] == "glass" && anns[2]["oval"] is bool ov && ov);
            r.Check("caption cx/cy round-trip", cs.ContainsKey("cx") && cs.ContainsKey("cy"));
            r.Check("splits / keyMutes round-trip", back.Splits.SequenceEqual(mp.Splits) && back.KeyMutes.Count == 1 && Near(back.KeyMutes[0][1], 7.5));
            r.Check("same after second save", back.ToJson() == StudioProject.FromJson(back.ToJson()).ToJson());

            // 2) 剪映式剪辑：分割出 2–8 整段 2×；再在 5 处分割、删掉 5–8
            var p = new StudioProject { TrimStart = 0, TrimEnd = 14 };
            Pieces.Split(p, 2, D); Pieces.Split(p, 8, D);
            var pc = Pieces.List(p, D).First(x => Near(x.Start, 2)); Pieces.SetSpeed(p, pc.Start, pc.End, 2);
            r.Check("split too close refused", !Pieces.Split(p, 2.05, D));
            Pieces.Split(p, 5, D);
            pc = Pieces.List(p, D).First(x => Near(x.Start, 5)); Pieces.Delete(p, pc.Start, pc.End);
            var cuts = p.Cuts.OrderBy(c => c.Start).ToList();
            r.Check("speed 2–5 kept, 5–8 removed", cuts.Count == 2 && cuts[0].Speed && Near(cuts[0].Start, 2) && Near(cuts[0].End, 5) && !cuts[1].Speed && Near(cuts[1].Start, 5) && Near(cuts[1].End, 8),
                    string.Join(" ", cuts.Select(c => (c.Speed ? "speed" : "cut") + "[" + F(c.Start) + "," + F(c.End) + "]")));
            r.Check("no overlapping cuts", cuts.Zip(cuts.Skip(1), (a, b) => a.End <= b.Start + 1e-6).All(x => x));
            var m = new TimeMap(p);
            r.Check("export skips 5–8", Near(m.Out(8), m.Out(5)), F(m.Out(5)) + " " + F(m.Out(8)));
            r.Check("2–5 plays at 2×", Near(m.Out(5) - m.Out(2), 1.5, 0.05), F(m.Out(5) - m.Out(2)));
            r.Check("no ghost piece", Pieces.List(p, D).All(x => x.Len > 0.05), Pieces.List(p, D).Count + " pieces");
            pc = Pieces.List(p, D).First(x => Near(x.Start, 2)); Pieces.SetSpeed(p, pc.Start, pc.End, 1);
            r.Check("back to 1×", !p.Cuts.Any(c => c.Speed));
            // 删掉的段旁边再删：并成一段
            pc = Pieces.List(p, D).First(x => Near(x.Start, 2)); Pieces.Delete(p, pc.Start, pc.End);
            r.Check("adjacent deletes merge", p.Cuts.Count(c => !c.Speed) == 1 && Near(p.Cuts.First(c => !c.Speed).Start, 2) && Near(p.Cuts.First(c => !c.Speed).End, 8));

            // 3) 可见时间：删掉 2–8 后合拢
            r.Check("vis time collapses", Near(Pieces.VisTime(p, 10), 4) && Near(Pieces.VisDuration(p, D), 14), F(Pieces.VisTime(p, 10)));
            r.Check("src time skips removed", Near(Pieces.SrcTime(p, 2, D), 8) && Near(Pieces.SrcTime(p, 1, D), 1));
            r.Check("drag across removed part", Near(1 + Pieces.SrcDelta(p, 1, 3, D), 10), F(1 + Pieces.SrcDelta(p, 1, 3, D)));

            // 4) 打字声：分阵、去掉 / 恢复、改速度
            var tr = new Track { Width = 1920, Height = 1080, PixelsPerPoint = 1 };
            tr.Keys.AddRange(new[] { 1.0, 1.3, 1.6, 2.0, 6.0, 6.4, 6.8, 7.1 });
            var q = new StudioProject { TrimStart = 0, TrimEnd = 14 };
            Pieces.SetLookBool(q, "typeSound", true);
            var bursts = Pieces.Bursts(tr, q);
            r.Check("two bursts", bursts.Count == 2 && bursts[0].Keys.Count == 4 && bursts[1].Keys.Count == 4, bursts.Count.ToString());
            Pieces.SetBurstSound(q, bursts[1], false);
            r.Check("burst muted", !Pieces.BurstOn(q, bursts[1]) && Pieces.BurstOn(q, bursts[0]) && !Pieces.KeySounds(q, 6.4));
            r.Check("muted range written", q.KeyMutes.Count == 1);
            Pieces.SetBurstSound(q, bursts[1], true);
            r.Check("burst restored", Pieces.BurstOn(q, bursts[1]) && q.KeyMutes.Count == 0);
            Pieces.SetBurstRate(q, bursts[0], 3);
            r.Check("burst speed", Near(Pieces.BurstRate(q, bursts[0]), 3) && q.Cuts.Count == 1 && q.Cuts[0].Speed);
            Pieces.SetLookBool(q, "typeMuteFast", true);
            r.Check("sped-up typing muted when asked", !Pieces.KeySounds(q, 1.3) && Pieces.KeySounds(q, 6.4));
            Pieces.SetBurstRate(q, bursts[0], 1);
            r.Check("burst back to 1×", q.Cuts.Count == 0);
            // 删掉的段里的按键不算
            var q2 = new StudioProject { TrimStart = 0, TrimEnd = 14 }; q2.Cuts.Add(new CutSegment { Start = 5.5, End = 7.5 });
            r.Check("keys in removed part dropped", Pieces.Bursts(tr, q2).Sum(b => b.Keys.Count) == 4);
            return r.Done("W5RULES");
        }
    }
}
