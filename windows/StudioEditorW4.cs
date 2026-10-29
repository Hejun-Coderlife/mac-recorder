// 剪辑窗口的 W4 部分（照 Mac 版 OverlayEditor.swift / EditorExtras.swift）：
//   · 遮挡（模糊 / 马赛克 / 色块）、标注（箭头 / 框 / 圈 / 高亮 / 文字）：播放栏「标注」选工具 → 在预览画面上拖出来；
//     选中后在画面上拖中间挪、拉八个点改大小（箭头拖两头），双击文字改字，Ctrl+D 复制；时间轴上各一条轨
//   · 背景音乐：选文件（拷进素材夹 music\），音乐轨拖动 / 剪两头；音量、淡入淡出、循环、说话时压低在右边「声音」里
//   · 推近速度：每段单独设（跟全局 / 慢 / 标准 / 快），全局的在右边「推近」里
//   · 建议加速：找空档（4×）和一直在打字的地方（2×），剪辑轨上画成虚线块，✓ 采纳、✕ 忽略
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static ScreenRecorder.App;

namespace ScreenRecorder
{
    sealed partial class StudioEditor
    {
        public string SelectedRed, SelectedAnn, SelectedMusic;
        public List<SpeedSuggestion> Suggestions = new List<SpeedSuggestion>();
        public bool Suggesting;
        public PreviewBox Preview => preview;
        public bool EditingAnnText => annEditing != null && annText.Focused;
        public string Tool;                    // 正在画什么：null / "redact" / arrow rect ellipse highlight text
        readonly Button annotateBtn = new Button(), suggestBtn = new Button();

        void InitW4()
        {
            preview.OverlayDown = OverlayDown; preview.OverlayMove = (x, y) => { try { OverlayMove(x, y); } catch (Exception ex) { OverlayLog += "move error " + ex.GetType().Name + ": " + ex.Message + "; "; } }; preview.OverlayUp = OverlayUp;
            preview.OverlayDouble = OverlayDouble; preview.OverlayCursor = OverlayCursorAt; preview.Decor = DrawDecor;
        }

        void BuildW4Bar()
        {
            Flat(annotateBtn, "✎ " + L("标注", "Annotate"), (s, e) => ShowAnnotateMenu());
            tips.SetToolTip(annotateBtn, L("遮挡隐私（模糊 / 马赛克 / 色块），或者加箭头、框、圈、高亮、文字：选好后在预览画面上拖出来", "Redact (blur / mosaic / solid) or add an arrow, box, circle, highlight or text: pick one, then drag on the picture"));
            Flat(suggestBtn, "⏩", (s, e) => SuggestSpeed()); suggestBtn.Width = 34;
            tips.SetToolTip(suggestBtn, L("建议加速：找出很久没操作的空档（4×）和一直在打字的地方（2×），在剪辑轨上标出来，✓ 采纳、✕ 忽略", "Suggest speed-ups: finds idle gaps (4×) and long typing (2×); accept ✓ or ignore ✕ on the cut lane"));
        }

        void ShowAnnotateMenu()
        {
            var m = new ContextMenuStrip();
            void Item(string text, string tool) { var it = new ToolStripMenuItem(text) { Checked = Tool == tool }; it.Click += (s, e) => StartTool(tool); m.Items.Add(it); }
            Item("▦ " + L("遮挡（模糊 / 马赛克）", "Redact (blur / mosaic)"), "redact");
            m.Items.Add(new ToolStripSeparator());
            foreach (var k in Annotation.Kinds) Item(Annotation.Name(k, L), k);
            m.Show(annotateBtn, new Point(0, annotateBtn.Height));
        }

        public void StartTool(string tool)
        {
            Select(null, null);
            Tool = tool;
            Changed?.Invoke();
        }

        // ---- 预览：遮挡、标注画进原画面（同导出）。按住拖动时不复制也行，但简单起见每次画一张新的 ----
        StudioProject ovFor; OverlayLayer ovLayer;
        byte[] MarkOverlays(byte[] src, double t)
        {
            var p = Project;
            if (!ReferenceEquals(p, ovFor)) { ovFor = p; ovLayer = OverlayLayer.Make(p); }
            var ol = ovLayer;
            if (ol == null) return src;
            var dst = new byte[src.Length];
            return ol.Apply(src, dst, pw, ph, t, ptrack.PixelsPerPoint) ? dst : src;
        }

        // ---- 原画面比例 ↔ 预览画面比例（都是左上原点），按当前播放头那一帧的镜头 ----
        bool CurFrame(out StudioLayout lay, out FramePlan f)
        {
            StudioScene sc; FramePlan[] pl;
            lock (sceneGate) { sc = scene; pl = plan; }
            lay = sc?.Lay; f = default;
            if (sc == null || pl == null || pl.Length == 0) return false;
            f = pl[Math.Min(Math.Max((int)Math.Round(Time * Fps), 0), pl.Length - 1)];
            return true;
        }
        public (double x, double y) ToView(double nx, double ny)
        {
            if (!CurFrame(out var L, out var f)) return (nx, ny);
            double X = L.card.X + nx * L.W * L.s, Y = L.card.Y + (1 - ny) * L.H * L.s;
            return (((X - f.sx) * f.z + L.cw / 2) / L.cw, (L.ch - ((Y - f.sy) * f.z + L.ch / 2)) / L.ch);
        }
        public (double x, double y) FromView(double vx, double vy)
        {
            if (!CurFrame(out var L, out var f)) return (vx, vy);
            double px = vx * L.cw, py = L.ch - vy * L.ch;
            double sx = (px - L.cw / 2) / f.z + f.sx, sy = (py - L.ch / 2) / f.z + f.sy;
            return ((sx - L.card.X) / L.s / L.W, 1 - (sy - L.card.Y) / L.s / L.H);
        }

