using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 视觉基础：调色板、圆角、柔和投影。
    ///
    /// 配色按 Windows 11 深色主题取（不是纯黑，是带一点冷调的炭灰），
    /// 层级用「表面亮度」表达：窗口 &lt; 卡片 &lt; 悬停 &lt; 按下。
    /// </summary>
    internal static class Ui
    {
        public static readonly Color WindowBg = Color.FromArgb(27, 28, 31);
        public static readonly Color WindowBg2 = Color.FromArgb(21, 22, 25);
        public static readonly Color Card = Color.FromArgb(40, 41, 46);
        public static readonly Color CardHover = Color.FromArgb(49, 50, 56);
        public static readonly Color CardHi = Color.FromArgb(58, 60, 67);
        public static readonly Color Text = Color.FromArgb(243, 244, 247);
        public static readonly Color SubText = Color.FromArgb(156, 160, 170);
        public static readonly Color Faint = Color.FromArgb(112, 116, 126);
        public static readonly Color Track = Color.FromArgb(62, 64, 72);
        public static readonly Color Line = Color.FromArgb(52, 54, 61);

        // 兼容旧名字
        public static readonly Color Bg = WindowBg;

        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);
        }

        /// <summary>
        /// sRGB 相对亮度（WCAG 口径，0 = 纯黑，1 = 纯白）。
        /// 不是简单的 (R+G+B)/3 —— 人眼对绿最敏感、对蓝最迟钝，
        /// 用算术平均会把 #FFEE00 和 #0000FF 判成"一样亮"。
        /// </summary>
        public static double Luminance(Color c)
        {
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        private static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// 铺在某块底色上的文字该用什么颜色。
        ///
        /// ★ 原来强调色的按钮文字**永远写死白色**：用户把强调色调成浅黄 / 浅青之后，
        ///   白字压在浅底上对比度只有 1.2 左右 —— 基本看不见。
        ///   这里按 WCAG 的对比度公式两种都算一遍，取对比度高的那一个。
        /// </summary>
        public static Color OnColor(Color bg)
        {
            double l = Luminance(bg);
            double contrastWhite = 1.05 / (l + 0.05);
            double contrastBlack = (l + 0.05) / 0.05;
            if (contrastWhite >= contrastBlack) return Color.FromArgb(255, 255, 255);
            // 不用纯黑：深灰压在浅色强调色上更像 Fluent 的 "on-accent" 前景
            return Color.FromArgb(24, 26, 31);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            float max = Math.Min(r.Width, r.Height);
            if (d > max) d = max;
            if (d <= 0.5f) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Round(Rectangle r, float radius)
        {
            return Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
        }

        public static void FillRounded(Graphics g, RectangleF r, float rad, Color c)
        {
            if (c.A <= 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void StrokeRounded(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            if (c.A <= 0 || w <= 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }

        /// <summary>1px 发丝线（用填充矩形而不是 DrawLine，避免半像素模糊）。</summary>
        public static void HairLine(Graphics g, float x, float y, float w, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillRectangle(b, x, y, w, 1f);
        }
    }

    // ================================================================
    //  轻量动画驱动
    //
    //  设置界面里十几个控件，每个都开一个 15ms 的 Timer 太浪费。
    //  这里共用一个：谁在动就注册进来，停下来自动退出（列表空了就停表）。
    // ================================================================

    internal interface IUiAnimated
    {
        /// <summary>推进一步；返回 false 表示已经收敛，可以摘掉。</summary>
        bool Advance(float dt);
    }

    internal static class UiAnim
    {
        private static readonly List<IUiAnimated> items = new List<IUiAnimated>();
        private static System.Windows.Forms.Timer timer;

        public static void Add(IUiAnimated a)
        {
            if (a == null || items.Contains(a)) return;
            items.Add(a);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 15;
                timer.Tick += Tick;
            }
            if (!timer.Enabled) timer.Start();
        }

        public static void Remove(IUiAnimated a) { items.Remove(a); }

        private static void Tick(object s, EventArgs e)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                bool keep;
                try { keep = items[i].Advance(0.015f); }
                catch { keep = false; }
                if (!keep) items.RemoveAt(i);
            }
            if (items.Count == 0 && timer != null) timer.Stop();
        }
    }

    // ================================================================
    //  滑块
    // ================================================================

    internal class FlatSlider : Control, IUiAnimated
    {
        private int val;
        private bool drag;
        private float hover;      // 0..1
        private float grow;       // 轨道变粗的程度 0..1

        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler ValueChanged;

        public FlatSlider(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = (int)Math.Round(32 * S);
            BackColor = Ui.Card;
        }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(0, Math.Min(1000, value));
                if (v == val) return;
                val = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilent(int value) { val = Math.Max(0, Math.Min(1000, value)); Invalidate(); }

        private int Pad { get { return (int)Math.Round(9 * S); } }

        /// <summary>右侧给数值标签预留的宽度：轨道和旋钮不会进入这条区域。</summary>
        public int ValueInset { get; set; }

        private int TrackWidth { get { return Width - Pad * 2 - ValueInset; } }

        private void FromMouse(int x)
        {
            int pad = Pad;
            int w = TrackWidth;
            if (w <= 0) return;
            double t = (x - pad) / (double)w;
            Value = (int)Math.Round(Math.Max(0, Math.Min(1, t)) * 1000);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            drag = true;
            Capture = true;
            FromMouse(e.X);
            UiAnim.Add(this);
        }

        protected override void OnMouseMove(MouseEventArgs e) { if (drag) FromMouse(e.X); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            drag = false;
            Capture = false;
            UiAnim.Add(this);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) drag = false;
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e) { UiAnim.Add(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { UiAnim.Add(this); base.OnMouseLeave(e); }

        public bool Advance(float dt)
        {
            bool hot = (drag || ClientRectangle.Contains(PointToClient(Cursor.Position)));
            hover = Ease.Toward(hover, hot ? 1f : 0f, 16f, dt);
            grow = Ease.Toward(grow, hot ? 1f : 0f, 16f, dt);
            Invalidate();
            return Math.Abs(hover - (hot ? 1f : 0f)) > 0.01f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int pad = Pad;
            int cy = Height / 2;
            float th = Math.Max(3f, 4f * S) + 1.5f * S * grow;
            float w = TrackWidth;

            RectangleF track = new RectangleF(pad, cy - th / 2f, w, th);
            Ui.FillRounded(g, track, th / 2f, Ui.Track);

            float kx = pad + w * (val / 1000f);
            if (kx > pad + 1f)
            {
                RectangleF fill = new RectangleF(pad, cy - th / 2f, kx - pad, th);
                Ui.FillRounded(g, fill, th / 2f, Accent);
            }

            float kr = Math.Max(4f, 6.5f * S) + 1.2f * S * grow;
            RectangleF knob = new RectangleF(kx - kr, cy - kr, kr * 2, kr * 2);

            if (hover > 0.02f)
            {
                float gr = kr * (2.0f + 0.5f * hover);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(kx - gr, cy - gr, gr * 2, gr * 2);
                    using (PathGradientBrush pg = new PathGradientBrush(gp))
                    {
                        pg.CenterColor = Color.FromArgb((int)(58 * hover), Accent);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, Accent) };
                        g.FillPath(pg, gp);
                    }
                }
            }

            using (SolidBrush b = new SolidBrush(Color.FromArgb(56, 0, 0, 0)))
                g.FillEllipse(b, knob.X, knob.Y + 1.2f * S, knob.Width, knob.Height);
            using (SolidBrush b = new SolidBrush(Ui.Mix(Color.FromArgb(214, 218, 226), Color.White, hover)))
                g.FillEllipse(b, knob);
        }
    }

    // ================================================================
    //  开关
    // ================================================================

    internal class FlatToggle : Control, IUiAnimated
    {
        private bool chk;
        private float t;          // 0 = 关，1 = 开
        private float hover;

        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler CheckedChanged;

        public FlatToggle(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // ★ 控件要比胶囊大一圈：胶囊的圆角端头若正好顶满控件边界，
            //   抗锯齿的最外圈像素会被控件裁掉，两端看起来就是平的（"被截断"）。
            Size = new Size((int)Math.Round(50 * S), (int)Math.Round(27 * S));
            Cursor = Cursors.Hand;
            BackColor = Ui.Card;
        }

        public bool Checked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value;
                UiAnim.Add(this);
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilent(bool v) { chk = v; t = v ? 1f : 0f; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Checked = !chk;
        }

        protected override void OnMouseEnter(EventArgs e) { UiAnim.Add(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { UiAnim.Add(this); base.OnMouseLeave(e); }

        public bool Advance(float dt)
        {
            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            hover = Ease.Toward(hover, hot ? 1f : 0f, 16f, dt);
            float target = chk ? 1f : 0f;
            t = Ease.Toward(t, target, 20f, dt);
            Invalidate();
            return Math.Abs(t - target) > 0.005f || Math.Abs(hover - (hot ? 1f : 0f)) > 0.01f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            float h = Math.Max(18f, 23f * S);
            float inset = 2f * S;
            RectangleF r = new RectangleF(inset, (Height - h) / 2f, Width - inset * 2, h);
            float rad = r.Height / 2f;

            Color off = Ui.Mix(Ui.Track, Ui.CardHi, hover);
            Color on = Ui.Mix(Accent, Color.White, 0.10f * hover);
            Ui.FillRounded(g, r, rad, Ui.Mix(off, on, t));

            if (t > 0.02f)
            {
                using (Pen pen = new Pen(Color.FromArgb((int)(60 * t), 255, 255, 255), Math.Max(1f, S)))
                    g.DrawPath(pen, Ui.Round(r, rad));
            }

            float kr = r.Height / 2f - 2.6f * S;
            // ★ 旋钮圆心必须从「r.X + kr + 边距」出发，而不是「r.X + 2.6S」。
            //   原式里边距(2.6S≈5px)远小于旋钮半径(≈18px)，OFF 时旋钮向左伸出
            //   胶囊十几像素，直接被控件边界裁掉 —— 用户截图里"开关被截断"
            //   裁的就是旋钮左半边。
            float gap = 2.6f * S;
            float kxOff = r.X + kr + gap;
            float kxOn = r.Right - kr - gap;
            float kx = kxOff + (kxOn - kxOff) * Ease.OutCubic(t);
            float cy = r.Y + r.Height / 2f;

            using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                g.FillEllipse(b, kx - kr, cy - kr + 0.8f * S, kr * 2, kr * 2);
            using (SolidBrush b = new SolidBrush(Ui.Mix(Color.FromArgb(186, 192, 204), Color.White, t)))
                g.FillEllipse(b, kx - kr, cy - kr, kr * 2, kr * 2);
        }
    }

    // ================================================================
    //  分段选择（滑动胶囊）
    // ================================================================

    internal class FlatSegmented : Control, IUiAnimated
    {
        private string[] items = new string[0];
        private int sel;
        private float pos;       // 胶囊位置（以"项"为单位，可以是小数）
        private float hover;

        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler SelectedIndexChanged;

        public FlatSegmented(float scale, string[] options)
        {
            S = scale;
            items = options ?? new string[0];
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = (int)Math.Round(32 * S);
            Cursor = Cursors.Hand;
            BackColor = Ui.Card;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = Math.Max(0, Math.Min(items.Length - 1, value));
                if (v == sel) return;
                sel = v;
                UiAnim.Add(this);
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilent(int v)
        {
            sel = Math.Max(0, Math.Min(Math.Max(0, items.Length - 1), v));
            pos = sel;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (items.Length == 0) return;
            float w = Width / (float)items.Length;
            SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, (int)(e.X / w)));
        }

        protected override void OnMouseEnter(EventArgs e) { UiAnim.Add(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { UiAnim.Add(this); base.OnMouseLeave(e); }

        public bool Advance(float dt)
        {
            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            hover = Ease.Toward(hover, hot ? 1f : 0f, 16f, dt);
            pos = Ease.Toward(pos, sel, 20f, dt);
            Invalidate();
            return Math.Abs(pos - sel) > 0.004f || Math.Abs(hover - (hot ? 1f : 0f)) > 0.01f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (items.Length == 0) return;

            RectangleF all = new RectangleF(0, 0, Width, Height);
            float rad = 8 * S;
            Ui.FillRounded(g, all, rad, Ui.Mix(Ui.Track, Ui.CardHi, hover * 0.5f));

            float w = Width / (float)items.Length;
            RectangleF pill = new RectangleF(pos * w + 2 * S, 2 * S, w - 4 * S, Height - 4 * S);
            Ui.FillRounded(g, pill, rad * 0.72f, Accent);

            using (Font f = new Font("Microsoft YaHei UI", 8.8f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                Color onPill = Ui.OnColor(Accent);
                for (int i = 0; i < items.Length; i++)
                {
                    RectangleF r = new RectangleF(i * w, 0, w, Height);
                    // 胶囊下面的项用"on-accent"色（浅强调色时自动转深色），其余用次级灰
                    float k = 1f - Math.Min(1f, Math.Abs(pos - i));
                    Color c = Ui.Mix(Ui.SubText, onPill, k);
                    using (SolidBrush b = new SolidBrush(c))
                        g.DrawString(items[i], f, b, r, sf);
                }
            }
        }
    }

    // ================================================================
    //  按钮
    // ================================================================

    internal class FlatButton : Control, IUiAnimated
    {
        private float hover, press;
        public float S = 1f;
        public bool Primary;
        public bool Danger;
        public Color Accent = Color.FromArgb(76, 154, 255);

        public FlatButton(float scale, string text)
        {
            S = scale;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = (int)Math.Round(34 * S);
            Cursor = Cursors.Hand;
            BackColor = Ui.WindowBg;
        }

        protected override void OnMouseEnter(EventArgs e) { UiAnim.Add(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { UiAnim.Add(this); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            press = 1f; UiAnim.Add(this); base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            press = 0f; UiAnim.Add(this); base.OnMouseUp(e);
        }

        public bool Advance(float dt)
        {
            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            hover = Ease.Toward(hover, hot ? 1f : 0f, 18f, dt);
            bool down = (System.Windows.Forms.Control.MouseButtons & System.Windows.Forms.MouseButtons.Left) != 0;
            if (!down) press = Ease.Toward(press, 0f, 24f, dt);
            Invalidate();
            return Math.Abs(hover - (hot ? 1f : 0f)) > 0.01f || press > 0.01f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            float rad = 7 * S;

            Color fillC;
            if (Primary)
            {
                Color c = Ui.Mix(Accent, Color.White, 0.10f * hover);
                if (press > 0.01f) c = Ui.Mix(c, Color.Black, 0.10f * press);
                fillC = c;
                Ui.FillRounded(g, r, rad, c);
            }
            else
            {
                Color baseC = Ui.Mix(Ui.Card, Ui.CardHover, hover);
                if (press > 0.01f) baseC = Ui.Mix(baseC, Ui.CardHi, press);
                fillC = baseC;
                Ui.FillRounded(g, r, rad, baseC);
                Ui.StrokeRounded(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), rad,
                    Color.FromArgb((int)(34 + 34 * hover), 255, 255, 255), Math.Max(1f, S));
            }

            // ★ 文字色跟着**实际铺上去的那块底色**算，不写死白色：
            //   强调色被调成浅色时，白字直接消失在按钮里（用户实测）。
            Color fg = Primary ? Ui.OnColor(fillC)
                              : (Danger ? Color.FromArgb(255, 122, 122) : Ui.Text);
            using (Font f = new Font("Microsoft YaHei UI", 9f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(fg))
                    g.DrawString(Text, f, b, r, sf);
            }
        }
    }

    /// <summary>标题栏上的圆形关闭按钮。</summary>
    internal class FlatIconButton : Control, IUiAnimated
    {
        private float hover;
        public float S = 1f;
        public string Glyph = "\uE8BB";      // ChromeClose
        public Color Fore = Ui.SubText;
        public bool Danger;

        public FlatIconButton(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size((int)Math.Round(32 * S), (int)Math.Round(32 * S));
            Cursor = Cursors.Hand;
            BackColor = Ui.WindowBg;
        }

        protected override void OnMouseEnter(EventArgs e) { UiAnim.Add(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { UiAnim.Add(this); base.OnMouseLeave(e); }

        public bool Advance(float dt)
        {
            bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position));
            hover = Ease.Toward(hover, hot ? 1f : 0f, 20f, dt);
            Invalidate();
            return Math.Abs(hover - (hot ? 1f : 0f)) > 0.01f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            if (hover > 0.02f)
            {
                Color bg = Danger ? Color.FromArgb((int)(180 * hover), 196, 43, 28)
                                  : Color.FromArgb((int)(26 * hover), 255, 255, 255);
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(r);
                    using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                }
            }

            Font f;
            try { f = new Font("Segoe Fluent Icons", 10f * S); }
            catch { f = new Font("Segoe MDL2 Assets", 10f * S); }
            using (f)
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                Color c = Danger && hover > 0.5f ? Color.White : Ui.Mix(Fore, Ui.Text, hover);
                using (SolidBrush b = new SolidBrush(c))
                    g.DrawString(Glyph, f, b, r, sf);
            }
        }
    }

    // ================================================================
    //  卡片容器（圆角 + 细边 + 轻微高光）
    // ================================================================

    internal class CardPanel : Panel
    {
        public float S = 1f;
        public int Radius = 10;

        public CardPanel(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.WindowBg);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float rad = Radius * S;

            // 卡片表面用**纯色**，不加渐变高光：行内的控件（滑块/开关）各自
            // `g.Clear(BackColor)`，一旦卡片有渐变，控件矩形就会在卡片上留下
            // 一块块颜色略深的方形补丁（实测：右侧一条条竖着的灰块）。
            // 立体感交给 1px 描边和窗口背景的渐变。
            using (GraphicsPath p = Ui.Round(r, rad))
            using (SolidBrush b = new SolidBrush(Ui.Card))
                g.FillPath(b, p);

            Ui.StrokeRounded(g, r, rad, Color.FromArgb(26, 255, 255, 255), Math.Max(1f, S));
        }
    }

    // ================================================================
    //  暗色输入框（原生 TextBox 换不了边框色，套一层自绘外壳）
    // ================================================================

    internal class FlatTextBox : Control
    {
        private readonly TextBox box;
        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler TextValueChanged;

        public FlatTextBox(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = (int)Math.Round(34 * S);
            BackColor = Ui.Card;

            box = new TextBox();
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.FromArgb(30, 31, 36);
            box.ForeColor = Ui.Text;
            box.Font = new Font("Microsoft YaHei UI", 9.5f);
            box.TextChanged += delegate
            {
                if (TextValueChanged != null) TextValueChanged(this, EventArgs.Empty);
            };
            Controls.Add(box);
        }

        public override string Text
        {
            get { return box.Text; }
            set { box.Text = value; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (box != null)
                box.Bounds = new Rectangle(
                    (int)Math.Round(10 * S), (Height - box.PreferredHeight) / 2,
                    Math.Max(10, Width - (int)Math.Round(20 * S)), box.PreferredHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            float rad = 6 * S;
            bool focused = box != null && box.Focused;

            Ui.FillRounded(g, r, rad, Color.FromArgb(30, 31, 36));
            Ui.StrokeRounded(g, r, rad,
                focused ? Accent : Color.FromArgb(44, 255, 255, 255), Math.Max(1f, S));
        }
    }

    // ================================================================
    //  暗色输入对话框
    // ================================================================

    internal class InputDialog : Form
    {
        private TextBox box;
        public string Result = "";

        public static string Ask(IWin32Window owner, string title, string prompt, string initial)
        {
            using (InputDialog d = new InputDialog(title, prompt, initial))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                return d.Result;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle);
            Native.RoundCorners(Handle);
        }

        public InputDialog(string title, string prompt, string initial)
        {
            float s = 1f;
            int dpi = Native.GetWindowDpi(IntPtr.Zero);
            if (dpi > 0) s = dpi / 96f;
            Func<int, int> P = delegate(int v) { return (int)Math.Round(v * s); };

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(P(420), P(178));
            BackColor = Ui.WindowBg;
            ForeColor = Ui.Text;
            Font = new Font("Microsoft YaHei UI", 9f);
            Padding = new Padding(0);

            Label lab = new Label();
            lab.Text = prompt;
            lab.AutoSize = false;
            lab.Location = new Point(P(20), P(18));
            lab.Size = new Size(P(380), P(50));
            lab.ForeColor = Ui.SubText;
            lab.BackColor = Ui.WindowBg;
            Controls.Add(lab);

            box = new TextBox();
            box.Text = initial;
            box.Location = new Point(P(20), P(74));
            box.Size = new Size(P(380), P(26));
            box.BackColor = Color.FromArgb(30, 31, 36);
            box.ForeColor = Ui.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(box);

            FlatButton cancel = new FlatButton(s, "取消");
            cancel.Size = new Size(P(96), P(34));
            cancel.Location = new Point(ClientSize.Width - P(20) - P(96), P(120));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            FlatButton ok = new FlatButton(s, "确定");
            ok.Primary = true;
            ok.Size = new Size(P(96), P(34));
            ok.Location = new Point(cancel.Left - P(10) - P(96), P(120));
            ok.Click += delegate { Result = box.Text; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(ok);

            AcceptButton = null;
            box.KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { Result = box.Text; DialogResult = DialogResult.OK; Close(); }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }
    }
}
