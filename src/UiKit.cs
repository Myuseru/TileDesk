using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 设置界面用的一组自绘控件。
    /// 原生 WinForms 控件在暗色下很难看（TrackBar 是亮白轨道、ComboBox 带白色下拉箭头），
    /// 这里全部自己画，和主界面一套视觉。
    /// </summary>
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(27, 29, 34);
        public static readonly Color Card = Color.FromArgb(38, 41, 48);
        public static readonly Color CardHi = Color.FromArgb(48, 52, 61);
        public static readonly Color Text = Color.FromArgb(232, 233, 238);
        public static readonly Color SubText = Color.FromArgb(150, 155, 166);
        public static readonly Color Track = Color.FromArgb(58, 63, 74);
        public static readonly Color Line = Color.FromArgb(46, 50, 59);

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

        public static void FillRounded(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void StrokeRounded(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }
    }

    // ================================================================

    internal class FlatSlider : Control
    {
        private int val;
        private bool drag;
        private bool hover;
        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler ValueChanged;

        public FlatSlider(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = (int)Math.Round(30 * S);
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

        private void FromMouse(int x)
        {
            int pad = Pad;
            int w = Width - pad * 2;
            if (w <= 0) return;
            double t = (x - pad) / (double)w;
            Value = (int)Math.Round(Math.Max(0, Math.Min(1, t)) * 1000);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            drag = true;
            Capture = true;
            FromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag) FromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            drag = false;
            Capture = false;
        }

        /// <summary>
        /// 兜底：捕获一旦被系统收走（窗口失活、Alt+Tab、被别的窗口抢焦点…），
        /// 必须把 drag 也清掉。否则 drag 永远为 true、Capture 又已经不在自己身上，
        /// 后续点击会莫名其妙地"点哪儿都没反应，只有滚轮还能用" ——
        /// 这正是设置界面偶发点不动的原因。
        /// </summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) drag = false;
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);

            int pad = Pad;
            int cy = Height / 2;
            float th = Math.Max(3f, 5f * S);
            float w = Width - pad * 2f;

            RectangleF track = new RectangleF(pad, cy - th / 2f, w, th);
            Ui.FillRounded(g, track, th / 2f, Ui.Track);

            float kx = pad + w * (val / 1000f);
            if (kx > pad + 1f)
            {
                RectangleF fill = new RectangleF(pad, cy - th / 2f, kx - pad, th);
                Ui.FillRounded(g, fill, th / 2f, Accent);
            }

            // 旋钮不再用纯白 —— 在深色面板上太扎眼。平时是柔和的浅灰，
            // 悬停/拖动时染上强调色并浮出一圈光晕（Win11 的滑条就是这个行为）。
            float kr = Math.Max(4f, 7f * S);
            RectangleF knob = new RectangleF(kx - kr, cy - kr, kr * 2, kr * 2);
            if (drag || hover)
            {
                float gr = kr * 2.1f;
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(kx - gr, cy - gr, gr * 2, gr * 2);
                    using (PathGradientBrush pg = new PathGradientBrush(gp))
                    {
                        pg.CenterColor = Color.FromArgb(70, Accent);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, Accent) };
                        g.FillPath(pg, gp);
                    }
                }
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillEllipse(b, knob.X, knob.Y + 1.2f * S, knob.Width, knob.Height);
            using (SolidBrush b = new SolidBrush((drag || hover) ? Accent : Color.FromArgb(206, 211, 219)))
                g.FillEllipse(b, knob);
            using (Pen pen = new Pen(Color.FromArgb(90, 0, 0, 0), Math.Max(1f, S)))
                g.DrawEllipse(pen, knob);
        }
    }

    // ================================================================

    internal class FlatToggle : Control
    {
        private bool chk;
        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler CheckedChanged;

        public FlatToggle(float scale)
        {
            S = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size((int)Math.Round(44 * S), (int)Math.Round(23 * S));
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilent(bool v) { chk = v; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Checked = !chk;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);

            RectangleF r = new RectangleF(0, (Height - (int)Math.Round(23 * S)) / 2f,
                Width, (int)Math.Round(23 * S));
            float rad = r.Height / 2f;

            Ui.FillRounded(g, r, rad, chk ? Accent : Ui.Track);

            float kr = r.Height / 2f - 2.5f * S;
            float kx = chk ? r.Right - kr - 2.5f * S : r.X + kr + 2.5f * S;
            float cy = r.Y + r.Height / 2f;
            using (SolidBrush b = new SolidBrush(chk ? Color.White : Color.FromArgb(190, 205, 208, 218)))
                g.FillEllipse(b, kx - kr, cy - kr, kr * 2, kr * 2);
        }
    }

    // ================================================================

    internal class FlatSegmented : Control
    {
        private string[] items = new string[0];
        private int sel;
        public float S = 1f;
        public Color Accent = Color.FromArgb(76, 154, 255);
        public event EventHandler SelectedIndexChanged;

        public FlatSegmented(float scale, string[] options)
        {
            S = scale;
            items = options;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = (int)Math.Round(32 * S);
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                int v = Math.Max(0, Math.Min(items.Length - 1, value));
                if (v == sel) return;
                sel = v;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public void SetSilent(int v) { sel = Math.Max(0, Math.Min(items.Length - 1, v)); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (items.Length == 0) return;
            float w = Width / (float)items.Length;
            SelectedIndex = (int)(e.X / w);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);
            if (items.Length == 0) return;

            RectangleF all = new RectangleF(0, 0, Width, Height);
            float rad = 7 * S;
            Ui.FillRounded(g, all, rad, Ui.Card);

            float w = Width / (float)items.Length;
            RectangleF selRect = new RectangleF(sel * w + 2 * S, 2 * S, w - 4 * S, Height - 4 * S);
            Ui.FillRounded(g, selRect, rad * 0.75f, Accent);

            using (Font f = new Font("Microsoft YaHei UI", 9f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                for (int i = 0; i < items.Length; i++)
                {
                    RectangleF r = new RectangleF(i * w, 0, w, Height);
                    using (SolidBrush b = new SolidBrush(i == sel ? Color.White : Ui.SubText))
                        g.DrawString(items[i], f, b, r, sf);
                }
            }
        }
    }

    // ================================================================

    internal class FlatButton : Control
    {
        private bool hot, down;
        public float S = 1f;
        public bool Primary;
        public Color Accent = Color.FromArgb(76, 154, 255);

        public FlatButton(float scale, string text)
        {
            S = scale;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Height = (int)Math.Round(34 * S);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);

            RectangleF r = new RectangleF(0, 0, Width, Height);
            float rad = 7 * S;
            Color bg = Primary ? (hot ? Lighten(Accent, 0.12f) : Accent)
                               : (down ? Ui.CardHi : (hot ? Ui.CardHi : Ui.Card));
            Ui.FillRounded(g, r, rad, bg);
            if (!Primary)
                Ui.StrokeRounded(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), rad,
                    hot ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255), Math.Max(1f, S));

            using (Font f = new Font("Microsoft YaHei UI", 9f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(Primary ? Color.White : Ui.Text))
                    g.DrawString(Text, f, b, r, sf);
            }
        }

        private static Color Lighten(Color c, float t)
        {
            return Color.FromArgb(c.A,
                (int)Math.Min(255, c.R + (255 - c.R) * t),
                (int)Math.Min(255, c.G + (255 - c.G) * t),
                (int)Math.Min(255, c.B + (255 - c.B) * t));
        }
    }

    // ================================================================

    /// <summary>暗色的单行输入框（WinForms 没有内置的 InputBox）。</summary>
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
            ClientSize = new Size(P(400), P(150));
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            Font = new Font("Microsoft YaHei UI", 9f);

            Label lab = new Label();
            lab.Text = prompt;
            lab.AutoSize = false;
            lab.Location = new Point(P(18), P(16));
            lab.Size = new Size(P(364), P(44));
            lab.ForeColor = Ui.Text;
            Controls.Add(lab);

            box = new TextBox();
            box.Text = initial;
            box.Location = new Point(P(18), P(64));
            box.Size = new Size(P(364), P(26));
            box.BackColor = Ui.Card;
            box.ForeColor = Ui.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(box);

            FlatButton cancel = new FlatButton(s, "取消");
            cancel.Size = new Size(P(96), P(32));
            cancel.Location = new Point(ClientSize.Width - P(18) - P(96), P(102));
            cancel.Click += delegate(object o, EventArgs e) { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            FlatButton ok = new FlatButton(s, "确定");
            ok.Primary = true;
            ok.Size = new Size(P(96), P(32));
            ok.Location = new Point(cancel.Left - P(8) - P(96), P(102));
            ok.Click += delegate(object o, EventArgs e) { Result = box.Text; DialogResult = DialogResult.OK; Close(); };
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