        // 某个遮挡 / 标注在原画面比例里的框（左上原点）
        (double x, double y, double w, double h) NormBox(object o)
        {
            if (o is Redaction r) return (Math.Min(r.X, r.X + r.W), Math.Min(r.Y, r.Y + r.H), Math.Abs(r.W), Math.Abs(r.H));
            var a = (Annotation)o;
            return OverlayLayer.NormBounds(a, Track.Width, Track.Height, Track.PixelsPerPoint);
        }
        object SelectedOverlay => (object)Project.Redactions.FirstOrDefault(r => r.Id == SelectedRed) ?? Project.Annotations.FirstOrDefault(a => a.Id == SelectedAnn);
        static bool VisibleAt(object o, double t) => o is Redaction r ? t >= r.Start && t < r.End : o is Annotation a && t >= a.Start && t < a.End;
        string IdOf(object o) => o is Redaction r ? r.Id : ((Annotation)o).Id;

        // 把手（预览画面比例）：0 左上 1 上 2 右上 3 右 4 右下 5 下 6 左下 7 左；箭头是 0 起点、1 终点
        List<(int k, double x, double y)> HandlesOf(object o)
        {
            var list = new List<(int, double, double)>();
            if (o is Annotation a && a.Kind == "arrow")
            {
                var p0 = ToView(a.X, a.Y); var p1 = ToView(a.X + a.W, a.Y + a.H);
                list.Add((100, p0.x, p0.y)); list.Add((101, p1.x, p1.y));
                return list;
            }
            if (o is Annotation t && t.Kind == "text") return list;
            var b = NormBox(o);
            var tl = ToView(b.x, b.y); var br = ToView(b.x + b.w, b.y + b.h);
            double mx = (tl.x + br.x) / 2, my = (tl.y + br.y) / 2;
            list.Add((0, tl.x, tl.y)); list.Add((1, mx, tl.y)); list.Add((2, br.x, tl.y)); list.Add((3, br.x, my));
            list.Add((4, br.x, br.y)); list.Add((5, mx, br.y)); list.Add((6, tl.x, br.y)); list.Add((7, tl.x, my));
            return list;
        }

        double Tol => 8.0 / Math.Max(50, preview.ImageRect.Width);   // 把手命中范围（预览画面比例，约 8 像素）

        int HitHandle(object o, double vx, double vy)
        {
            double tol = Tol, ty = tol * Math.Max(50, preview.ImageRect.Width) / Math.Max(50, preview.ImageRect.Height);
            foreach (var (k, x, y) in HandlesOf(o)) if (Math.Abs(vx - x) <= tol && Math.Abs(vy - y) <= ty) return k;
            return -1;
        }
        bool InsideBox(object o, double vx, double vy)
        {
            if (o is Annotation a && a.Kind == "arrow")
            {
                var p0 = ToView(a.X, a.Y); var p1 = ToView(a.X + a.W, a.Y + a.H);
                double ax = p1.x - p0.x, ay = p1.y - p0.y, l2 = ax * ax + ay * ay;
                double u = l2 > 1e-12 ? Math.Min(1, Math.Max(0, ((vx - p0.x) * ax + (vy - p0.y) * ay) / l2)) : 0;
                double dx = p0.x + ax * u - vx, dy = p0.y + ay * u - vy;
                return Math.Sqrt(dx * dx + dy * dy) < Tol * 1.6;
            }
            var b = NormBox(o);
            var tl = ToView(b.x, b.y); var br = ToView(b.x + b.w, b.y + b.h);
            return vx >= Math.Min(tl.x, br.x) - Tol && vx <= Math.Max(tl.x, br.x) + Tol && vy >= Math.Min(tl.y, br.y) - Tol && vy <= Math.Max(tl.y, br.y) + Tol;
        }
        // 当前时间看得见的遮挡 / 标注里，点到的最上面那个（标注在遮挡上面）
        object OverlayAt(double vx, double vy)
        {
            foreach (var a in Project.Annotations.AsEnumerable().Reverse()) if (VisibleAt(a, Time) && InsideBox(a, vx, vy)) return a;
            foreach (var r in Project.Redactions.AsEnumerable().Reverse()) if (VisibleAt(r, Time) && InsideBox(r, vx, vy)) return r;
            return null;
        }

        // ---- 在预览上按下 / 拖 / 松手 ----
        string ovMode, ovId;                    // "create" / "move" / "h0".."h7" / "a0" "a1"
        object ovNew;                           // 正在拖出来的新遮挡 / 标注（拖动每一步都从拖之前的工程重做，要把它再放进去）
        void PutNew(StudioProject p)
        {
            if (ovNew is Redaction nr && !p.Redactions.Any(x => x.Id == nr.Id)) p.Redactions.Add(nr.Clone());
            if (ovNew is Annotation na && !p.Annotations.Any(x => x.Id == na.Id)) p.Annotations.Add(na.Clone());
        }
        double downNx, downNy;
        (double x, double y, double w, double h) ovBase;

