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
    /// 一个够用的 SVG path 解析器（只处理 path 的 d 属性，转成 GDI+ 的 GraphicsPath）。
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
    /// 左下角「正在播放」控件 —— **独立的分层窗口**。
    ///
    /// 为什么不跟磁贴墙画在同一块画布上：那样每动一下进度条都要走一遍
    /// 3840x2064 的整屏合成。Windows 原生控件的做法是「每个控件一个窗口，各自重绘」，
    /// 这里就是等价的实现 —— 它只有 690x84 逻辑像素（设备像素约 1380x168），
    /// 自己画、自己上传，磁贴墙完全不参与。
    ///
    /// 窗口特性（和磁贴墙同源）：
    ///   WS_EX_LAYERED 逐像素透明 / WS_EX_NOACTIVATE 不抢焦点 /
    ///   WS_EX_TOOLWINDOW 不进 Alt+Tab 和任务栏。
    /// 不可见时直接 Hide()，一个像素都不画，也不会吃掉鼠标。
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

        // ---- 自己的画布 ----
        private Bitmap surface;
        private IntPtr memDc = IntPtr.Zero;
        private IntPtr dib = IntPtr.Zero;
        private int surfW, surfH;

        // ---- 布局 ----
        private Rectangle rCover, rBar, rTitle, rTime, rPrev, rPlay, rNext;

        private Font fontTitle, fontTime;

        // ---- 交互 ----
        private int hover;
        private bool seeking;

        /// <summary>
        /// 进度条拖动开关（当前关闭）。
        ///
        /// 为什么关：QQ 音乐的 SMTC 实现里 TryChangePlaybackPositionAsync 是个
        /// "永远返回 true 但位置纹丝不动"的空操作（实测：目标 251.4 秒，800ms 后
        /// 播放器自报 183.5 秒）。所以拖了没反应，用户会以为是我们坏了。
        /// 直接屏蔽，免得制造困惑。
        ///
        /// **下面的实现完整保留** —— seeking / seekRatio / RatioAt / Seek() 全都在。
        /// 以后遇到真正支持 SMTC 跳转的播放器（浏览器、VLC、Spotify 之类），
        /// 把这个常量改成 true 就能直接用。
        /// </summary>
        private static readonly bool SeekEnabled = false;
        private double seekRatio = -1;
        private int lastSec = -1;
        private Bitmap coverScaled;
        private object coverKey;
        private int npRenders = 0;
        private double npTotalMs = 0;
        private int lastVisQ = -1;
        private bool dirty = true;
        private bool shown;
        private float chromeFade = 1f;   // 按钮的淡出程度（1=全亮）
        private bool knobHot;   // 鼠标是否落在进度条小球上

        private const int HoverNone = 0, HoverPrev = 1, HoverPlay = 2, HoverNext = 3, HoverBar = 4;

        // ---- 底部长条：播放器面板在左，频谱画在同一条长条上 ----
        // 为什么要合并：磁贴墙的淡出是整块位图的整体透明度（SourceConstantAlpha），
        // 墙上没法"卡片淡出、频谱留着"。搬进播放器窗口（独立分层窗口）就都解决了。
        public Action<Graphics, Rectangle> SpectrumPainter;   // 频谱绘制（由磁贴墙注入）
        public bool SpectrumActive;                            // 有声音在放 → 驱动快速重绘
        private int panelW, panelH, panelOffX, panelOffY;

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

            // 长条 = 面板 + 四周留白。这样面板的屏幕位置和原来**完全一样**
            // （长条左上角往左上挪一个 margin），时钟/信息条的对齐不用改。
            int w = Math.Max(320, (int)Math.Round(cfg.npWidth * scale));
            int h = Math.Max(44, (int)Math.Round(cfg.npHeight * scale));
            int m = Math.Max(4, (int)Math.Round(cfg.npMargin * scale));
            panelW = w; panelH = h; panelOffX = m; panelOffY = m;
            int sw = SystemInformation.WorkingArea.Width;
            if (sw < w + m * 2) sw = w + m * 2;
            ClientSize = new Size(sw, h + m * 2);
            Config.Log("播放器长条: ClientSize=" + ClientSize.Width + "x" + ClientSize.Height +
                       "  面板=" + panelW + "x" + panelH + "  偏移=" + panelOffX + "," + panelOffY);
            Relayout();

            tick = new Timer();
            tick.Interval = 33;   // ~30fps：进度条连续，又不至于白烧 CPU
            tick.Tick += delegate { Pump(); };
            tick.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        /// <summary>面板左上角的屏幕坐标。长条比面板大一圈，外部要按面板对齐时用这个。</summary>
        public Point PanelScreenLocation
        {
            get { return new Point(Location.X + panelOffX, Location.Y + panelOffY); }
        }

        /// <summary>进度条中线在窗口内的 y（用来让右下角信息条和它对齐）。拿不到返回 -1。</summary>
        public int BarCenterY
        {
            // 注意减掉 panelOffY：调用方是拿它跟"面板的屏幕位置"做对齐的，
            // 而 rBar 是长条坐标系里的（比面板多一个 margin 的偏移）。不减会多算一次。
            get { return rBar.IsEmpty ? -1 : rBar.Y + rBar.Height / 2 - panelOffY; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE —— 点它不夺焦点
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW —— 不进 Alt+Tab / 任务栏
                return cp;
            }
        }

        // ================================================================
        //  生命周期
        // ================================================================

        /// <summary>由磁贴墙的 tick 调用：同步可见度、层级和位置。</summary>
        public void Sync(IntPtr above, Point location, bool assertZ, float fade)
        {
            // 播放器只淡"按钮"：封面/歌名/进度条保持可见，
            // 不然连在放什么歌都看不到了。
            //
            // 但**鼠标一进控件，按钮立刻恢复全亮**：
            // 按钮的"能不能点"是跟 chromeFade 绑的（HitControl 里 btnOn），
            // 而 chromeFade 跟着墙的空闲淡出走 —— 墙空闲 8 秒淡出后按钮就点不动了，
            // 用户的感觉正是"看得见按钮却按不了"。鼠标进来就点亮既符合直觉，
            // 也让"移过去→点"这条路径永远有效。
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

            // 层级：把**墙**插到自己下面（= 自己在墙上面）。
            // 注意 SetWindowPos(hWnd, hWndInsertAfter) 是把 hWnd 放到 hWndInsertAfter
            // 下面，所以这里传的是 (墙, 控件)。不能用 Owner 关系代替 —— 被拥有窗口
            // 点击时会先尝试激活拥有者，而墙是永不激活的最底层窗口，点击会被丢掉
            // （三个按钮直接点不动）。SetWindowPos 会改 z 序、逼 DWM 重新合成，
            // 所以由调用方节流（每 64 tick 一次）。
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

            // 频谱在跑：每帧都在变，重绘提到 ~8ms（60fps+）；否则 33ms 足够。
            if (tick != null)
            {
                int want = SpectrumActive ? 8 : 33;
                if (tick.Interval != want)
                {
                    tick.Interval = want;
                    Native.SetFastTimer(SpectrumActive);
                }
                if (SpectrumActive) dirty = true;
            }

            float v = mon.Visibility;
            int visQ = (int)(v * 128f + 0.5f);
            bool need = visQ != lastVisQ;
            if (need) lastVisQ = visQ;

            NowPlayingState st = mon.Latest != null ? mon.Latest : mon.State;
            // 小球命中判定：小球随播放一直在动，所以每帧按当前鼠标位置算一次。
            if (!rBar.IsEmpty)
            {
                Point cp = PointToClient(Cursor.Position);
                double rr = (seeking && seekRatio >= 0) ? seekRatio : st.Progress;
                float kx = rBar.X + rBar.Width * (float)rr;
                float ky = rBar.Y + rBar.Height / 2f;
                float hit = 12f * scale;   // 比球本身略大，好点中
                bool kn = (cp.X - kx) * (cp.X - kx) + (cp.Y - ky) * (cp.Y - ky) <= hit * hit;
                if (kn != knobHot) { knobHot = kn; dirty = true; }
            }

            if (!seeking && !rBar.IsEmpty)
            {
                int sec = (int)st.PositionNow.TotalSeconds;
                if (sec != lastSec) { lastSec = sec; need = true; }
                if (st.Playing) need = true;   // 播放中每帧都跟 —— 反正只有这一小块
            }
            if (need) dirty = true;
            if (dirty) { dirty = false; Render(); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 这个窗口由磁贴墙管，用户不该直接关掉它
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tick != null) { tick.Stop(); tick.Dispose(); }
                if (fontTitle != null) { fontTitle.Dispose(); fontTitle = null; }
                if (fontTime != null) { fontTime.Dispose(); fontTime = null; }
                if (coverScaled != null) { coverScaled.Dispose(); coverScaled = null; }
                ReleaseSurface();
            }
            base.Dispose(disposing);
        }

        // ================================================================
        //  布局
        // ================================================================

        private void Relayout()
        {
            // 面板内部布局用面板自己的尺寸算，算完再整体偏移到长条里的位置。
            int w = panelW > 0 ? panelW : ClientSize.Width;
            int h = panelH > 0 ? panelH : ClientSize.Height;
            int pad = (int)Math.Round(3 * scale);
            int cover = h - pad * 2;
            rCover = new Rectangle(pad, pad, cover, cover);

            int left = rCover.Right + (int)Math.Round(16 * scale);
            int contentRight = (int)Math.Round(w * 0.62);

            int titleH = (int)Math.Round(31 * scale);
            rTitle = new Rectangle(left, pad + (int)Math.Round(3 * scale), contentRight - left, titleH);

            int barY = pad + titleH + (int)Math.Round(9 * scale);
            rBar = new Rectangle(left, barY, contentRight - left, (int)Math.Round(5 * scale));

            rTime = new Rectangle(left, rBar.Bottom + (int)Math.Round(5 * scale),
                                  contentRight - left + (int)Math.Round(130 * scale), (int)Math.Round(24 * scale));

            int bw = (int)Math.Round(48 * scale);
            int bh = h - pad * 2;
            int gap = (int)Math.Round(26 * scale);
            int total = bw * 3 + gap * 2;
            int bx = w - (int)Math.Round(w * 0.03) - total;
            int by = pad;
            rPrev = new Rectangle(bx, by, bw, bh);
            rPlay = new Rectangle(rPrev.Right + gap, by, bw, bh);
            rNext = new Rectangle(rPlay.Right + gap, by, bw, bh);

            // 面板在长条里的偏移（长条比面板大一圈：左上各留一个 margin）
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
            // Segoe UI 在小字号下 hinting 好、西里尔/拉丁质量高；中文靠系统字体链接回退
            string fam = "Segoe UI";
            try { using (FontFamily probe = new FontFamily(fam)) { } }
            catch { fam = PickFontFamily(cfg.fontFamily); }
            fontTitle = new Font(fam, Math.Max(12f, 18.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontTime = new Font(fam, Math.Max(9.5f, 14.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private static string PickFontFamily(string want)
        {
            try
            {
                if (!string.IsNullOrEmpty(want))
                {
                    using (FontFamily ff = new FontFamily(want)) { }
                    return want;
                }
            }
            catch { }
            return "Microsoft YaHei UI";
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ReleaseSurface();
            Relayout();
            lastVisQ = -1;
            dirty = true;
        }

        // ================================================================
        //  自己那块画布
        // ================================================================

        private void ReleaseSurface()
        {
            lock (Gdi.Lock)
            {
                if (surface != null) { surface.Dispose(); surface = null; }
                if (memDc != IntPtr.Zero) { Native.DeleteDC(memDc); memDc = IntPtr.Zero; }
                if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
                surfW = surfH = 0;
            }
        }

        private bool EnsureSurface()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return false;
            if (surface != null && surfW == w && surfH == h) return true;

            lock (Gdi.Lock)
            {
                if (surface != null) { surface.Dispose(); surface = null; }
                if (memDc != IntPtr.Zero) { Native.DeleteDC(memDc); memDc = IntPtr.Zero; }
                if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }

                BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
                bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bi.biWidth = w;
                bi.biHeight = -h;
                bi.biPlanes = 1;
                bi.biBitCount = 32;
                bi.biCompression = 0;

                IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                IntPtr bits = IntPtr.Zero;
                try { dib = Native.CreateDIBSection(screenDc, ref bi, 0, out bits, IntPtr.Zero, 0); }
                finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }

                if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    Config.Log("NowPlaying 窗口: CreateDIBSection 失败 " + Marshal.GetLastWin32Error());
                    return false;
                }
                memDc = Native.CreateCompatibleDC(IntPtr.Zero);
                if (memDc == IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; return false; }
                Native.SelectObject(memDc, dib);

                // 用 DIB 的内存直接构造 Bitmap：GDI+ 画进去就是那块显存，零拷贝
                surface = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                surfW = w; surfH = h;
                using (Graphics g = Graphics.FromImage(surface)) g.Clear(Color.Transparent);
                return true;
            }
        }

        private void Present()
        {
            if (memDc == IntPtr.Zero || surface == null || !IsHandleCreated) return;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                Native.POINT dst = new Native.POINT(Left, Top);
                Native.POINT src = new Native.POINT(0, 0);
                SIZE size = new SIZE(surfW, surfH);
                Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
                bf.BlendOp = 0;
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1;
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                                           memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        }

        // ================================================================
        //  绘制
        // ================================================================

        /// <summary>
        /// 控件中心是否被别的进程的窗口盖住。
        /// 盖住时跳过整帧渲染（不画也不上传），这是播放器控件最大的一笔 CPU 开销。
        /// </summary>
        private bool CoveredByOthers()
        {
            try
            {
                if (!Visible) return true;
                // 三点采样：全部被别的进程盖住才算遮挡（避免被压在上面小浮层骗到）
                uint me = (uint)Process.GetCurrentProcess().Id;
                int cy = ClientSize.Height / 2;
                int[] xs = new int[] { ClientSize.Width / 4, ClientSize.Width / 2, ClientSize.Width * 3 / 4 };
                for (int i = 0; i < xs.Length; i++)
                {
                    Point c = PointToScreen(new Point(xs[i], cy));
                    IntPtr h = Native.WindowFromPoint(new Native.POINT(c.X, c.Y));
                    if (h == IntPtr.Zero || h == Handle) return false;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if (pid == me) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private void Render()
        {
            if (!shown || mon == null || IsDisposed) return;
            // 被别的进程的窗口盖住就别画了：这个控件是普通浮层（不是 TOPMOST），
            // 最大化别的窗口会把它完全盖住，此时重绘纯属白烧 CPU（用户截图确认过）。
            if (CoveredByOthers()) return;
            if (!EnsureSurface()) return;
            long tRender0 = System.Diagnostics.Stopwatch.GetTimestamp();

            float vis = mon.Visibility;
            if (vis <= 0.01f) return;
            if (vis > 1f) vis = 1f;

            NowPlayingState st = mon.Latest != null ? mon.Latest : mon.State;
            EnsureFonts();
            int A = (int)(255 * vis);

            lock (Gdi.Lock)
            {
                using (Graphics g = Graphics.FromImage(surface))
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    // 频谱先画（在面板底下），再由面板盖上去 —— 和原来"频谱画在墙上、
                    // 播放器窗口浮在上面"的层次完全一致。
                    if (SpectrumPainter != null)
                    {
                        try { SpectrumPainter(g, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)); }
                        catch (Exception ex) { Config.Log("画频谱失败: " + ex.Message); }
                    }

                    TextRenderingHint oldHint = g.TextRenderingHint;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    // 面板默认全透明；想加底板就把 npPanelOpacity 调大
                    if (cfg.npPanelOpacity > 0.01)
                    {
                        Rectangle all = new Rectangle(panelOffX, panelOffY, panelW, panelH);
                        using (GraphicsPath p = Round(all, Math.Max(4f, 14 * scale)))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(
                            (int)(cfg.npPanelOpacity * 235 * vis), 16, 18, 24)))
                            g.FillPath(b, p);
                    }

                    // 分层窗口的鼠标命中是**按像素 alpha** 判定的：alpha = 0 的地方
                    // 消息会直接穿到下面的窗口。面板是全透明的，所以必须给可交互的
                    // 区域垫一层 alpha = 1 的黑 —— 1/255 肉眼看不见，但不再是 0。
                    using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                    {
                        int hx = (int)Math.Round(8 * scale), hy = (int)Math.Round(12 * scale);
                        Rectangle hp = rPrev; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                        hp = rPlay; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                        hp = rNext; hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                        hp = rBar;  hp.Inflate(hx, hy); g.FillRectangle(hit, hp);
                        g.FillRectangle(hit, rCover);
                    }

                    DrawCover(g, st, vis);

                    string title = st.Title;
                    using (StringFormat sf = LeftFormat())
                    {
                        if (title != null && title.Length > 0)
                        {
                            Fill(g, title, fontTitle, rTitle, sf, Color.FromArgb(A, 255, 255, 255));

                            // 歌手（灰）接在曲名后面
                            if (st.Artist != null && st.Artist.Length > 0)
                            {
                                SizeF tw = g.MeasureString(title, fontTitle);
                                float ax = rTitle.X + tw.Width + 14 * scale;
                                RectangleF ar = new RectangleF(ax, rTitle.Y + 1.5f * scale,
                                    Math.Max(0, rTitle.Right - ax), rTitle.Height);
                                if (ar.Width > 56 * scale)   // 剩的位置太窄就干脆不画，别挤出一个字母
                                {
                                    float need = g.MeasureString(st.Artist, fontTime).Width + 4 * scale;
                                    if (need < ar.Width) ar.Width = need;
                                    Fill(g, st.Artist, fontTime, ar, sf, Color.FromArgb(A, 178, 182, 190));
                                }
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
                        Fill(g, t1 + " / " + t2, fontTime, rTime, sf, Color.FromArgb(A, 150, 156, 168));

                    bool canPrev = st.HasSession && st.CanPrev;
                    bool canNext = st.HasSession && st.CanNext;
                    bool canPlay = st.HasSession && (st.Playing ? st.CanPause : st.CanPlay);
                    DrawIcon(g, rPrev, IconPrev, HoverPrev, canPrev, vis, 0.80f);
                    DrawIcon(g, rPlay, st.Playing ? IconPause : IconPlay, HoverPlay, canPlay, vis, 0.72f);
                    DrawIcon(g, rNext, IconNext, HoverNext, canNext, vis, 0.80f);

                    g.TextRenderingHint = oldHint;
                }
            }
            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            npRenders++; npTotalMs += (tEnd - tRender0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (npRenders >= 40)
            {
                // 只在明显偏慢时才记 —— 正常约 2.7 ms/帧，每 40 帧（≈0.6 秒）写一行的话，
                // 一天就是十几万行、日志文件被这类周期性诊断撑爆（用户实测反馈）。
                double avgMs = npTotalMs / npRenders;
                if (avgMs > 6.0)
                    Config.Log("控件渲染偏慢: " + npRenders + " 帧，平均 " + avgMs.ToString("0.0") +
                               " ms/帧，可视度=" + vis.ToString("0.00"));
                npRenders = 0; npTotalMs = 0;
            }
            Present();
        }

        private void DrawCover(Graphics g, NowPlayingState st, float vis)
        {
            if (st.Cover == null) return;   // 没封面就什么都不画
            float rad = Math.Max(3f, 10 * scale);

            // 封面预缩放缓存：双三次缩放每帧都做太贵，按源图 + 目标尺寸缓存一份。
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
            using (GraphicsPath cp = Round(rCover, rad))
            {
                Region saved = g.Clip;
                g.SetClip(cp, CombineMode.Intersect);
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = vis;
                    ia.SetColorMatrix(cm);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(st.Cover, rCover, 0, 0, st.Cover.Width, st.Cover.Height,
                                GraphicsUnit.Pixel, ia);
                }
                g.Clip = saved;
            }
            using (Pen pen = new Pen(Color.FromArgb((int)(70 * vis), 0, 0, 0), Math.Max(1f, 1.2f * scale)))
                g.DrawPath(pen, Round(rCover, rad));
        }

        /// <summary>
        /// 底部居中的音频电平条：把系统当前的实际音量峰值画成一条对称的条形显示。
        ///
        /// 电平来自 WASAPI 的 IAudioMeterInformation（默认播放设备混音后的峰值），
        /// 所以播放器放什么它就跟着动；起得快、落得慢，看着像电平表。
        /// 形状用固定的钟形包络放大，观感接近频谱——但它是**电平**显示，不是逐频段频谱
        /// （真频谱要把系统音频回环抓下来做 FFT，那是另一套）。
        /// </summary>
        private void DrawLevelMeter(Graphics g, float vis)
        {
            if (!cfg.showLevelMeter) return;
            int n = 26;
            float[] lv = AudioMeter.Levels(n);
            if (lv == null || lv.Length != n) return;

            int pad = (int)Math.Round(3 * scale);
            int bh = (int)Math.Round(13 * scale);          // 柱子最大高度
            int bw = Math.Max(2, (int)Math.Round(2.0 * scale));
            int gap = Math.Max(1, (int)Math.Round(1.6 * scale));
            int total = n * bw + (n - 1) * gap;

            // 居中在内容区（封面右边到按钮左边）
            int cx = (rCover.Right + rNext.Left) / 2;
            int x0 = cx - total / 2;
            int baseline = ClientSize.Height - pad;

            for (int i = 0; i < n; i++)
            {
                float h = lv[i] * bh;
                if (h < 1.5f) h = 1.5f;                     // 静止时留一条细线，看得出位置
                int x = x0 + i * (bw + gap);
                RectangleF r = new RectangleF(x, baseline - h, bw, h);
                int a = (int)(200 * vis);
                // 越靠中间越亮，两侧淡一点
                float t = (float)i / (n - 1);
                float edge = 0.55f + 0.45f * (float)Math.Sin(Math.PI * t);
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(a * edge), 235, 240, 250)))
                    g.FillRectangle(b, r);
            }
        }

        private void DrawBar(Graphics g, NowPlayingState st, float vis, int A)
        {
            double ratio = seeking && seekRatio >= 0 ? seekRatio : st.Progress;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            float th = rBar.Height;                              // 已听部分：满高
            float trackTh = Math.Max(2f, th * 0.42f);            // 未听部分：更细，和已听区分开

            Rectangle track = new Rectangle(rBar.X,
                (int)Math.Round(rBar.Y + (th - trackTh) / 2f),
                rBar.Width, (int)Math.Round(trackTh));
            using (GraphicsPath tp = Round(track, trackTh / 2f))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(70 * vis), 0, 0, 0)))
                    g.FillPath(b, tp);
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(95 * vis), 255, 255, 255)))
                    g.FillPath(b, tp);
            }
            int fillW = (int)Math.Round(rBar.Width * ratio);
            if (fillW > 0)
            {
                Rectangle fr = new Rectangle(rBar.X, rBar.Y, fillW, rBar.Height);
                using (GraphicsPath fp = Round(fr, th / 2f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(A, 255, 255, 255)))
                    g.FillPath(b, fp);
            }

            // 鼠标移到进度条上时圆点变大 + 一圈柔光。
            // （光标形状按你的要求不变，所以视觉反馈得给足。）
            // 只有鼠标真正落在小球上才放大发光 —— 移到进度条别处不反应。
            bool barHot = knobHot || seeking;
            float kr = (barHot ? 7.5f : 5f) * scale;
            float kx = rBar.X + rBar.Width * (float)ratio;
            float ky = rBar.Y + rBar.Height / 2f;
            if (barHot)
            {
                // 径向渐变做柔光：中心亮、边缘全透明
                float gr = kr * 2.3f;
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(kx - gr, ky - gr, gr * 2, gr * 2);
                    using (PathGradientBrush pg = new PathGradientBrush(gp))
                    {
                        pg.CenterColor = Color.FromArgb((int)(120 * vis), 255, 255, 255);
                        pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(pg, gp);
                    }
                }
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(70 * vis), 0, 0, 0)))
                g.FillEllipse(b, kx - kr, ky - kr + scale, kr * 2, kr * 2);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(A, 255, 255, 255)))
                g.FillEllipse(b, kx - kr, ky - kr, kr * 2, kr * 2);
        }

        private void DrawIcon(Graphics g, Rectangle box, string icon, int hotId,
                              bool enabled, float vis, float iconScale)
        {
            bool hot = hover == hotId && enabled;
            int a = enabled ? (int)(255 * vis * chromeFade * (hot ? 1f : 0.90f))
                             : (int)(255 * vis * chromeFade * 0.32f);
            if (a <= 0) return;

            if (hot)
            {
                RectangleF hb = new RectangleF(box.X + box.Width * 0.08f, box.Y + box.Height * 0.20f,
                                               box.Width * 0.84f, box.Height * 0.60f);
                using (GraphicsPath hp = Round(hb, Math.Max(4f, 9 * scale)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(40 * vis * chromeFade), 255, 255, 255)))
                    g.FillPath(b, hp);
            }

            using (GraphicsPath p = SvgPath.Icon(icon, box, iconScale * (hot ? 1.12f : 1f)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
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

        private static void Fill(Graphics g, string text, Font font, RectangleF box,
                                 StringFormat sf, Color color)
        {
            if (color.A <= 0 || text == null || text.Length == 0) return;
            using (SolidBrush b = new SolidBrush(color))
                g.DrawString(text, font, b, box, sf);
        }

        private static GraphicsPath Round(Rectangle r, float radius)
        {
            return Ui.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
        }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            return Ui.Round(r, radius);
        }

        private static string Fmt(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int total = (int)t.TotalSeconds;
            return (total / 60) + ":" + (total % 60).ToString("00");
        }

        // ================================================================
        //  鼠标 —— 自己的窗口，标准 Form 事件就够了
        // ================================================================

        private int HitControl(Point pt)
        {
            bool btnOn = chromeFade > 0.04f;   // 按钮淡没了就不该还能点中（防误触）
            if (btnOn) {
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
            if (h != hover)
            {
                hover = h;
                // 光标一律不换：进度条给拖动箭头、按钮给手型，都太"抢戏"。
                // 反馈全靠悬停时那层淡底，光标保持系统默认箭头。
                dirty = true;
            }
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
