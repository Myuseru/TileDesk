using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 一个够用的 SVG path 解析器（只处理 d 属性，转成 GDI+ 的 GraphicsPath）。
    /// 支持 M/m L/l H/h V/v C/c Z/z —— 图标素材基本只会用到这些。
    /// 坐标系按 24x24（Material Icons 的标准 viewBox），画的时候再缩放。
    /// </summary>
    internal static class SvgPath
    {
        public static GraphicsPath Parse(string d)
        {
            GraphicsPath path = new GraphicsPath();
            if (string.IsNullOrEmpty(d)) return path;

            float cx = 0, cy = 0, startX = 0, startY = 0;
            int i = 0;
            char cmd = '\0';

            while (i < d.Length)
            {
                char ch = d[i];
                if (char.IsWhiteSpace(ch) || ch == ',') { i++; continue; }
                if (char.IsLetter(ch)) { cmd = ch; i++; }

                char up = char.ToUpperInvariant(cmd);
                bool rel = char.IsLower(cmd);

                if (up == 'Z') { path.CloseFigure(); cx = startX; cy = startY; cmd = '\0'; continue; }

                float a, b, c, e;
                if (up == 'M' || up == 'L')
                {
                    if (!Num(d, ref i, out a) || !Num(d, ref i, out b)) break;
                    float nx = rel ? cx + a : a, ny = rel ? cy + b : b;
                    if (up == 'M') { path.StartFigure(); startX = nx; startY = ny; }
                    else path.AddLine(cx, cy, nx, ny);
                    cx = nx; cy = ny;
                    if (up == 'M') cmd = rel ? 'l' : 'L';
                }
                else if (up == 'H')
                {
                    if (!Num(d, ref i, out a)) break;
                    float nx = rel ? cx + a : a;
                    path.AddLine(cx, cy, nx, cy); cx = nx;
                }
                else if (up == 'V')
                {
                    if (!Num(d, ref i, out a)) break;
                    float ny = rel ? cy + a : a;
                    path.AddLine(cx, cy, cx, ny); cy = ny;
                }
                else if (up == 'C')
                {
                    if (!Num(d, ref i, out a) || !Num(d, ref i, out b) ||
                        !Num(d, ref i, out c) || !Num(d, ref i, out e)) break;
                    float x1 = rel ? cx + a : a, y1 = rel ? cy + b : b;
                    float x2 = rel ? cx + c : c, y2 = rel ? cy + e : e;
                    if (!Num(d, ref i, out a) || !Num(d, ref i, out b)) break;
                    float x3 = rel ? cx + a : a, y3 = rel ? cy + b : b;
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x3, y3);
                    cx = x3; cy = y3;
                }
                else
                {
                    int j = i;
                    while (j < d.Length && !char.IsLetter(d[j])) j++;
                    i = j;
                }
            }
            return path;
        }

        private static bool Num(string s, ref int i, out float v)
        {
            v = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == '\t' ||
                                    s[i] == '\n' || s[i] == '\r')) i++;
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            bool dot = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { i++; continue; }
                if (c == '.' && !dot) { dot = true; i++; continue; }
                if ((c == 'e' || c == 'E') && i + 1 < s.Length) { i++; if (s[i] == '-' || s[i] == '+') i++; continue; }
                break;
            }
            if (i == start) return false;
            return float.TryParse(s.Substring(start, i - start),
                                  NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        public static GraphicsPath Icon(string d, RectangleF box, float iconScale)
        {
            GraphicsPath p = Parse(d);
            float size = Math.Min(box.Width, box.Height) * iconScale;
            float k = size / 24f;
            using (Matrix m = new Matrix())
            {
                m.Translate(box.X + (box.Width - size) / 2f, box.Y + (box.Height - size) / 2f);
                m.Scale(k, k);
                p.Transform(m);
            }
            return p;
        }
    }

    /// <summary>
    /// 左下角「正在播放」控件 —— 独立的分层窗口。
    ///
    /// 为什么不跟磁贴墙画在同一块画布上：那样每动一下进度条都要走一遍
    /// 3840x2064 的整屏合成。Windows 原生控件也是「每个控件一个窗口，各自重绘」。
    ///
    /// 窗口特性：WS_EX_LAYERED 逐像素透明 / WS_EX_NOACTIVATE 不抢焦点 /
    /// WS_EX_TOOLWINDOW 不进 Alt+Tab。不可见时直接 Hide()，一个像素都不画。
    /// </summary>
    internal class NowPlayingWindow : Form
    {
        // ---- Material Icons（Apache License 2.0）的 SVG path，viewBox 24x24 ----
        private const string IconPrev = "M6 6h2v12H6zm3.5 6l8.5 6V6z";
        private const string IconNext = "M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z";
        private const string IconPlay = "M8 5v14l11-7z";
        private const string IconPause = "M6 19h4V5H6v14zm8-14v14h4V5h-4z";

        private readonly NowPlayingMonitor mon;
        private readonly Config cfg;
        private readonly float scale;
        private readonly Timer tick;
        private readonly LayerSurface surface = new LayerSurface();

        // ---- 布局 ----
        private Rectangle rCover, rBar, rTitle, rTime, rPrev, rPlay, rNext;

        private Font fontTitle, fontArtist, fontTime;

        // ---- 交互 ----
        private int hover;
        private bool seeking;
        private static readonly bool SeekEnabled = false;
        private double seekRatio = -1;
        private int lastSec = -1;
        private Bitmap coverScaled;
        private object coverKey;
        private int lastVisQ = -1;
        private bool dirty = true;
        private bool shown;
        private float chromeFade = 1f;
        private float knobHot;

        private int panelW, panelH, panelOffX, panelOffY;

        // 遮挡检测结果缓存（每 400ms 才采样一次，别每帧 WindowFromPoint）
        private bool coveredNow;
        private int coveredCheckAt;

        private const int HoverNone = 0, HoverPrev = 1, HoverPlay = 2, HoverNext = 3, HoverBar = 4;

        public Action<Graphics, Rectangle> SpectrumPainter;
        public bool SpectrumActive;

        public NowPlayingWindow(NowPlayingMonitor monitor, Config config, float dpiScale)
        {
            mon = monitor;
            cfg = config;
            scale = dpiScale;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            AutoScaleMode = AutoScaleMode.None;

            int w = Math.Max(320, (int)Math.Round(cfg.npWidth * scale));
            int h = Math.Max(44, (int)Math.Round(cfg.npHeight * scale));
            int m = Math.Max(4, (int)Math.Round(cfg.npMargin * scale));
            panelW = w; panelH = h; panelOffX = m; panelOffY = m;
            int sw = SystemInformation.WorkingArea.Width;
            if (sw < w + m * 2) sw = w + m * 2;
            ClientSize = new Size(sw, h + m * 2);
            Relayout();

            tick = new Timer();
            tick.Interval = 33;
            tick.Tick += delegate { Pump(); };
            tick.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public Point PanelScreenLocation
        {
            get { return new Point(Location.X + panelOffX, Location.Y + panelOffY); }
        }

        public int BarCenterY
        {
            get { return rBar.IsEmpty ? -1 : rBar.Y + rBar.Height / 2 - panelOffY; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        // ================================================================
        //  生命周期
        // ================================================================

        public void Sync(IntPtr above, Point location, bool assertZ, float fade)
        {
            float want = fade;
            try
            {
                if (ClientRectangle.Contains(PointToClient(Cursor.Position))) want = 1f;
            }
            catch { }
            if (Math.Abs(chromeFade - want) > 0.003f) { chromeFade = want; dirty = true; }
            if (mon == null || IsDisposed) return;

            float v = mon.Visibility;
            if (v <= 0.01f)
            {
                if (shown) { shown = false; Hide(); }
                return;
            }
            if (!shown)
            {
                shown = true;
                Location = new Point(location.X - panelOffX, location.Y - panelOffY);
                Show();
                lastVisQ = -1;
                lastSec = -1;
                dirty = true;
            }
            else
            {
                Point wantLoc = new Point(location.X - panelOffX, location.Y - panelOffY);
                if (Location != wantLoc) Location = wantLoc;
            }

            if (assertZ && above != IntPtr.Zero && IsHandleCreated)
            {
                Native.SetWindowPos(above, Handle, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW);
            }
            Pump();
        }

        private void Pump()
        {
            if (!shown || mon == null || IsDisposed) return;

            if (tick != null)
            {
                int want = SpectrumActive ? 8 : 33;
                if (tick.Interval != want) tick.Interval = want;
                if (SpectrumActive) dirty = true;
            }

            float v = mon.Visibility;
            int visQ = (int)(v * 128f + 0.5f);
            if (visQ != lastVisQ) { lastVisQ = visQ; dirty = true; }

            NowPlayingState st = mon.Latest != null ? mon.Latest : mon.State;

            if (!rBar.IsEmpty)
            {
                Point cp = PointToClient(Cursor.Position);
                double rr = (seeking && seekRatio >= 0) ? seekRatio : st.Progress;
                float kx = rBar.X + rBar.Width * (float)rr;
                float ky = rBar.Y + rBar.Height / 2f;
                float hit = 12f * scale;
                bool kn = (cp.X - kx) * (cp.X - kx) + (cp.Y - ky) * (cp.Y - ky) <= hit * hit;
                float want = kn ? 1f : 0f;
                if (Math.Abs(knobHot - want) > 0.01f) { knobHot = want; dirty = true; }
            }

            if (!seeking && !rBar.IsEmpty)
            {
                int sec = (int)st.PositionNow.TotalSeconds;
                if (sec != lastSec) { lastSec = sec; dirty = true; }
                if (st.Playing) dirty = true;
            }
            if (dirty) { dirty = false; Render(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tick != null) { tick.Stop(); tick.Dispose(); }
                if (fontTitle != null) { fontTitle.Dispose(); fontTitle = null; }
                if (fontArtist != null) { fontArtist.Dispose(); fontArtist = null; }
                if (fontTime != null) { fontTime.Dispose(); fontTime = null; }
                if (coverScaled != null) { coverScaled.Dispose(); coverScaled = null; }
                surface.Dispose();
            }
            base.Dispose(disposing);
        }

        // ================================================================
        //  布局
        // ================================================================

        private void Relayout()
        {
            int w = panelW > 0 ? panelW : ClientSize.Width;
            int h = panelH > 0 ? panelH : ClientSize.Height;
            int pad = (int)Math.Round(4 * scale);
            int cover = h - pad * 2;
            rCover = new Rectangle(pad, pad, cover, cover);

            int left = rCover.Right + (int)Math.Round(16 * scale);
            int contentRight = (int)Math.Round(w * 0.60);

            int titleH = (int)Math.Round(26 * scale);
            rTitle = new Rectangle(left, pad + (int)Math.Round(6 * scale), contentRight - left, titleH);

            int barY = pad + titleH + (int)Math.Round(10 * scale);
            rBar = new Rectangle(left, barY, contentRight - left, (int)Math.Round(4 * scale));

            rTime = new Rectangle(left, rBar.Bottom + (int)Math.Round(5 * scale),
                                  contentRight - left + (int)Math.Round(120 * scale), (int)Math.Round(22 * scale));

            int bw = (int)Math.Round(44 * scale);
            int bh = h - pad * 2;
            int gap = (int)Math.Round(18 * scale);
            int total = bw * 3 + gap * 2;
            int bx = w - (int)Math.Round(w * 0.035) - total;
            int by = pad;
            rPrev = new Rectangle(bx, by, bw, bh);
            rPlay = new Rectangle(rPrev.Right + gap, by, bw, bh);
            rNext = new Rectangle(rPlay.Right + gap, by, bw, bh);

            rCover.Offset(panelOffX, panelOffY);
            rTitle.Offset(panelOffX, panelOffY);
            rBar.Offset(panelOffX, panelOffY);
            rTime.Offset(panelOffX, panelOffY);
            rPrev.Offset(panelOffX, panelOffY);
            rPlay.Offset(panelOffX, panelOffY);
            rNext.Offset(panelOffX, panelOffY);
        }

        private void EnsureFonts()
        {
            if (fontTitle != null) return;
            string fam = "Segoe UI";
            try { using (FontFamily probe = new FontFamily(fam)) { } }
            catch { fam = "Microsoft YaHei UI"; }
            fontTitle = new Font(fam, Math.Max(13f, 19f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontArtist = new Font(fam, Math.Max(10f, 14f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontTime = new Font(fam, Math.Max(9f, 12.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            surface.Release();
            Relayout();
            lastVisQ = -1;
            dirty = true;
        }

        // ================================================================
        //  遮挡检测
        // ================================================================

        private bool CoveredByOthers()
        {
            try
            {
                if (!Visible) return true;
                uint me = (uint)Process.GetCurrentProcess().Id;

                int py = panelOffY + panelH / 2;
                if (!ForeignAt(panelOffX + panelW / 4, py, me)) return false;
                if (!ForeignAt(panelOffX + panelW / 2, py, me)) return false;
                if (!ForeignAt(panelOffX + panelW * 3 / 4, py, me)) return false;

                int sy = ClientSize.Height - Math.Max(4, (int)Math.Round(8 * scale));
                if (!ForeignAt(ClientSize.Width / 4, sy, me)) return false;
                if (!ForeignAt(ClientSize.Width / 2, sy, me)) return false;
                if (!ForeignAt(ClientSize.Width * 3 / 4, sy, me)) return false;

                return true;
            }
            catch { return false; }
        }

        private bool ForeignAt(int x, int y, uint me)
        {
            Point c = PointToScreen(new Point(x, y));
            IntPtr h = Native.WindowFromPoint(new Native.POINT(c.X, c.Y));
            if (h == IntPtr.Zero || h == Handle) return false;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            return pid != me;
        }

        // ================================================================
        //  绘制
        // ================================================================

        private void Render()
        {
            if (!shown || mon == null || IsDisposed) return;

            int now = Clock.NowMs;
            if (unchecked(now - coveredCheckAt) > 400)
            {
                coveredCheckAt = now;
                coveredNow = CoveredByOthers();
            }
            if (coveredNow) return;

            if (!surface.Ensure(ClientSize.Width, ClientSize.Height)) return;

            float vis = mon.Visibility;
            if (vis <= 0.01f) return;
            if (vis > 1f) vis = 1f;

            NowPlayingState st = mon.Latest != null ? mon.Latest : mon.State;
            EnsureFonts();
            int A = (int)(255 * vis);

            Graphics g = surface.G;
            surface.ClearAll();

            if (SpectrumPainter != null)
            {
                try { SpectrumPainter(g, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)); }
                catch (Exception ex) { Config.Log("画频谱失败: " + ex.Message); }
            }

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // 命中区：分层窗口按像素 alpha 判定，全透明处鼠标会穿过去。
            // 垫一层 alpha=1 的黑，肉眼看不见但能接住点击。
            using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            {
                int hx = (int)Math.Round(8 * scale), hy = (int)Math.Round(10 * scale);
                Rectangle hp = rPrev; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                hp = rPlay; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                hp = rNext; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                hp = rBar; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                g.FillRectangle(hit, rCover);
            }

            DrawCover(g, st, vis);

            if (!string.IsNullOrEmpty(st.Title))
            {
                using (StringFormat sf = LeftFormat())
                using (SolidBrush b = new SolidBrush(Color.FromArgb(A, 255, 255, 255)))
                    g.DrawString(st.Title, fontTitle, b, rTitle, sf);

                if (!string.IsNullOrEmpty(st.Artist))
                {
                    SizeF tw = g.MeasureString(st.Title, fontTitle);
                    float ax = rTitle.X + tw.Width + 10 * scale;
                    RectangleF ar = new RectangleF(ax, rTitle.Y + 1.5f * scale,
                        Math.Max(0, rTitle.Right - ax), rTitle.Height);
                    if (ar.Width > 52 * scale)
                    {
                        using (StringFormat sf = LeftFormat())
                        using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(A * 0.62f), 214, 218, 226)))
                            g.DrawString(st.Artist, fontArtist, b, ar, sf);
                    }
                }
            }

            DrawBar(g, st, vis, A);

            string t1, t2;
            if (seeking && seekRatio >= 0 && st.Duration > TimeSpan.Zero)
            {
                t1 = Fmt(TimeSpan.FromSeconds(st.Duration.TotalSeconds * seekRatio));
                t2 = Fmt(st.Duration);
            }
            else { t1 = Fmt(st.PositionNow); t2 = Fmt(st.Duration); }
            using (StringFormat sf = LeftFormat())
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(A * 0.52f), 214, 218, 226)))
                g.DrawString(t1 + "  /  " + t2, fontTime, b, rTime, sf);

            bool canPrev = st.HasSession && st.CanPrev;
            bool canNext = st.HasSession && st.CanNext;
            bool canPlay = st.HasSession && (st.Playing ? st.CanPause : st.CanPlay);
            DrawIcon(g, rPrev, IconPrev, HoverPrev, canPrev, vis, 0.78f);
            DrawIcon(g, rPlay, st.Playing ? IconPause : IconPlay, HoverPlay, canPlay, vis, 0.70f);
            DrawIcon(g, rNext, IconNext, HoverNext, canNext, vis, 0.78f);

            surface.Present(Handle, Left, Top, Rectangle.Empty, 1f);
        }

        private void DrawCover(Graphics g, NowPlayingState st, float vis)
        {
            if (st.Cover == null) return;
            float rad = Math.Max(3f, 12 * scale);

            if (coverScaled == null || !ReferenceEquals(coverKey, st.Cover) ||
                coverScaled.Width != rCover.Width || coverScaled.Height != rCover.Height)
            {
                if (coverScaled != null) { coverScaled.Dispose(); coverScaled = null; }
                try
                {
                    Bitmap b = new Bitmap(Math.Max(1, rCover.Width), Math.Max(1, rCover.Height));
                    using (Graphics gg = Graphics.FromImage(b))
                    {
                        gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        gg.DrawImage(st.Cover, new Rectangle(0, 0, b.Width, b.Height));
                    }
                    coverScaled = b;
                    coverKey = st.Cover;
                }
                catch { coverScaled = null; }
            }
            if (coverScaled == null) return;

            // 柔和外扩阴影
            using (GraphicsPath sp = new GraphicsPath())
            {
                RectangleF sr = new RectangleF(rCover.X - 3 * scale, rCover.Y - 1 * scale,
                                               rCover.Width + 6 * scale, rCover.Height + 8 * scale);
                sp.AddEllipse(sr);
                using (PathGradientBrush pg = new PathGradientBrush(sp))
                {
                    pg.CenterColor = Color.FromArgb((int)(80 * vis), 0, 0, 0);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, 0, 0, 0) };
                    g.FillPath(pg, sp);
                }
            }

            using (GraphicsPath cp = Ui.Round(rCover, rad))
            {
                Region saved = g.Clip;
                g.SetClip(cp, CombineMode.Intersect);
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = vis;
                    ia.SetColorMatrix(cm);
                    g.DrawImage(coverScaled, rCover, 0, 0, coverScaled.Width, coverScaled.Height,
                                GraphicsUnit.Pixel, ia);
                }
                g.Clip = saved;
            }

            using (GraphicsPath cp = Ui.Round(rCover, rad))
            using (Pen pen = new Pen(Color.FromArgb((int)(58 * vis), 255, 255, 255), Math.Max(1f, scale)))
                g.DrawPath(pen, cp);
        }

        private void DrawBar(Graphics g, NowPlayingState st, float vis, int A)
        {
            double ratio = seeking && seekRatio >= 0 ? seekRatio : st.Progress;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            float th = rBar.Height;
            float trackTh = Math.Max(2f, th * 0.85f);

            RectangleF track = new RectangleF(rBar.X,
                rBar.Y + (rBar.Height - trackTh) / 2f, rBar.Width, trackTh);
            Ui.FillRounded(g, track, trackTh / 2f, Color.FromArgb((int)(46 * vis), 255, 255, 255));

            float fillW = rBar.Width * (float)ratio;
            if (fillW > 0.5f)
            {
                RectangleF fr = new RectangleF(rBar.X, track.Y, fillW, trackTh);
                Ui.FillRounded(g, fr, trackTh / 2f, Color.FromArgb((int)(236 * vis), 255, 255, 255));
            }

            float kr = (4.4f + 1.8f * knobHot) * scale;
            float kx = rBar.X + rBar.Width * (float)ratio;
            float ky = rBar.Y + rBar.Height / 2f;

            if (knobHot > 0.02f)
            {
                float gr = kr * 2.6f;
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(kx - gr, ky - gr, gr * 2, gr * 2);
                    using (PathGradientBrush pg = new PathGradientBrush(gp))
                    {
                        pg.CenterColor = Color.FromArgb((int)(110 * knobHot * vis), 255, 255, 255);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(pg, gp);
                    }
                }
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(44 * vis), 0, 0, 0)))
                g.FillEllipse(b, kx - kr, ky - kr + scale, kr * 2, kr * 2);
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(248 * vis), 255, 255, 255)))
                g.FillEllipse(b, kx - kr, ky - kr, kr * 2, kr * 2);
        }

        private void DrawIcon(Graphics g, Rectangle box, string icon, int hotId,
                              bool enabled, float vis, float iconScale)
        {
            bool hot = hover == hotId && enabled;
            float a = enabled ? vis * chromeFade * (hot ? 1f : 0.86f) : vis * chromeFade * 0.30f;
            if (a <= 0.01f) return;

            float cx = box.X + box.Width / 2f;
            float cy = box.Y + box.Height / 2f;
            float rr = Math.Min(box.Width, box.Height) * 0.44f * (hot ? 1.08f : 1f);

            if (hot)
            {
                using (GraphicsPath hp = new GraphicsPath())
                {
                    hp.AddEllipse(cx - rr, cy - rr, rr * 2, rr * 2);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(30 * vis * chromeFade), 255, 255, 255)))
                        g.FillPath(b, hp);
                }
            }

            using (GraphicsPath p = SvgPath.Icon(icon, box, iconScale * (hot ? 1.06f : 1f)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * a), 255, 255, 255)))
                g.FillPath(b, p);
        }

        private static StringFormat LeftFormat()
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }

        private static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int total = (int)t.TotalSeconds;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        // ================================================================
        //  鼠标
        // ================================================================

        private int HitControl(Point pt)
        {
            bool btnOn = chromeFade > 0.04f;
            if (btnOn)
            {
                if (rPrev.Contains(pt)) return HoverPrev;
                if (rPlay.Contains(pt)) return HoverPlay;
                if (rNext.Contains(pt)) return HoverNext;
            }
            Rectangle bar = rBar;
            bar.Inflate((int)Math.Round(8 * scale), (int)Math.Round(12 * scale));
            if (bar.Contains(pt)) return HoverBar;
            return HoverNone;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (seeking)
            {
                if (e.Button == MouseButtons.Left) { seekRatio = RatioAt(e.X); dirty = true; }
                return;
            }
            int h = HitControl(new Point(e.X, e.Y));
            if (h != hover) { hover = h; dirty = true; }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != HoverNone) { hover = HoverNone; dirty = true; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (SeekEnabled && HitControl(new Point(e.X, e.Y)) == HoverBar)
            {
                seeking = true;
                seekRatio = RatioAt(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (seeking)
            {
                double r = RatioAt(e.X);
                seeking = false;
                seekRatio = -1;
                NowPlayingState st = mon.Latest != null ? mon.Latest : mon.State;
                if (st.Duration > TimeSpan.Zero)
                {
                    TimeSpan to = TimeSpan.FromSeconds(st.Duration.TotalSeconds * r);
                    Config.Log("正在播放控件：拖动进度条 -> " + Fmt(to));
                    mon.Seek(to);
                }
                dirty = true;
                return;
            }

            int h = HitControl(new Point(e.X, e.Y));
            if (h == HoverPrev) { Config.Log("正在播放控件：上一首"); mon.Previous(); }
            else if (h == HoverNext) { Config.Log("正在播放控件：下一首"); mon.Next(); }
            else if (h == HoverPlay) { Config.Log("正在播放控件：播放/暂停"); mon.TogglePlayPause(); }
            dirty = true;
        }

        private double RatioAt(int x)
        {
            if (rBar.Width <= 0) return 0;
            double r = (x - rBar.X) / (double)rBar.Width;
            return r < 0 ? 0 : (r > 1 ? 1 : r);
        }
    }
}