        public string OverlayLog = "";        // 自测看：预览上按下时的情况
        public bool OverlayDown(double vx, double vy)
        {
            var (nx, ny) = FromView(vx, vy);
            if (OverlayLog.Length > 2000) OverlayLog = "";
            OverlayLog += $"down v({vx:0.000},{vy:0.000}) n({nx:0.000},{ny:0.000}) tool={Tool ?? "-"}; ";
            downNx = nx; downNy = ny;
            if (Tool != null)
            {
                if (nx < -0.05 || nx > 1.05 || ny < -0.05 || ny > 1.05) return false;
                StartCreate(Tool, nx, ny);
                return true;
            }
            var sel = SelectedOverlay;
            if (sel != null && VisibleAt(sel, Time))
            {
                int h = HitHandle(sel, vx, vy);
                if (h >= 0) { BeginOverlayDrag(sel, h >= 100 ? "a" + (h - 100) : "h" + h); return true; }
                if (InsideBox(sel, vx, vy)) { BeginOverlayDrag(sel, "move"); return true; }
            }
            var hit = OverlayAt(vx, vy);
            if (hit != null)
            {
                Select(hit is Redaction ? "red" : "ann", IdOf(hit));
                BeginOverlayDrag(hit, "move");
                return true;
            }
            return false;
        }

        void BeginOverlayDrag(object o, string mode)
        {
            ovMode = mode; ovId = IdOf(o);
            ovBase = o is Redaction r ? (r.X, r.Y, r.W, r.H) : (((Annotation)o).X, ((Annotation)o).Y, ((Annotation)o).W, ((Annotation)o).H);
            DragBegin();
        }

        void StartCreate(string tool, double nx, double ny)
        {
            double t0 = Math.Min(Math.Max(Time, 0), Math.Max(0, Duration - 0.5)), t1 = Math.Min(Duration, t0 + 3);
            DragBegin();
            if (tool == "redact")
            {
                var r = new Redaction { Start = t0, End = t1, X = nx, Y = ny, W = 0, H = 0 };
                ovId = r.Id; ovNew = r;
                DragUpdate(PutNew);
                SelectedRed = r.Id; SelectedAnn = null; Selected = null; SelectedCut = null;
            }
            else
            {
                var a = new Annotation { Start = t0, End = t1, Kind = tool, X = nx, Y = ny, W = 0, H = 0, Text = tool == "text" ? L("文字", "Text") : "" };
                if (tool == "highlight") a.Color = Annotation.Palette[2];
                ovId = a.Id; ovNew = a;
                DragUpdate(PutNew);
                SelectedAnn = a.Id; SelectedRed = null; Selected = null; SelectedCut = null;
            }
            ovMode = "create"; ovBase = (nx, ny, 0, 0);
        }

        int ovMoves;
        public void OverlayMove(double vx, double vy)
        {
            if (ovMoves++ < 3) OverlayLog += $"move v({vx:0.000},{vy:0.000}) mode={ovMode ?? "-"} dragging={IsDragging}; ";
            if (ovMode == null) return;
            var (nx, ny) = FromView(vx, vy);
            double dx = nx - downNx, dy = ny - downNy;
            var b = ovBase; string mode = ovMode, id = ovId;
            DragUpdate(p =>
            {
                PutNew(p);
                var r = p.Redactions.FirstOrDefault(x => x.Id == id); var a = p.Annotations.FirstOrDefault(x => x.Id == id);
                double x0, y0, w, h;
                if (mode == "create") { x0 = b.x; y0 = b.y; w = nx - b.x; h = ny - b.y; if (a == null || a.Kind != "arrow") { x0 = Math.Min(b.x, nx); y0 = Math.Min(b.y, ny); w = Math.Abs(nx - b.x); h = Math.Abs(ny - b.y); } }
                else if (mode == "move")
                {
                    w = b.w; h = b.h;
                    double minX = Math.Min(0, -Math.Min(0, w)), maxX = 1 - Math.Max(0, w), minY = Math.Min(0, -Math.Min(0, h)), maxY = 1 - Math.Max(0, h);
                    x0 = Math.Min(Math.Max(b.x + dx, minX), Math.Max(minX, maxX)); y0 = Math.Min(Math.Max(b.y + dy, minY), Math.Max(minY, maxY));
                }
                else if (mode == "a0") { x0 = nx; y0 = ny; w = b.x + b.w - nx; h = b.y + b.h - ny; }
                else if (mode == "a1") { x0 = b.x; y0 = b.y; w = nx - b.x; h = ny - b.y; }
                else
                {
                    int k = mode[1] - '0';
                    double L0 = b.x, T0 = b.y, R0 = b.x + b.w, B0 = b.y + b.h;
                    if (k == 0 || k == 6 || k == 7) L0 = Math.Min(nx, R0 - 0.01);
                    if (k == 2 || k == 3 || k == 4) R0 = Math.Max(nx, L0 + 0.01);
                    if (k == 0 || k == 1 || k == 2) T0 = Math.Min(ny, B0 - 0.01);
                    if (k == 4 || k == 5 || k == 6) B0 = Math.Max(ny, T0 + 0.01);
                    x0 = L0; y0 = T0; w = R0 - L0; h = B0 - T0;
                }
                if (r != null) { r.X = x0; r.Y = y0; r.W = w; r.H = h; }
                if (a != null) { a.X = x0; a.Y = y0; if (a.Kind != "text" || mode != "move") { a.W = w; a.H = h; } }
            });
        }

        public void OverlayUp()
        {
            OverlayLog += $"up after {ovMoves} moves mode={ovMode ?? "-"}; "; ovMoves = 0;
            if (ovMode == "create")
            {
                // 只点了一下（没拖）：给个默认大小
                string id = ovId;
                DragUpdate(p =>
                {
                    PutNew(p);
                    var r = p.Redactions.FirstOrDefault(x => x.Id == id); var a = p.Annotations.FirstOrDefault(x => x.Id == id);
                    if (r != null && (r.W < 0.01 || r.H < 0.01)) { r.W = 0.2; r.H = 0.12; r.X = Math.Min(Math.Max(0, r.X - 0.1), 0.8); r.Y = Math.Min(Math.Max(0, r.Y - 0.06), 0.88); }
                    if (a != null && a.Kind == "arrow" && Math.Abs(a.W) < 0.01 && Math.Abs(a.H) < 0.01) { a.W = 0.12; a.H = -0.1; }
                    else if (a != null && a.Kind != "text" && a.Kind != "arrow" && (a.W < 0.01 || a.H < 0.01)) { a.W = 0.2; a.H = 0.14; a.X = Math.Min(Math.Max(0, a.X - 0.1), 0.8); a.Y = Math.Min(Math.Max(0, a.Y - 0.07), 0.86); }
                    if (a != null && a.Kind == "text") { a.W = 0; a.H = 0; }
                });
                bool text = Project.Annotations.FirstOrDefault(x => x.Id == id)?.Kind == "text";
                Tool = null;
                ovMode = null; ovNew = null;
                DragEnd();
                if (text) EditAnnotationText();
                return;
            }
            ovMode = null;
            DragEnd();
        }

        public bool OverlayDouble(double vx, double vy)
        {
            var hit = OverlayAt(vx, vy);
            if (hit is Annotation a && a.Kind == "text") { Select("ann", a.Id); EditAnnotationText(); return true; }
            return false;
        }

        Cursor OverlayCursorAt(double vx, double vy)
        {
            if (Tool != null) return Cursors.Cross;
            var sel = SelectedOverlay;
            if (sel != null && VisibleAt(sel, Time))
            {
                int h = HitHandle(sel, vx, vy);
                if (h >= 100) return Cursors.SizeAll;
                if (h == 0 || h == 4) return Cursors.SizeNWSE;
                if (h == 2 || h == 6) return Cursors.SizeNESW;
                if (h == 1 || h == 5) return Cursors.SizeNS;
                if (h == 3 || h == 7) return Cursors.SizeWE;
                if (InsideBox(sel, vx, vy)) return Cursors.SizeAll;
            }
            return OverlayAt(vx, vy) != null ? Cursors.Hand : null;
        }

        // 选中框和把手、画的时候的提示
        void DrawDecor(Graphics g, RectangleF ir)
        {
            var sel = SelectedOverlay;
            if (sel == null || !VisibleAt(sel, Time)) return;
            PointF P(double x, double y) => new PointF(ir.X + (float)(x * ir.Width), ir.Y + (float)(y * ir.Height));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(230, 0, 120, 215), 1.5f) { DashStyle = DashStyle.Dash })
            {
                if (sel is Annotation a && a.Kind == "arrow") { var p0 = ToView(a.X, a.Y); var p1 = ToView(a.X + a.W, a.Y + a.H); g.DrawLine(pen, P(p0.x, p0.y), P(p1.x, p1.y)); }
                else
                {
                    var b = NormBox(sel); var tl = ToView(b.x, b.y); var br = ToView(b.x + b.w, b.y + b.h);
                    var a0 = P(tl.x, tl.y); var a1 = P(br.x, br.y);
                    g.DrawRectangle(pen, Math.Min(a0.X, a1.X), Math.Min(a0.Y, a1.Y), Math.Abs(a1.X - a0.X), Math.Abs(a1.Y - a0.Y));
                }
            }
            using (var fill = new SolidBrush(Color.White)) using (var edge = new Pen(Color.FromArgb(0, 120, 215), 1.5f))
                foreach (var (k, x, y) in HandlesOf(sel)) { var c = P(x, y); var rr = new RectangleF(c.X - 4.5f, c.Y - 4.5f, 9, 9); g.FillEllipse(fill, rr); g.DrawEllipse(edge, rr); }
        }

        string ToolHint => Tool == null ? null : Tool == "redact" ? L("在画面上拖出要遮住的地方（Esc 取消）", "Drag over what to hide (Esc to cancel)")
                         : Tool == "text" ? L("在画面上点一下放文字（Esc 取消）", "Click the picture to place text (Esc to cancel)")
                         : Tool == "arrow" ? L("在画面上从尾拖到箭头指的地方（Esc 取消）", "Drag from the tail to where the arrow points (Esc to cancel)")
                         : L("在画面上拖出来（Esc 取消）", "Drag on the picture (Esc to cancel)");

        // ---- 属性条 ----
        readonly Panel redInspector = new Panel(), annInspector = new Panel(), musInspector = new Panel(), sugBar = new Panel();
        readonly Label redTitle = new Label(), redTime = new Label(), annTitle = new Label(), annTime = new Label(), musTitle = new Label(), musTime = new Label(), sugLabel = new Label();
        readonly Dictionary<string, Button> redStyles = new Dictionary<string, Button>();
        readonly TrackBar redStrength = new TrackBar(), annWidth = new TrackBar(), annFont = new TrackBar();
        readonly Button redColor = new Button(), redDelete = new Button(), annDelete = new Button(), annDup = new Button(), musDelete = new Button(), sugAccept = new Button(), sugIgnore = new Button(), musChoose = new Button();
        readonly List<Button> annColors = new List<Button>();
        readonly TextBox annText = new TextBox();
        readonly CheckBox annPill = new CheckBox(), annDraw = new CheckBox();
        readonly Label annWidthL = new Label(), annFontL = new Label(), redStrengthL = new Label();
        bool syncingW4;

        void BuildW4Inspectors(Panel tlArea)
        {
            foreach (var pnl in new[] { redInspector, annInspector, musInspector, sugBar }) { pnl.Dock = DockStyle.Top; pnl.Height = 34; pnl.Visible = false; }
            FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0), Margin = new Padding(0) };
            Label Title(Label l, string text) { l.Text = text; l.AutoSize = true; l.Font = new Font(Font, FontStyle.Bold); l.Margin = new Padding(0, 8, 8, 0); return l; }
            Label Sub(Label l) { l.AutoSize = true; l.ForeColor = Theme.Secondary; l.Margin = new Padding(0, 8, 12, 0); return l; }
            TrackBar Bar(TrackBar b, int min, int max, int w) { b.Minimum = min; b.Maximum = max; b.TickStyle = TickStyle.None; b.AutoSize = false; b.Width = w; b.Height = 28; b.Margin = new Padding(0, 3, 4, 0); return b; }

            // 遮挡：模糊 / 马赛克 / 色块、强度、色块颜色、删除
            var rf = Flow();
            rf.Controls.Add(Title(redTitle, L("遮挡", "Redact"))); rf.Controls.Add(Sub(redTime));
            foreach (var st in Redaction.Styles)
            {
                var b = new Button(); var s0 = st;
                Flat(b, Redaction.Name(st, L), (s, e) => EditRed(r => r.Style = s0));
                redStyles[st] = b; rf.Controls.Add(b);
            }
            redStrengthL.Text = L("强度", "Strength"); redStrengthL.AutoSize = true; redStrengthL.Margin = new Padding(8, 8, 4, 0); rf.Controls.Add(redStrengthL);
            Bar(redStrength, 0, 100, 110);
            redStrength.Scroll += (s, e) => { if (syncingW4) return; var id = SelectedRed; double v = redStrength.Value / 100.0; DragBegin(); DragUpdate(p => { var r = p.Redactions.FirstOrDefault(x => x.Id == id); if (r != null) r.Strength = v; }); };
            redStrength.MouseUp += (s, e) => DragEnd(); redStrength.KeyUp += (s, e) => DragEnd();
            rf.Controls.Add(redStrength);
            Flat(redColor, L("颜色…", "Color…"), (s, e) => { var r = Project.Redactions.FirstOrDefault(x => x.Id == SelectedRed); if (r == null) return; using (var d = new ColorDialog { Color = ToColor(r.Color), FullOpen = true }) if (d.ShowDialog(this) == DialogResult.OK) { var c = FromColor(d.Color); EditRed(x => x.Color = c); } });
            rf.Controls.Add(redColor);
            Flat(redDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected()); redDelete.Dock = DockStyle.Right;
            redInspector.Controls.Add(rf); redInspector.Controls.Add(redDelete);

            // 标注：颜色、线宽 / 文字（字、字号、底色胶囊）、箭头画出来、复制、删除
            var af = Flow();
            af.Controls.Add(Title(annTitle, L("标注", "Annotation"))); af.Controls.Add(Sub(annTime));
            foreach (var c in Annotation.Palette)
            {
                var cc = c;
                var b = new Button { Width = 20, Height = 20, FlatStyle = FlatStyle.Flat, BackColor = ToColor(c), Margin = new Padding(0, 6, 3, 0), TabStop = false, Text = "" };
                b.FlatAppearance.BorderColor = Theme.Line; b.FlatAppearance.BorderSize = 1;
                b.Click += (s, e) => EditAnn(a => a.Color = cc);
                annColors.Add(b); af.Controls.Add(b);
            }
            annWidthL.Text = L("粗细", "Width"); annWidthL.AutoSize = true; annWidthL.Margin = new Padding(8, 8, 4, 0); af.Controls.Add(annWidthL);
            Bar(annWidth, 1, 20, 90);
            annWidth.Scroll += (s, e) => { if (syncingW4) return; var id = SelectedAnn; double v = annWidth.Value; DragBegin(); DragUpdate(p => { var a = p.Annotations.FirstOrDefault(x => x.Id == id); if (a != null) a.Width = v; }); };
            annWidth.MouseUp += (s, e) => DragEnd(); annWidth.KeyUp += (s, e) => DragEnd();
            af.Controls.Add(annWidth);
            annText.Width = 150; annText.Margin = new Padding(6, 5, 4, 0);
            annText.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitAnnText(); ActiveControl = null; } else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; annEditing = null; SyncW4(); ActiveControl = null; } };
            annText.Leave += (s, e) => CommitAnnText();
            af.Controls.Add(annText);
            annFontL.Text = L("字号", "Size"); annFontL.AutoSize = true; annFontL.Margin = new Padding(4, 8, 4, 0); af.Controls.Add(annFontL);
            Bar(annFont, 12, 96, 90);
            annFont.Scroll += (s, e) => { if (syncingW4) return; var id = SelectedAnn; double v = annFont.Value; DragBegin(); DragUpdate(p => { var a = p.Annotations.FirstOrDefault(x => x.Id == id); if (a != null) a.FontSize = v; }); };
            annFont.MouseUp += (s, e) => DragEnd(); annFont.KeyUp += (s, e) => DragEnd();
            af.Controls.Add(annFont);
            annPill.Text = L("底色", "Pill"); annPill.AutoSize = true; annPill.Margin = new Padding(4, 8, 4, 0);
            annPill.CheckedChanged += (s, e) => { if (!syncingW4) EditAnn(a => a.Pill = annPill.Checked); };
            af.Controls.Add(annPill);
            annDraw.Text = L("画出来", "Draw on"); annDraw.AutoSize = true; annDraw.Margin = new Padding(4, 8, 4, 0);
            tips.SetToolTip(annDraw, L("箭头出现时像笔画出来那样", "The arrow draws itself in when it appears"));
            annDraw.CheckedChanged += (s, e) => { if (!syncingW4) EditAnn(a => a.DrawOn = annDraw.Checked); };
            af.Controls.Add(annDraw);
            Flat(annDup, "⧉", (s, e) => DuplicateOverlay()); tips.SetToolTip(annDup, L("复制一个（Ctrl+D）", "Duplicate (Ctrl+D)"));
            af.Controls.Add(annDup);
            Flat(annDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected()); annDelete.Dock = DockStyle.Right;
            annInspector.Controls.Add(af); annInspector.Controls.Add(annDelete);

            // 音乐：名字、范围、换一首、删除（音量等在右边「声音」里）
            var mf = Flow();
            mf.Controls.Add(Title(musTitle, "♪")); mf.Controls.Add(Sub(musTime));
            mf.Controls.Add(new Label { Text = L("音量、淡入淡出、循环、说话时压低：右边「声音」", "Volume, fades, loop, ducking: Sound on the right"), AutoSize = true, ForeColor = Theme.Secondary, Margin = new Padding(0, 8, 8, 0) });
            Flat(musDelete, "🗑 " + L("删除", "Delete"), (s, e) => DeleteSelected()); musDelete.Dock = DockStyle.Right;
            musInspector.Controls.Add(mf); musInspector.Controls.Add(musDelete);

            // 建议加速：几处、全部采纳、全部忽略
            var sf = Flow();
            sugLabel.AutoSize = true; sugLabel.Margin = new Padding(0, 8, 12, 0); sugLabel.Font = new Font(Font, FontStyle.Bold);
            Flat(sugAccept, "✓ " + L("全部采纳", "Accept All"), (s, e) => AcceptSuggestions(Suggestions.Select(x => x.Id).ToArray()));
            Flat(sugIgnore, "✕ " + L("全部忽略", "Ignore All"), (s, e) => IgnoreSuggestions(Suggestions.Select(x => x.Id).ToArray()));
            sf.Controls.Add(sugLabel); sf.Controls.Add(sugAccept); sf.Controls.Add(sugIgnore);
            sugBar.Controls.Add(sf);

            tlArea.Controls.Add(redInspector); tlArea.Controls.Add(annInspector); tlArea.Controls.Add(musInspector); tlArea.Controls.Add(sugBar);
        }

        static Color ToColor(RGB c) => Color.FromArgb((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255));
        static RGB FromColor(Color c) => new RGB(c.R / 255.0, c.G / 255.0, c.B / 255.0);

        void EditRed(Action<Redaction> change) { var id = SelectedRed; Edit(p => { var r = p.Redactions.FirstOrDefault(x => x.Id == id); if (r != null) change(r); }); Touch(); }
        void EditAnn(Action<Annotation> change) { var id = SelectedAnn; Edit(p => { var a = p.Annotations.FirstOrDefault(x => x.Id == id); if (a != null) change(a); }); Touch(); }

        string annEditing;
        public void EditAnnotationText()
        {
            SyncW4();
            var a = Project.Annotations.FirstOrDefault(x => x.Id == SelectedAnn);
            if (a == null || a.Kind != "text" || !annInspector.Visible) return;
            annEditing = a.Id;
            annText.Focus(); annText.SelectAll();
        }
        void CommitAnnText()
        {
            var id = annEditing; annEditing = null;
            if (id == null) return;
            var text = annText.Text.Replace("\r", "").Replace("\n", " ");
            Edit(p => { var a = p.Annotations.FirstOrDefault(x => x.Id == id); if (a != null && a.Text != text) a.Text = text; });
        }
        // 自测用：直接改文字（和在属性条里打字、回车一样）
        public void SetAnnotationText(string text) { annEditing = SelectedAnn; annText.Text = text; CommitAnnText(); }

        void SyncW4()
        {
            syncingW4 = true;
            try
            {
                var r = Project.Redactions.FirstOrDefault(x => x.Id == SelectedRed);
                redInspector.Visible = r != null;
                if (r != null)
                {
                    redTime.Text = Clock(r.Start) + " – " + Clock(r.End);
                    foreach (var kv in redStyles) { bool on = kv.Key == r.Style; kv.Value.BackColor = on ? Theme.Accent : Theme.Control; kv.Value.ForeColor = on ? Color.White : Theme.Text; }
                    int sv = (int)Math.Round(r.Strength * 100); if (!IsDragging && redStrength.Value != sv) redStrength.Value = Math.Min(100, Math.Max(0, sv));
                    redStrength.Visible = redStrengthL.Visible = r.Style != "solid"; redColor.Visible = r.Style == "solid";
                }
                var a = Project.Annotations.FirstOrDefault(x => x.Id == SelectedAnn);
                annInspector.Visible = a != null;
                if (a != null)
                {
                    annTitle.Text = Annotation.Name(a.Kind, L);
                    annTime.Text = Clock(a.Start) + " – " + Clock(a.End);
                    for (int i = 0; i < annColors.Count; i++) { bool on = Annotation.Palette[i].Same(a.Color); annColors[i].FlatAppearance.BorderSize = on ? 3 : 1; annColors[i].FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Line; }
                    bool text = a.Kind == "text";
                    annWidth.Visible = annWidthL.Visible = !text && a.Kind != "highlight";
                    annText.Visible = annFont.Visible = annFontL.Visible = annPill.Visible = text;
                    annDraw.Visible = a.Kind == "arrow";
                    int wv = (int)Math.Round(a.Width); if (!IsDragging && annWidth.Value != Math.Min(20, Math.Max(1, wv))) annWidth.Value = Math.Min(20, Math.Max(1, wv));
                    int fv = (int)Math.Round(a.FontSize); if (!IsDragging && annFont.Value != Math.Min(96, Math.Max(12, fv))) annFont.Value = Math.Min(96, Math.Max(12, fv));
                    if (annEditing == null && annText.Text != a.Text) annText.Text = a.Text;
                    annPill.Checked = a.Pill; annDraw.Checked = a.DrawOn;
                }
                var m = Project.Music.Clips.FirstOrDefault(x => x.Id == SelectedMusic);
                musInspector.Visible = m != null;
                if (m != null)
                {
                    musTitle.Text = "♪ " + m.Name;
                    musTime.Text = L($"成片 {Clock(m.Start)} 起，用 {Clock(m.TrimIn)}–{Clock(m.TrimOut)}", $"from {Clock(m.Start)}, using {Clock(m.TrimIn)}–{Clock(m.TrimOut)}");
                }
                sugBar.Visible = Suggestions.Count > 0 || Suggesting;
                sugLabel.Text = Suggesting ? L("正在找可以加速的地方…", "Looking for places to speed up…") : L($"建议加速 {Suggestions.Count} 处", $"{Suggestions.Count} speed-up suggestions");
                sugAccept.Visible = sugIgnore.Visible = !Suggesting && Suggestions.Count > 0;
                annotateBtn.BackColor = Tool != null ? Theme.Accent : Theme.Control; annotateBtn.ForeColor = Tool != null ? Color.White : Theme.Text;
                suggestBtn.Enabled = !Suggesting;
                if (Tool != null) preview.Hint = ToolHint;
                preview.Invalidate();
            }
            finally { syncingW4 = false; }
        }

        bool DeleteSelectedW4()
        {
            if (SelectedRed != null) { var id = SelectedRed; Edit(p => p.Redactions.RemoveAll(r => r.Id == id)); Select(null, null); return true; }
            if (SelectedAnn != null) { var id = SelectedAnn; Edit(p => p.Annotations.RemoveAll(a => a.Id == id)); Select(null, null); return true; }
            if (SelectedMusic != null) { var id = SelectedMusic; Edit(p => p.Music.Clips.RemoveAll(c => c.Id == id)); Select(null, null); return true; }
            return false;
        }

        // 复制选中的标注 / 遮挡：往右下错开一点，选中新的
        public void DuplicateOverlay()
        {
            if (SelectedAnn != null)
            {
                var a = Project.Annotations.FirstOrDefault(x => x.Id == SelectedAnn); if (a == null) return;
                var c = a.Clone(); c.Id = Guid.NewGuid().ToString().ToUpperInvariant(); c.X += 0.03; c.Y += 0.03;
                Edit(p => p.Annotations.Add(c)); Select("ann", c.Id);
            }
            else if (SelectedRed != null)
            {
                var r = Project.Redactions.FirstOrDefault(x => x.Id == SelectedRed); if (r == null) return;
                var c = r.Clone(); c.Id = Guid.NewGuid().ToString().ToUpperInvariant(); c.X = Math.Min(c.X + 0.03, 1 - c.W); c.Y = Math.Min(c.Y + 0.03, 1 - c.H);
                Edit(p => p.Redactions.Add(c)); Select("red", c.Id);
            }
        }

        // ---- 推近段的速度（跟全局 / 慢 / 标准 / 快） ----
        readonly Dictionary<string, Button> speedButtons = new Dictionary<string, Button>();
        static readonly (string key, double? v)[] Speeds = { ("auto", null), ("slow", 0.6), ("std", 1.0), ("fast", 1.7) };
        void AddZoomSpeed(FlowLayoutPanel flow)
        {
            flow.Controls.Add(new Label { Text = L("速度", "Speed"), AutoSize = true, Margin = new Padding(10, 8, 4, 0) });
            foreach (var (key, v) in Speeds)
            {
                var b = new Button(); var vv = v;
                Flat(b, key == "auto" ? L("跟全局", "Default") : key == "slow" ? L("慢", "Slow") : key == "std" ? L("标准", "Normal") : L("快", "Fast"), (s, e) => SetSegSpeed(vv));
                tips.SetToolTip(b, L("这一段镜头推近、拉远、跟随的快慢（跟全局 = 右边「推近」里的设置）", "How fast the camera moves for this zoom (Default = the Zoom setting on the right)"));
                speedButtons[key] = b; flow.Controls.Add(b);
            }
        }
        public void SetSegSpeed(double? v) { var id = Selected; Edit(p => { var sg = p.Segments.FirstOrDefault(x => x.Id == id); if (sg != null) sg.Speed = v; }); Touch(); }
        void SyncZoomSpeed(ZoomSegment seg)
        {
            foreach (var (key, v) in Speeds)
            {
                bool on = v == null ? seg.Speed == null : seg.Speed is double sp && Math.Abs(sp - v.Value) < 1e-6;
                var b = speedButtons[key]; b.BackColor = on ? Theme.Accent : Theme.Control; b.ForeColor = on ? Color.White : Theme.Text;
            }
        }

        // ---- 背景音乐 ----
        public void ChooseMusic()
        {
            using (var d = new OpenFileDialog { Filter = L("音乐", "Audio") + "|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.wma|*.*|*.*", Title = L("选一首背景音乐", "Choose background music") })
                if (d.ShowDialog(this) == DialogResult.OK) AddMusic(d.FileName);
        }

        // 拷进素材夹 music\，按成片时间接在已有音乐后面（没有就从开头）；返回新段，不行返回 null
        public MusicClip AddMusic(string file)
        {
            try
            {
                var mdir = Path.Combine(Dir, "music"); Directory.CreateDirectory(mdir);
                var name = Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + Path.GetFileName(file);
                var dst = Path.Combine(mdir, name);
                File.Copy(file, dst, true);
                double len = MusicMix.Duration(dst);
                if (len < 0.2) { try { File.Delete(dst); } catch { } System.Media.SystemSounds.Beep.Play(); return null; }
                var map = new TimeMap(Project); double filmEnd = map.Out(Project.TrimEnd);
                double at = Project.Music.Clips.Select(c => c.Start + c.Length).DefaultIfEmpty(Project.TrimStart).Max();
                var clip = new MusicClip { File = "music/" + name, Name = Path.GetFileNameWithoutExtension(file), Start = Math.Min(at, Math.Max(Project.TrimStart, filmEnd - 0.5)), TrimIn = 0, TrimOut = len, FileLength = len };
                Edit(p => p.Music.Clips.Add(clip));
                Select("music", clip.Id);
                Peaks(clip.File);
                return clip;
            }
            catch { System.Media.SystemSounds.Beep.Play(); return null; }
        }

        // ---- 建议加速 ----
        public void SuggestSpeed()
        {
            if (Suggesting) return;
            Suggesting = true; Suggestions = new List<SpeedSuggestion>(); Select(null, null);
            double a = Project.TrimStart, b = Math.Max(Project.TrimStart, Project.TrimEnd);
            var cuts = Project.Cuts.Select(c => c.Clone()).ToList(); var tr = Track; var raw = Raw;
            Changed?.Invoke();
            Task.Run(() =>
            {
                List<SpeedSuggestion> found;
                try { found = SpeedSuggest.Find(tr, SpeedSuggest.FrameDiffs(raw, a, b), a, b, cuts); } catch { found = new List<SpeedSuggestion>(); }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        Suggesting = false; Suggestions = found;
                        if (found.Count == 0) System.Media.SystemSounds.Beep.Play();
                        Changed?.Invoke();
                    }));
                }
                catch { }
            });
        }

        // 采纳：变成正式的快放段（能撤销）；和已有剪辑段重叠的跳过
        public void AcceptSuggestions(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            var pick = Suggestions.Where(g => set.Contains(g.Id)).ToList();
            Edit(p =>
            {
                foreach (var g in pick)
                    if (!p.Cuts.Any(c => c.Start < g.End && c.End > g.Start))
                        p.Cuts.Add(new CutSegment { Start = Math.Max(g.Start, p.TrimStart), End = Math.Min(g.End, p.TrimEnd), Speed = true, Rate = g.Rate });
                p.Cuts = p.Cuts.OrderBy(c => c.Start).ToList();
            });
            Suggestions = Suggestions.Where(g => !set.Contains(g.Id)).ToList();
            Changed?.Invoke();
        }
        public void IgnoreSuggestions(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids);
            Suggestions = Suggestions.Where(g => !set.Contains(g.Id)).ToList();
            Changed?.Invoke();
        }
    }
}
