using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 磁贴墙。
    ///
    /// v3 的结构性改动（相对 2.x）：
    ///   1. **帧时钟**取代 WM_TIMER。原来 tick.Interval = 8 实际只有 ~64Hz 且抖动
    ///      很大（WM_TIMER 的粒度就是系统 tick），悬停动画看着"一顿一顿"。
    ///      现在用高精度可等待计时器，60/120fps 精确落点，空闲时完全停摆。
    ///   2. **卡片不再因为滚动而重烤**。亚克力底按"内容坐标"取样，滚动时卡片整体
    ///      平移、底图不用重做 —— 原来滚动一停就重烤 26 张（每张 ~20ms），
    ///      那正是"松手后卡一下"的来源。
    ///   3. **脏区纪律**。烘焙好一张卡只标它自己那一块；只有整墙结构变化才整窗重绘。
    ///   4. **投影改成两层**（接触 + 环境），在半分辨率上模糊再放大，观感更接近
    ///      Win11 的卡片，计算量反而只有原来的 1/4。
    ///   5. **视觉全面重做**：渐变发丝描边、顶部高光、悬停强调色辉光、
    ///      圆形玻璃按钮、更克制的标签阴影。
    /// </summary>
    public partial class TileForm : Form
    {
        // ---- 视觉常量 ----
        private const float HoverGrow = 0.045f;      // 悬停放大比例
        private const float HoverLift = 2f;          // 悬停上浮（逻辑像素）
        private const float LabelLinesMax = 2;

        private Config cfg;
        private bool windowed;

        private List<TileItem> allItems = new List<TileItem>();
        private List<TileItem> view = new List<TileItem>();
        private readonly object dataLock = new object();

        // ---- 布局 ----
        private float scale = 1f;
        private int cardW, cardH, gapPx, padPx;
        private float radiusPx = 10f;
        private int cols = 1;
        private int gridLeft, gridTop, viewH, gridTopPad;
        private int shadowPadPx = 28;
        private int tileW, tileH;

        // ---- 滚动 / 交互 ----
        private float scroll, scrollTarget, maxScroll;
        private int hoverIndex = -1, selIndex = -1;

        // ---- 画布 ----
        private LayerSurface surface = new LayerSurface();
        private Bitmap wallpaperBlur;
        private int wpScreenW, wpScreenH;
        private volatile bool acrylicDirty = true;
        private Bitmap previewBackdrop;

        // ---- 素材 ----
        private ShadowBank shadows = new ShadowBank();
        private int cacheEpoch;

        private volatile bool dirty = true;
        private bool dirtyFull = true;
        private Rectangle dirtyRect = Rectangle.Empty;

        private bool stickBottom = true;
        private IntPtr desktopParent = IntPtr.Zero;
        private int pinRetries = 0;
        private int demoHover = -1;
        private IntPtr trayIconHandle = IntPtr.Zero;

        // ---- 右下角常驻按钮 ----
        private Rectangle ctrlRect = Rectangle.Empty;
        private Rectangle trashRect = Rectangle.Empty;
        private float ctrlHover, trashHover;     // 0..1 平滑
        private int hotButton;                   // 0 无 / 1 垃圾桶 / 2 齿轮

        // ---- 拖拽 ----
        private bool pressCandidate = false;
        private int pressStamp = 0;
        private Point pressAt = Point.Empty;
        private int pressIndex = -1;
        private bool dragging = false;
        private int dragIndex = -1;
        private int[] dragPreview;
        private Point dragPos = Point.Empty;
        private int dropIndex = -1;
        private int dropCol = -1, dropRow = -1;

        private int TapSlop { get { return (int)Math.Round(14 * scale); } }
        private int EdgeZone { get { return (int)Math.Round(6 * scale); } }
        private int SwipeDist { get { return (int)Math.Round(40 * scale); } }
        private bool edgeArmed = false;

        // ---- 空闲淡出 ----
        private float fadeLevel = 1f;
        private int fadeTarget = 1;
        private const float FadeSec = 0.40f;
        private const float FadeDead = 0.04f;
        private bool wallHidden;

        // ---- 帧循环 ----
        private FrameClock clock;
        private System.Windows.Forms.Timer logicTimer;
        private double lastFrameTime;
        private int animUntilMs;                 // 动画持续到这个时刻（之后降帧/停摆）
        private readonly Perf perf = new Perf();
        private int lastCoveredCheck;
        private bool covered;

        private Font fontCard;
        private Color accent = Color.FromArgb(76, 154, 255);
        private Color shadowCol = Color.Black;

        private NotifyIcon tray;
        private DeskInfoWindow deskInfo;
        private NowPlayingMonitor nowPlaying;
        private NowPlayingWindow npWindow;
        private SettingsForm settingsForm;

        // ---- 布局格子 ----
        private int[] cellCol = new int[0];
        private int[] cellRow = new int[0];
        private readonly Dictionary<int, int> cellOwner = new Dictionary<int, int>();
        private static int CellKey(int col, int row) { return col * 4096 + row; }

        // ---- 自动保存 / 监视 ----
        private int saveDueMs;
        private int watchDueMs;
        private int loadVersion;
        private volatile bool reloadPending;
        private volatile bool artPending;
        private int logicTick;

        // ---- 拖拽落点驻留 ----
        private const int DropDwellMs = 40;
        private int dropDwellStart;
        private int pendDropCol = -2, pendDropRow = -2, pendDropIndex = -2;
        private int dragFromCol = -1, dragFromRow = -1;
        private int freeSwapIndex = -1;
        private bool freeAnimBusy;
        private float[] animSlotFrom = new float[0];

        // ---- 消息 ----
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_THEMECHANGED = 0x031A;
        private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

        private bool trackingLeave;

        public TileForm(Config config, bool asWindow)
        {
            cfg = config;
            windowed = asWindow;
            accent = ColorUtils.FromHex(cfg.accentColor, Color.FromArgb(76, 154, 255));
            shadowCol = ColorUtils.FromHex(cfg.shadowColor, Color.Black);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = windowed;
            TopMost = windowed;
            Text = "TileDesk";
            StartPosition = FormStartPosition.Manual;
            KeyPreview = false;
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint, true);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (windowed)
            {
                int w = Math.Min(1500, wa.Width - 80);
                int h = Math.Min(940, wa.Height - 80);
                Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
            }
            else
            {
                Bounds = new Rectangle(wa.X, wa.Y, wa.Width, wa.Height);
            }

            string dh = Environment.GetEnvironmentVariable("TILEDESK_DEMO_HOVER");
            if (!string.IsNullOrEmpty(dh)) { int v; if (int.TryParse(dh, out v)) demoHover = v; }

            Config.Log("窗口创建: windowed=" + windowed + " wa=" + wa + " bounds=" + Bounds);

            BuildTray();
        }

        // ================================================================
        //  窗口样式
        // ================================================================

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED;
                if (!windowed)
                {
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                    cp.ExStyle |= Native.WS_EX_NOACTIVATE;
                    cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
                }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            clock = new FrameClock(Handle);
            Config.Log("帧时钟: 高精度=" + clock.HighResolution);

            logicTimer = new System.Windows.Forms.Timer();
            logicTimer.Interval = 250;
            logicTimer.Tick += delegate { OnLogicTick(); };
            logicTimer.Start();

            RecomputeMetrics();
            StartLoad();
            SetupWatchers();

            nowPlaying = new NowPlayingMonitor();
            nowPlaying.Start();

            try { npWindow = new NowPlayingWindow(nowPlaying, cfg, scale); }
            catch (Exception ex) { Config.Log("创建正在播放窗口失败: " + ex.Message); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Config.Log("窗口显示: bounds=" + Bounds + " client=" + ClientSize);
            if (windowed)
            {
                stickBottom = false;
                Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            else
            {
                BeginInvoke((MethodInvoker)delegate { ApplyWindowMode(cfg.windowMode); });
            }
            MarkDirtyAll();
            Wake(300);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecomputeMetrics();
            MarkDirtyAll();
            Wake(300);
        }

        // ================================================================
        //  尺寸 / DPI / 布局
        // ================================================================

        private void RecomputeMetrics()
        {
            int dpi = Native.GetWindowDpi(IsHandleCreated ? Handle : IntPtr.Zero);
            if (dpi <= 0) { try { dpi = DeviceDpi; } catch { } }
            if (dpi <= 0) dpi = 96;

            float newScale = dpi / 96f;
            if (cfg.uiScale > 0) newScale = (float)cfg.uiScale;
            if (newScale <= 0.1f) newScale = 1f;

            bool changed = Math.Abs(newScale - scale) > 0.001f;
            scale = newScale;

            cardW = Math.Max(48, (int)Math.Round(cfg.cardWidth * scale));
            cardH = Math.Max(48, (int)Math.Round(cfg.cardHeight * scale));
            gapPx = Math.Max(2, (int)Math.Round(cfg.gap * scale));
            padPx = Math.Max(4, (int)Math.Round(cfg.padding * scale));
            radiusPx = (float)Math.Max(0, cfg.cornerRadius * scale);
            shadowPadPx = Math.Max(12, (int)Math.Round(Math.Max(cfg.shadowSize, 18) * scale));
            tileW = cardW + shadowPadPx * 2;
            tileH = cardH + shadowPadPx * 2;

            if (changed || fontCard == null)
            {
                DisposeFonts();
                string fam = PickFontFamily(cfg.fontFamily);
                fontCard = new Font(fam, Math.Max(11f, cardW * 0.098f), FontStyle.Bold, GraphicsUnit.Pixel);
                shadows.Dispose();
            }

            Relayout();
            InvalidateAllCards();
            EnsureDeskInfo();
            Config.Log("UI: dpi=" + dpi + " scale=" + scale.ToString("0.##") +
                       " card=" + cardW + "x" + cardH + " tile=" + tileW + "x" + tileH +
                       " cols=" + cols + " client=" + ClientSize.Width + "x" + ClientSize.Height);
        }

        private void EnsureDeskInfo()
        {
            try
            {
                if (!cfg.showDeskInfo)
                {
                    if (deskInfo != null) { deskInfo.Dispose(); deskInfo = null; }
                    return;
                }
                int wantW = (int)Math.Round(340 * scale), wantH = (int)Math.Round(76 * scale);
                if (deskInfo != null && !deskInfo.IsDisposed &&
                    deskInfo.ClientSize.Width == wantW && deskInfo.ClientSize.Height == wantH) return;
                if (deskInfo != null) { deskInfo.Dispose(); deskInfo = null; }
                deskInfo = new DeskInfoWindow(cfg, scale);
                deskInfo.ClientSize = new Size(wantW, wantH);
            }
            catch (Exception ex)
            {
                Config.Log("创建信息条失败: " + ex.Message);
                deskInfo = null;
            }
        }

        private Rectangle DeskInfoRect()
        {
            if (deskInfo == null || deskInfo.IsDisposed) return Rectangle.Empty;
            int gap = (int)Math.Round(14 * scale);
            int w = deskInfo.ClientSize.Width, h = deskInfo.ClientSize.Height;
            int x = trashRect.Left - gap - w;
            int y = ctrlRect.Bottom - h;
            if (cfg.showNowPlaying && npWindow != null && !npWindow.IsDisposed &&
                npWindow.Visible && npWindow.BarCenterY > 0)
            {
                Point np = PointToClient(npWindow.PanelScreenLocation);
                y = np.Y + npWindow.BarCenterY - h / 2 + (int)Math.Round(22 * scale);
            }
            if (x < padPx) x = padPx;
            if (y < padPx) y = padPx;
            return new Rectangle(x, y, w, h);
        }

        private static string PickFontFamily(string wanted)
        {
            string[] cands = new string[] { wanted, "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimHei" };
            foreach (string c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try
                {
                    using (Font f = new Font(c, 12f))
                        if (string.Equals(f.Name, c, StringComparison.OrdinalIgnoreCase)) return c;
                }
                catch { }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        private void DisposeFonts()
        {
            if (fontCard != null) { fontCard.Dispose(); fontCard = null; }
        }

        private void Relayout()
        {
            int availW = ClientSize.Width - padPx * 2;
            cols = availW < cardW ? 1 : Math.Max(1, (availW + gapPx) / (cardW + gapPx));

            int gridW = cols * cardW + Math.Max(0, cols - 1) * gapPx;
            gridLeft = padPx + Math.Max(0, (availW - gridW) / 2);

            int cs = Math.Max(20, (int)Math.Round(38 * scale));
            int cm = Math.Max(6, (int)Math.Round(20 * scale));
            int bgap = Math.Max(4, (int)Math.Round(10 * scale));
            int lift = Math.Max(0, (int)Math.Round(cfg.bottomLift * scale));
            ctrlRect = new Rectangle(ClientSize.Width - cm - cs, ClientSize.Height - cm - cs - lift, cs, cs);
            trashRect = new Rectangle(ctrlRect.X - bgap - cs, ctrlRect.Y, cs, cs);

            gridTopPad = 0;
            AssignCells();

            gridTop = padPx;
            viewH = Math.Max(cardH, ClientSize.Height - gridTop - padPx);

            int baseScroll = Math.Max(0, ContentHeight() - viewH);
            maxScroll = baseScroll > 0 ? baseScroll + ObstacleBandPx() : 0;
            scrollTarget = ClampF(scrollTarget, 0, maxScroll);
            scroll = ClampF(scroll, 0, maxScroll);

            // ★ 只有"内容一屏放得下"时才把压在浮层上的卡片挪开。
            //   内容超过一屏时是**可滚动**的 —— 卡片滚过右下角那一小块浮层完全正常，
            //   这时候还去挪它，等于每次改窗口大小/缩放都把用户的布局重排一遍
            //   （原日志里那句"10 张卡片压在浮层上，已自动上移让开"就是这么来的）。
            if (baseScroll <= 0) NudgeCardsOutOfObstacles();
        }

        private void NudgeCardsOutOfObstacles()
        {
            if (allItems.Count == 0) return;
            Rectangle[] obs = ObstacleRects();
            if (obs.Length == 0) return;
            int n = Math.Min(allItems.Count, cellRow.Length);
            int moved = 0;
            for (int i = 0; i < n; i++)
            {
                TileItem it = allItems[i];
                if (it == null) continue;
                int row0 = cellRow[i];
                if (row0 <= 0) continue;
                int col = cellCol[i];
                int oldKey = CellKey(col, row0);
                int owner;
                if (cellOwner.TryGetValue(oldKey, out owner) && owner == i) cellOwner.Remove(oldKey);

                int row = row0;
                while (row > 0)
                {
                    int key = CellKey(col, row);
                    if (!cellOwner.ContainsKey(key))
                    {
                        cellRow[i] = row;
                        Rectangle r = CardRect(i);
                        bool hit = false;
                        for (int k = 0; k < obs.Length; k++) if (r.IntersectsWith(obs[k])) { hit = true; break; }
                        if (!hit) break;
                    }
                    row--;
                }
                cellRow[i] = row;
                cellOwner[CellKey(col, row)] = i;
                if (row != row0) { it.cellRow = row; moved++; }
            }
            if (moved > 0)
            {
                cellOwner.Clear();
                int useCols = Math.Max(1, cols);
                int scan = 0;
                for (int i = 0; i < n; i++)
                {
                    TileItem it = allItems[i];
                    if (it == null) continue;
                    int c = cellCol[i], nr = cellRow[i];
                    if (c < 0 || c >= useCols || nr < 0 || cellOwner.ContainsKey(CellKey(c, nr)))
                    {
                        int guard2 = 0;
                        while (guard2++ < 4096)
                        {
                            int cc = scan % useCols, rr = scan / useCols;
                            scan++;
                            if (!cellOwner.ContainsKey(CellKey(cc, rr))) { c = cc; nr = rr; break; }
                        }
                        cellCol[i] = c; cellRow[i] = nr;
                    }
                    cellOwner[CellKey(c, nr)] = i;
                    it.cellCol = c; it.cellRow = nr;
                }
                Config.Log("布局: " + moved + " 张卡片压在浮层上，已自动上移让开");
                ScheduleSave();
            }
        }

        private int ObstacleBandPx()
        {
            Rectangle[] obs = ObstacleRects();
            int band = 0;
            for (int i = 0; i < obs.Length; i++)
                band = Math.Max(band, ClientSize.Height - obs[i].Top);
            return band;
        }

        private int ContentHeight()
        {
            int maxRow = 0;
            for (int i = 0; i < cellRow.Length; i++) if (cellRow[i] > maxRow) maxRow = cellRow[i];
            int rows = cellRow.Length > 0 ? maxRow + 1 : 0;
            return rows > 0 ? rows * cardH + (rows - 1) * gapPx : 0;
        }

        private Rectangle[] ObstacleRects()
        {
            int tol = (int)Math.Round(10 * scale);
            List<Rectangle> list = new List<Rectangle>(3);
            if (cfg.showControlButton && !ctrlRect.IsEmpty)
            {
                Rectangle b = ctrlRect;
                if (!trashRect.IsEmpty) b = Rectangle.Union(b, trashRect);
                b.Inflate(tol, tol);
                list.Add(b);
            }
            if (cfg.showNowPlaying && npWindow != null)
            {
                if (npWindow.Width > 0 && npWindow.Height > 0)
                {
                    int m = cfg.npMargin;
                    int pw = (int)Math.Round(cfg.npWidth * scale);
                    int ph = (int)Math.Round(cfg.npHeight * scale);
                    Rectangle np = new Rectangle(m, ClientSize.Height - m - ph, pw, ph);
                    np.Inflate(tol, tol);
                    list.Add(np);
                }
            }
            Rectangle di = DeskInfoRect();
            if (!di.IsEmpty) { di.Inflate(tol, tol); list.Add(di); }
            return list.ToArray();
        }

        private bool CellBlockedAt(int col, int row, int scrollPx, Rectangle[] obs)
        {
            if (obs == null || obs.Length == 0) return false;
            int x = gridLeft + col * (cardW + gapPx);
            int y = gridTop + gridTopPad + row * (cardH + gapPx) - scrollPx;
            Rectangle r = new Rectangle(x, y, cardW, cardH);
            for (int i = 0; i < obs.Length; i++)
                if (r.IntersectsWith(obs[i])) return true;
            return false;
        }

        // ================================================================
        //  卡片位置：自动排列 / 自由排列
        // ================================================================

        private static string PosKey(TileItem it)
        {
            try { return Path.GetFileName(it.path); } catch { return it.name; }
        }

        private string[] SplitPos(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] parts = s.Split('|');
            return parts.Length == 3 ? parts : null;
        }

        private Dictionary<string, int[]> SavedPositions()
        {
            Dictionary<string, int[]> map = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in cfg.positions)
            {
                string[] p = SplitPos(s);
                if (p == null) continue;
                int c, r;
                if (int.TryParse(p[1], out c) && int.TryParse(p[2], out r)) map[p[0]] = new int[] { c, r };
            }
            return map;
        }

        private void AssignCells()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            int n = snapshot.Count;
            if (cellCol.Length != n) { cellCol = new int[n]; cellRow = new int[n]; }
            cellOwner.Clear();
            int useCols = Math.Max(1, cols);

            if (cfg.autoArrange)
            {
                for (int i = 0; i < n; i++)
                {
                    int c = i % useCols, r = i / useCols;
                    cellCol[i] = c; cellRow[i] = r;
                    cellOwner[CellKey(c, r)] = i;
                }
                return;
            }

            Dictionary<string, int[]> saved = SavedPositions();
            for (int i = 0; i < n; i++)
            {
                TileItem it = snapshot[i];
                int c = it.cellCol, r = it.cellRow;
                int[] p;
                if ((c < 0 || r < 0) && saved.TryGetValue(PosKey(it), out p)) { c = p[0]; r = p[1]; }
                if (c < 0 || c >= useCols || r < 0 || cellOwner.ContainsKey(CellKey(c, r))) { c = -1; r = -1; }
                it.cellCol = c; it.cellRow = r;
            }
            for (int i = 0; i < n; i++)
            {
                TileItem it = snapshot[i];
                if (it.cellCol < 0) continue;
                if (cellOwner.ContainsKey(CellKey(it.cellCol, it.cellRow)))
                {
                    it.cellCol = -1; it.cellRow = -1;
                    continue;
                }
                cellCol[i] = it.cellCol; cellRow[i] = it.cellRow;
                cellOwner[CellKey(cellCol[i], cellRow[i])] = i;
            }
            int scan = 0;
            for (int i = 0; i < n; i++)
            {
                TileItem it = snapshot[i];
                if (it.cellCol >= 0) continue;
                int guard = 0;
                while (guard++ < 65536)
                {
                    int c = scan % useCols, r = scan / useCols;
                    scan++;
                    if (cellOwner.ContainsKey(CellKey(c, r))) continue;
                    cellCol[i] = c; cellRow[i] = r;
                    it.cellCol = c; it.cellRow = r;
                    cellOwner[CellKey(c, r)] = i;
                    break;
                }
            }
        }

        private void SavePositions()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            List<string> list = new List<string>();
            for (int i = 0; i < snapshot.Count && i < cellCol.Length; i++)
                list.Add(PosKey(snapshot[i]) + "|" + cellCol[i] + "|" + cellRow[i]);
            cfg.positions = list;
        }

        private void CellAt(Point pt, out int col, out int row)
        {
            int relX = pt.X - gridLeft;
            int relY = pt.Y - gridTop - gridTopPad + (int)Math.Round(scroll);
            int cellX = cardW + gapPx, cellY = cardH + gapPx;
            col = relX / cellX;
            row = relY / cellY;
            if (col < 0) col = 0;
            if (col >= Math.Max(1, cols)) col = Math.Max(0, cols - 1);
            if (row < 0) row = 0;
            Rectangle[] obs = ObstacleRects();
            int guard = 0;
            while (CellBlockedAt(col, row, (int)Math.Round(scroll), obs) && row > 0 && guard++ < 128) row--;
        }

        private void DropCell(out int col, out int row)
        {
            col = dropCol; row = dropRow;
            if (col < 0 || row < 0)
            {
                if (dragIndex >= 0 && dragIndex < cellCol.Length) { col = cellCol[dragIndex]; row = cellRow[dragIndex]; }
                else { CellAt(dragPos, out col, out row); }
            }
        }

        private void ToggleAutoArrange()
        {
            cfg.autoArrange = !cfg.autoArrange;
            if (!cfg.autoArrange)
            {
                List<TileItem> snapshot;
                lock (dataLock) { snapshot = view; }
                for (int i = 0; i < snapshot.Count && i < cellCol.Length; i++)
                {
                    snapshot[i].cellCol = cellCol[i];
                    snapshot[i].cellRow = cellRow[i];
                }
                SavePositions();
            }
            Relayout();
            InvalidateAllCards();
            cfg.Save();
            MarkDirtyAll();
            Wake(300);
            Config.Log(cfg.autoArrange ? "自动排列卡片: 开" : "自动排列卡片: 关");
        }

        private void ResetCardPositions()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++) { snapshot[i].cellCol = -1; snapshot[i].cellRow = -1; }
            cfg.positions.Clear();
            Relayout();
            InvalidateAllCards();
            cfg.Save();
            MarkDirtyAll();
            Wake(300);
            Config.Log("卡片位置已重置");
        }

        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ================================================================
        //  素材：亚克力 / 噪声
        // ================================================================

        private void EnsureAcrylic(bool force)
        {
            Size screen = Screen.PrimaryScreen.Bounds.Size;
            if (!force && wallpaperBlur != null && !acrylicDirty &&
                wpScreenW == screen.Width && wpScreenH == screen.Height) return;
            if (acrylicBuilding) return;
            acrylicDirty = false;
            acrylicBuilding = true;

            // ★ 后台生成。原来是在 Render 里同步做的：读壁纸 + 缩到 1/4 + 三次盒式模糊，
            //   一张 4K 壁纸要 300~450ms，全部算在**首帧**上 —— 启动那一下白屏就是这么来的。
            //   现在先让墙用深色底画出来，模糊好了再换（亚克力卡片会自动重烤一次）。
            int w = screen.Width, h = screen.Height;
            double blur = cfg.acrylicBlur;
            float sc = scale;
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                Bitmap b = null;
                try { b = Acrylic.BuildWallpaperBlur(w, h, blur, sc); }
                catch (Exception ex) { Config.Log("生成亚克力壁纸失败: " + ex.Message); }
                pendingBlur = b;
                pendingBlurW = w;
                pendingBlurH = h;
                blurReady = true;
            });
        }

        private Bitmap pendingBlur;
        private int pendingBlurW, pendingBlurH;
        private volatile bool blurReady;
        private volatile bool acrylicBuilding;

        /// <summary>把后台做好的模糊壁纸换上去（只能在 UI 线程调）。</summary>
        private void AdoptPendingBlur()
        {
            if (!blurReady) return;
            blurReady = false;
            acrylicBuilding = false;
            Bitmap old = wallpaperBlur;
            wallpaperBlur = pendingBlur;
            pendingBlur = null;
            wpScreenW = pendingBlurW;
            wpScreenH = pendingBlurH;
            if (old != null) old.Dispose();
            InvalidateAcrylicCards();
            Wake(400);
            Config.Log("亚克力壁纸: " + (wallpaperBlur == null ? "不可用（退回深色底）"
                       : wallpaperBlur.Width + "x" + wallpaperBlur.Height));
            if (wallpaperBlur != null && Environment.GetEnvironmentVariable("TILEDESK_DUMP_BLUR") == "1")
            {
                try
                {
                    string p = Path.Combine(Config.DataDir, "wallpaper-blur.png");
                    lock (Gdi.Lock) { wallpaperBlur.Save(p, ImageFormat.Png); }
                    Config.Log("已导出壁纸模糊图: " + p);
                }
                catch (Exception ex) { Config.Log("导出壁纸模糊图失败: " + ex.Message); }
            }
        }

        // ================================================================
        //  脏区
        // ================================================================

        private void MarkDirtyAll()
        {
            dirty = true;
            dirtyFull = true;
            dirtyRect = Rectangle.Empty;
        }

        private void MarkDirty(Rectangle r)
        {
            dirty = true;
            if (dirtyFull) return;
            if (r.Width <= 0 || r.Height <= 0) return;
            r.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            dirtyRect = dirtyRect.IsEmpty ? r : Rectangle.Union(dirtyRect, r);
        }

        private Rectangle TileRectOf(int i)
        {
            Rectangle cr = CardRect(i);
            if (cr.IsEmpty) return Rectangle.Empty;
            cr.Inflate(shadowPadPx + 3, shadowPadPx + 3);
            return cr;
        }

        /// <summary>
        /// 悬停相关的脏区。
        ///
        /// 悬停位图不是"同一张卡"，它比常态卡大一圈：放大 4.5%（每边 +2%~3px）
        /// 再上浮 2 逻辑像素，位图里还带着放大后的那圈影子。如果脏区仍按常态卡算，
        /// 就会出现两个方向的问题：
        ///   · 开始悬停时：新的影子有一部分落在脏区外，画不进去；
        ///   · 移开鼠标时：那一圈影子没人擦，**残留在桌面上**（"阴影发生错误"）。
        /// 所以凡是 hover 状态发生变化，脏区一律用这个放大版。
        /// </summary>
        private Rectangle HoverRectOf(int i)
        {
            Rectangle cr = CardRect(i);
            if (cr.IsEmpty) return Rectangle.Empty;
            int extra = (int)Math.Round(Math.Max(cardW, cardH) * HoverGrow * 0.5f) +
                        (int)Math.Round(HoverLift * scale) + 4;
            int pad = shadowPadPx + Math.Max(3, extra);
            cr.Inflate(pad, pad);
            return cr;
        }

        /// <summary>
        /// 卡片网格所在的竖向条带（客户区坐标）。
        ///
        /// 滚动时整面墙都在动，直觉上要整窗重绘 —— 但 4K 下整窗是 3840x2064、
        /// 32MB 的位图，清一遍 + 传一遍就要 20ms，只能跑到 45fps。
        /// 实际上卡片只占中间一条（左右各有大片边距），而且滚动是**纯竖直**的：
        /// 条带之外一个像素都不会变。所以滚动只重绘这条带子，
        /// 4K 下上传量从 32MB 降到 ~11MB，帧时间直接砍掉一半以上。
        /// </summary>
        private Rectangle GridBand()
        {
            int pad = shadowPadPx + 6;
            int gridW = cols * cardW + Math.Max(0, cols - 1) * gapPx;
            int x = Math.Max(0, gridLeft - pad);
            int right = Math.Min(ClientSize.Width, gridLeft + gridW + pad);
            return new Rectangle(x, 0, Math.Max(1, right - x), ClientSize.Height);
        }

        /// <summary>标脏"整条网格"（滚动用）。</summary>
        private void MarkDirtyGrid()
        {
            dirty = true;
            if (dirtyFull) return;              // 已经有整窗重绘排队，它更大，不用再加
            Rectangle band = GridBand();
            dirtyRect = dirtyRect.IsEmpty ? band : Rectangle.Union(dirtyRect, band);
        }

        /// <summary>把整面墙"唤醒"一段时间：这段时间内跑满帧，之后按需降下来。</summary>
        private void Wake(int ms)
        {
            int until = Clock.NowMs + ms;
            if (unchecked(until - animUntilMs) > 0) animUntilMs = until;
            if (clock != null) clock.SetRate(60);
        }

        // ================================================================
        //  卡片缓存
        // ================================================================

        private void InvalidateAllCards()
        {
            cacheEpoch++;
            shadows.Dispose();
            cardRetry.Clear();
            MarkDirtyAll();
        }

        /// <summary>只让"没有封面"的卡片失效：它们的底是按位置从壁纸取样的。</summary>
        private void InvalidateAcrylicCards()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem it = snapshot[i];
                if (it != null && it.art == null) it.cacheEpoch = cacheEpoch - 1;
            }
            MarkDirtyAll();
        }

        private void DisposeAllCardCaches()
        {
            List<TileItem> list;
            lock (dataLock) { list = new List<TileItem>(allItems); }
            foreach (TileItem it in list) if (it != null) it.DropCache();
        }

        private readonly List<TileItem> cardRetry = new List<TileItem>();
        private readonly Dictionary<TileItem, int> cardRetryTries = new Dictionary<TileItem, int>();
        private readonly List<TileItem> disposeQueue = new List<TileItem>();

        /// <summary>把后台线程判定"该丢掉"的卡片缓存真正释放掉（只在 UI 线程调）。</summary>
        private void DrainDisposeQueue()
        {
            TileItem[] batch;
            lock (disposeQueue)
            {
                if (disposeQueue.Count == 0) return;
                batch = disposeQueue.ToArray();
                disposeQueue.Clear();
            }
            foreach (TileItem it in batch)
            {
                try { it.DropCache(); } catch { }
            }
        }

        private bool CacheStale(TileItem it)
        {
            return it.cacheNormal == null || it.cacheEpoch != cacheEpoch;
        }

        /// <summary>
        /// 增量烤卡片。
        ///
        /// 每帧最多花 <paramref name="budgetMs"/> 毫秒、最多烤一张 —— 一张卡约 4~7ms，
        /// 摊到几帧里完成，输入和动画完全不受影响。烤好一张只标它自己那块脏区，
        /// **不再整窗重绘**（原来每烤一张就 MarkDirtyAll 一次，26 张就是 26 次全屏合成，
        /// 那是启动和滚动结束时"卡一大下"的真正原因）。
        /// </summary>
        private void EnsureCaches(int budgetMs)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }

            int t0 = Clock.NowMs;
            int baked = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem it = snapshot[i];
                if (it == null || !CacheStale(it)) continue;
                try
                {
                    BuildCard(it, i);
                    baked++;
                    Rectangle r = TileRectOf(i);
                    if (!r.IsEmpty) MarkDirty(r);
                }
                catch (Exception ex)
                {
                    Config.Log("构建卡片失败 " + it.name + ": " + ex.Message);
                    if (!cardRetry.Contains(it)) cardRetry.Add(it);
                }
                if (unchecked(Clock.NowMs - t0) >= budgetMs) return;   // 到预算就收工，下一帧接着来
            }

            if (baked == 0 && cardRetry.Count > 0)
            {
                TileItem[] again = cardRetry.ToArray();
                cardRetry.Clear();
                foreach (TileItem it in again)
                {
                    int tries;
                    cardRetryTries.TryGetValue(it, out tries);
                    if (tries >= 6) { cardRetryTries.Remove(it); continue; }
                    try { BuildCard(it, IndexOfItem(it)); cardRetryTries.Remove(it); }
                    catch (Exception ex)
                    {
                        cardRetryTries[it] = tries + 1;
                        cardRetry.Add(it);
                        if (tries == 0) Config.Log("重建卡片失败 " + it.name + ": " + ex.Message);
                    }
                }
                MarkDirtyAll();
            }
        }

        private bool CachesIncomplete()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i] != null && CacheStale(snapshot[i])) return true;
            return cardRetry.Count > 0;
        }

        private int IndexOfItem(TileItem it)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++) if (object.ReferenceEquals(snapshot[i], it)) return i;
            return -1;
        }

        private Rectangle CardRect(int i)
        {
            if (i < 0 || i >= cellCol.Length) return Rectangle.Empty;
            return CellRect(cellCol[i], cellRow[i], (int)Math.Round(scroll));
        }

        private Rectangle CellRect(int col, int row, int scrollPx)
        {
            int x = gridLeft + col * (cardW + gapPx);
            int y = gridTop + gridTopPad + row * (cardH + gapPx) - scrollPx;
            return new Rectangle(x, y, cardW, cardH);
        }

        /// <summary>卡片在**内容坐标**里的位置（不含滚动）。亚克力底按它取样，滚动时不用重烤。</summary>
        private Rectangle CellRectContent(int col, int row)
        {
            int x = gridLeft + col * (cardW + gapPx);
            int y = gridTop + gridTopPad + row * (cardH + gapPx);
            return new Rectangle(x, y, cardW, cardH);
        }

        private Rectangle CardRectF(float slot)
        {
            if (slot < 0f) slot = 0f;
            int row = (int)(slot / cols);
            float col = slot - row * cols;
            float x = gridLeft + col * (cardW + gapPx);
            float y = gridTop + gridTopPad + row * (cardH + gapPx) - scroll;
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), cardW, cardH);
        }

        private Rectangle CellRectF(float col, float row)
        {
            float x = gridLeft + col * (cardW + gapPx);
            float y = gridTop + gridTopPad + row * (cardH + gapPx) - scroll;
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), cardW, cardH);
        }

        private void CellAtF(Point pt, out float col, out float row)
        {
            col = (pt.X - tileW / 2f - gridLeft) / (float)(cardW + gapPx);
            row = (pt.Y - tileH / 2f - gridTop - gridTopPad + scroll) / (float)(cardH + gapPx);
        }


        private void BuildCard(TileItem it, int index)
        {
            Rectangle content = (index >= 0 && index < cellCol.Length)
                ? CellRectContent(cellCol[index], cellRow[index])
                : Rectangle.Empty;

            if (Config.Verbose)
            {
                Color td = Material.TintFromBrightness(cfg.acrylicBrightness);
                Config.Log("烤卡 [" + it.name + "] " + (it.art != null ? "封面" : "亚克力") +
                           " 格=(" + (index >= 0 && index < cellCol.Length ? cellCol[index].ToString() : "?") +
                           "," + (index >= 0 && index < cellRow.Length ? cellRow[index].ToString() : "?") + ")" +
                           " tint=" + td.R + "," + td.G + "," + td.B +
                           " tintOp=" + cfg.acrylicTintOpacity.ToString("0.##") +
                           " sat=" + cfg.acrylicSaturation.ToString("0.##") +
                           " noise=" + cfg.acrylicNoise.ToString("0.#") +
                           "/" + cfg.acrylicNoiseScale.ToString("0.#") +
                           " cardOp=" + cfg.cardOpacity.ToString("0.##") +
                           " blur=" + cfg.acrylicBlur + " 底图=" + (wallpaperBlur != null));
            }
            if (Environment.GetEnvironmentVariable("TILEDESK_DEBUG_ART") == "1")
                Config.Log("烤卡素材 [" + it.name + "] art=" + (it.art != null ? "有" : "无") +
                           " path=" + (it.artPath ?? "null") +
                           " epoch=" + it.cacheEpoch + "/" + cacheEpoch +
                           " steam=" + (it.steamId ?? "null") +
                           " icon=" + (it.icon != null ? "有" : "无"));

            EnsureBitmap(ref it.cacheNormal, tileW, tileH);
            PaintCard(it.cacheNormal, it, false, content.X, content.Y);

            if (it.cacheHover != null) { it.cacheHover.Dispose(); it.cacheHover = null; }
            it.cacheEpoch = cacheEpoch;
            it.label = null;
        }

        private void BuildHoverCard(TileItem it, int index)
        {
            // ★ 悬停卡和常态卡必须基于**同一份素材状态**。
            //   封面是后台异步下载的：如果 cacheNormal 还是"无封面"时烤的，
            //   而 cacheHover 在封面下载完成之后才构建，就会出现
            //   "常态显示默认图标、鼠标移上去变成正常封面"（用户实测）。
            //   所以构建悬停卡之前，先把过期的常态卡重烤一遍。
            if (CacheStale(it))
            {
                try { BuildCard(it, index); } catch { }
            }

            Rectangle content = (index >= 0 && index < cellCol.Length)
                ? CellRectContent(cellCol[index], cellRow[index])
                : Rectangle.Empty;

            // ★ 悬停位图要比常态大一圈：卡片放大 4.5%，影子也按放大后的尺寸重做，
            //   如果位图还是 tileW×tileH，多出来的那圈影子会被位图边界裁掉 ——
            //   看起来就是"悬停后阴影没跟着放大"。
            int extraW = (int)Math.Round(cardW * HoverGrow);
            int extraH = (int)Math.Round(cardH * HoverGrow);
            EnsureBitmap(ref it.cacheHover, tileW + extraW, tileH + extraH);
            PaintCard(it.cacheHover, it, true, content.X, content.Y);
        }

        private static void EnsureBitmap(ref Bitmap bmp, int w, int h)
        {
            if (bmp != null && bmp.Width == w && bmp.Height == h) return;
            if (bmp != null) bmp.Dispose();
            bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }

        private static bool _surfaceDumped;

        // ---- 渲染面导出（排查用；正常运行时一个文件都不写）----
        //   TILEDESK_DUMP_SURFACE=1 : 稳态导出 surface-alpha.png
        //   TILEDESK_DUMP_FADE=1    : 另外在每次淡入/淡出**结束时**导出
        //                             surface-alpha-fade-<序号>-<0|1>.png
        private int fadeDumps;
        private static bool DumpSurfaceEnabled
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("TILEDESK_DUMP_SURFACE");
                return v == "1";
            }
        }
        private static bool DumpFadeEnabled
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("TILEDESK_DUMP_FADE");
                return v == "1";
            }
        }

        private void DumpSurface(string tag)
        {
            try
            {
                if (surface == null || surface.Bitmap == null) return;
                string name = "surface-alpha-" + tag + ".png";
                string path = Path.Combine(Config.DataDir, name);
                lock (Gdi.Lock)
                {
                    using (Bitmap copy = new Bitmap(surface.Bitmap))
                        copy.Save(path, ImageFormat.Png);
                }
                Config.Log("已导出渲染面(带alpha): " + path);
            }
            catch (Exception ex) { Config.Log("导出渲染面失败: " + ex.Message); }
        }

        /// <summary>淡入/淡出刚走完那一帧：把渲染面导出来（排查"恢复后阴影不对"专用）。</summary>
        private void DumpFadeSurface(float level)
        {
            if (!DumpFadeEnabled || fadeDumps >= 12) return;
            if (CachesIncomplete()) return;
            fadeDumps++;
            DumpSurface("fade-" + fadeDumps.ToString("00") + "-" + (level < 0.5f ? "out" : "in"));
        }

        // ================================================================
        //  单张卡片绘制
        // ================================================================

        /// <summary>
        /// 把一张卡片（含两层投影）烤进位图。
        ///
        /// 结构是分层的，和 Win11 的视觉树一个思路：
        ///   投影 → 材质（玻璃/封面）→ 内容（图标/封面）→ 描边 → 文字
        /// 每一层有**自己的透明度**：cardOpacity 只作用于材质，coverOpacity 只作用于封面。
        /// </summary>
        private void PaintCard(Bitmap target, TileItem it, bool hover, int sampleX, int sampleY)
        {
            using (Graphics g = Graphics.FromImage(target))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // ★ 必须用 SourceCopy 清空：EnsureBitmap 会复用上一次的位图，
                //   而 SourceOver 模式下的 Clear(Transparent) 等于"拿透明往上面盖一层"
                //   —— 什么都没清掉。于是每烤一次就往旧内容上叠一次，
                //   cardOpacity 一调低，累积的旧层就露馅（实测：玻璃怎么调都不变透明）。
                g.CompositingMode = CompositingMode.SourceCopy;
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;

                float grow = hover ? HoverGrow : 0f;
                float gw = cardW * grow, gh = cardH * grow;
                // ★ 卡片左上角保持在 shadowPadPx：悬停位图已经按 gw/gh 加大了
                //   （见 BuildHoverCard），这样四周的阴影内边距仍然对称，
                //   放大的影子能完整画进位图里，不会被边界裁掉。
                RectangleF cr = new RectangleF(
                    shadowPadPx, shadowPadPx, cardW + gw, cardH + gh);
                float rad = radiusPx + gw * 0.5f;

                // ---- 投影 ----
                // 投影浓度跟随卡片自身的透明度：玻璃调到 50% 时，影子若还是 100%，
                // 透出来的就是"被影子压暗的壁纸"，用户看起来就像"没变透明"。
                double cardOp = Math.Max(0.0, Math.Min(1.0,
                    it.art != null ? cfg.coverOpacity : cfg.cardOpacity));
                double sOp = cfg.shadowOpacity * cardOp;

                // ★ 悬停时卡片放大 4.5%，影子必须**按同样的比例**重取一张放大版，
                //   否则卡片边缘会伸出影子外面（用户实测"阴影脱离"）。
                //   贴图尺寸 = cardW+gw+pad*2 = 悬停位图尺寸，所以直接画在 (0,0)。
                int scw = (int)Math.Round(cardW + gw), sch = (int)Math.Round(cardH + gh);
                Bitmap sh = shadows.Get(scw, sch, shadowPadPx, (int)Math.Round(rad),
                                        sOp, Math.Max(6, cfg.shadowSize), shadowCol);
                if (sh != null && sOp > 0.01)
                    g.DrawImage(sh, 0, 0);

                using (GraphicsPath path = Shapes.Round(cr, rad))
                {
                    Region saved = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);

                    if (it.art != null) DrawCoverContent(g, cr, it, hover);
                    else DrawAcrylicContent(g, target, cr, rad, it,
                                            sampleX - (int)(gw / 2f), sampleY - (int)(gh / 2f), hover);

                    if (cfg.showLabels) DrawLabel(g, cr, it, false, 1f);

                    g.Clip = saved;
                }

                DrawBorder(g, cr, rad, hover);
            }
        }

        /// <summary>
        /// 封面：铺满整张卡。
        ///
        /// 底部那层遮罩**只在要显示名称时才画**（悬停、或开了常显）。
        /// 之前是无条件烤进去的，于是整面墙的封面下半截永远压着一层黑 ——
        /// 封面墙最不该有的就是这种"雾"。
        /// </summary>
        private void DrawCoverContent(Graphics g, RectangleF cr, TileItem it, bool hover)
        {
            // ★ 悬停/选中时封面**不再跟随 coverOpacity**：直接用 100% 不透明。
            //   常态墙是半透的（用户 coverOpacity=0.5），选中那张必须"实"起来，
            //   才有"这一张被拎出来了"的对比感；否则放大版也只是更淡的一层。
            float op = hover
                ? 1f
                : (float)Math.Max(0.0, Math.Min(1.0, cfg.coverOpacity));
            using (ImageAttributes ia = new ImageAttributes())
            {
                // 悬停时封面提亮 4%：Win11 的 hover 是"亮一点"，不是"发光"
                ColorMatrix m = BrightnessMatrix(hover ? 1.04f : 1f);
                m.Matrix33 = op;
                ia.SetColorMatrix(m);
                DrawCover(g, it.art, Rectangle.Round(cr), ia, 0.06f);
            }

            if (hover || cfg.showLabels) DrawLabelScrim(g, cr, it);
        }

        /// <summary>名称底下的渐变遮罩：让任何封面上都读得清字。</summary>
        private void DrawLabelScrim(Graphics g, RectangleF cr, TileItem it)
        {
            float lineH = fontCard.GetHeight(g);
            float nameH = lineH * LabelLinesMax + 2f;
            float scrimH = Math.Min(cr.Height * 0.62f, nameH + cr.Height * 0.22f);
            float top = cr.Bottom - scrimH;

            Color sc = ScrimTint(it.tint, 255, 0.24f);
            using (LinearGradientBrush lg = new LinearGradientBrush(
                new RectangleF(cr.X, top - 2f, cr.Width, scrimH + 4f),
                Color.FromArgb(0, sc.R, sc.G, sc.B), Color.FromArgb(226, sc.R, sc.G, sc.B),
                LinearGradientMode.Vertical))
            {
                ColorBlend cb = new ColorBlend(4);
                cb.Colors = new Color[]
                {
                    Color.FromArgb(0, sc.R, sc.G, sc.B),
                    Color.FromArgb(42, sc.R, sc.G, sc.B),
                    Color.FromArgb(140, sc.R, sc.G, sc.B),
                    Color.FromArgb(226, sc.R, sc.G, sc.B)
                };
                cb.Positions = new float[] { 0f, 0.32f, 0.66f, 1f };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, cr.X, top, cr.Width, scrimH);
            }
        }

        /// <summary>
        /// 无封面卡片：Win11 亚克力。
        ///
        /// 材质本身（模糊背景 + 着色 + 噪点）交给 <see cref="Material.PaintAcrylic"/>，
        /// 这里只负责"从哪儿取样"和"把图标放上去"。
        ///
        /// 取样用**内容坐标**（不含滚动），所以滚动时材质不用重烤。
        /// </summary>
        private void DrawAcrylicContent(Graphics g, Bitmap targetBmp, RectangleF cr, float rad, TileItem it,
                                        int sampleX, int sampleY, bool hover)
        {
            Bitmap source = null;
            RectangleF src = RectangleF.Empty;

            if (wallpaperBlur != null)
            {
                float sx = (Left + sampleX) / (float)Acrylic.Div;
                float sy = (Top + sampleY) / (float)Acrylic.Div;
                float sw = cr.Width / (float)Acrylic.Div;
                float sh = cr.Height / (float)Acrylic.Div;
                if (sx < 0) { sw += sx; sx = 0; }
                if (sy < 0) { sh += sy; sy = 0; }
                sw = Math.Min(sw, wallpaperBlur.Width - sx);
                sh = Math.Min(sh, wallpaperBlur.Height - sy);
                if (sw > 1f && sh > 1f)
                {
                    source = wallpaperBlur;
                    src = new RectangleF(sx, sy, sw, sh);
                }
            }
            else if (it.header != null)
            {
                source = it.header;
                src = new RectangleF(0, 0, it.header.Width, it.header.Height);
            }

            Color tint = Material.TintFromBrightness(cfg.acrylicBrightness);
            float matOp = (float)Math.Max(0.0, Math.Min(1.0, cfg.cardOpacity));

            // ★ 悬停时把材质做"实"一点。
            //   cardOpacity 很低（比如 0.33）时，壁纸的明暗带会大量透进卡片内部，
            //   被圆角裁剪切成一圈"内框"，看起来像"阴影和卡片分离"（用户实测）。
            //   悬停是"聚焦"状态，材质变实既符合直觉，也能压掉这层透出的杂色。
            if (hover) matOp = Math.Min(1f, matOp + (1f - matOp) * 0.55f);

            if (hover && Environment.GetEnvironmentVariable("TILEDESK_DEBUG_ACRYLIC") == "1")
                Config.Log("亚克力诊断: cr=" + cr.X.ToString("0.0") + "," + cr.Y.ToString("0.0") +
                           " " + cr.Width.ToString("0.0") + "x" + cr.Height.ToString("0.0") +
                           " rad=" + rad.ToString("0.0") + " matOp=" + matOp.ToString("0.###") +
                           " src=" + src.X.ToString("0.0") + "," + src.Y.ToString("0.0") +
                           " " + src.Width.ToString("0.0") + "x" + src.Height.ToString("0.0") +
                           " blur=" + (wallpaperBlur != null ? wallpaperBlur.Width + "x" + wallpaperBlur.Height : "null") +
                           " sample=" + sampleX + "," + sampleY + " hover=" + hover);

            // ★ 材质本体不含噪点：噪点单独叠加（见下），
            //   否则离屏合成的 AlphaMatrix(matOp) 会把噪点一起压到
            //   noise*matOp —— cardOpacity=0.2 时只剩 1.9%，肉眼完全看不出。
            if (matOp >= 0.995f)
            {
                Material.PaintAcrylic(g, cr, rad, source, src, tint,
                    cfg.acrylicTintOpacity, cfg.acrylicSaturation, 1.0, 0, 1);
            }
            else
            {
                // 逐层降 alpha 和整体降 alpha **不等价**（SourceOver 不是线性的），
                // 所以先按原样合成到一张离屏位图，再整体按 matOp 贴回来。
                int mw = Math.Max(1, (int)Math.Ceiling(cr.Width));
                int mh = Math.Max(1, (int)Math.Ceiling(cr.Height));
                using (Bitmap mat = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics mg = Graphics.FromImage(mat))
                    {
                        mg.SmoothingMode = SmoothingMode.AntiAlias;
                        mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        mg.CompositingMode = CompositingMode.SourceOver;
                        RectangleF local = new RectangleF(cr.X - cr.X, 0, cr.Width, cr.Height);
                        Material.PaintAcrylic(mg, local, rad, source, src, tint,
                            cfg.acrylicTintOpacity, cfg.acrylicSaturation, 1.0, 0, 1);
                    }
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(AlphaMatrix(matOp));
                        g.DrawImage(mat, new Rectangle((int)Math.Round(cr.X), (int)Math.Round(cr.Y), mw, mh),
                            0, 0, mw, mh, GraphicsUnit.Pixel, ia);
                    }
                }
            }

            // ★ 磨砂颗粒：独立叠加层。
            //   alpha 只受 matOp 的"部分"影响（保留 35% 底噪），
            //   这样卡片很透明时磨砂感依然看得见，不会跟着材质一起消失。
            int na = (int)Math.Round(Math.Max(0, cfg.acrylicNoise) * (0.35 + 0.65 * matOp));
            if (na > 0)
            {
                using (GraphicsPath np = Shapes.Round(cr, rad))
                {
                    Region saved = g.Clip;
                    g.SetClip(np, CombineMode.Intersect);
                    using (TextureBrush nb = new TextureBrush(Material.Noise(na)))
                    {
                        nb.WrapMode = WrapMode.Tile;
                        float ns = (float)cfg.acrylicNoiseScale;
                        if (ns > 1.01f) nb.ScaleTransform(ns, ns);
                        g.FillRectangle(nb, cr);
                    }
                    g.Clip = saved;
                }
            }

            if (hover) Material.PaintHoverWash(g, cr, rad, 10);

            if (it.icon != null)
            {
                float size = Math.Min(cr.Width, cr.Height) * 0.44f;
                float cx = cr.X + cr.Width / 2f;
                float cy = cr.Y + cr.Height / 2f;
                RectangleF ir = new RectangleF(cx - size / 2f, cy - size / 2f, size, size);
                g.DrawImage(it.icon, ir);
            }
        }

        /// <summary>
        /// 描边。Win11 的卡片是**一圈均匀的淡白 + 顶边略亮**，
        /// 不是整圈从亮到暗的渐变（那样看着像塑料）。
        /// </summary>
        private void DrawBorder(Graphics g, RectangleF cr, float rad, bool hover)
        {
            using (GraphicsPath bp = Shapes.Round(cr, rad))
            {
                if (hover)
                {
                    using (Pen pen = new Pen(Color.FromArgb(214, accent), Math.Max(1.4f, 1.5f * scale)))
                        g.DrawPath(pen, bp);
                    return;
                }

                using (Pen pen = new Pen(Color.FromArgb(30, 255, 255, 255), Math.Max(1f, scale)))
                    g.DrawPath(pen, bp);

                // 顶边一道更亮的高光（圆角之外的直线段）
                float inset = 0.5f * scale;
                float x0 = cr.X + rad * 0.85f;
                float x1 = cr.Right - rad * 0.85f;
                if (x1 > x0)
                    using (Pen top = new Pen(Color.FromArgb(48, 255, 255, 255), Math.Max(1f, scale)))
                        g.DrawLine(top, x0, cr.Y + inset, x1, cr.Y + inset);
            }
        }

        // ================================================================
        //  文字
        // ================================================================

        private static int ScaleA(int a, float k)
        {
            int v = (int)Math.Round(a * k);
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        private void DrawLabel(Graphics g, RectangleF cr, TileItem it, bool withScrim)
        {
            DrawLabel(g, cr, it, withScrim, 1f);
        }

        private void DrawLabel(Graphics g, RectangleF cr, TileItem it, bool withScrim, float alpha)
        {
            if (alpha <= 0.004f) return;
            float lineH = fontCard.GetHeight(g);
            float nameH = lineH * LabelLinesMax + 2f;
            float scrimH = nameH + cr.Height * 0.16f;

            if (withScrim)
            {
                float top = cr.Bottom - scrimH;
                Color sc = ScrimTint(it.tint, 255, 0.26f);
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(cr.X, top - 2f, cr.Width, scrimH + 4f),
                    Color.FromArgb(0, sc.R, sc.G, sc.B), Color.FromArgb(210, sc.R, sc.G, sc.B),
                    LinearGradientMode.Vertical))
                {
                    ColorBlend cb = new ColorBlend(4);
                    cb.Colors = new Color[]
                    {
                        Color.FromArgb(ScaleA(0, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(46, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(132, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(232, alpha), sc.R, sc.G, sc.B)
                    };
                    cb.Positions = new float[] { 0f, 0.34f, 0.66f, 1f };
                    lg.InterpolationColors = cb;
                    g.FillRectangle(lg, cr.X, top, cr.Width, scrimH);
                }
            }

            RectangleF nameRect = new RectangleF(
                cr.X + cr.Width * 0.07f, cr.Bottom - nameH - cr.Height * 0.05f,
                cr.Width * 0.86f, nameH + 4f);

            string label = FitLabel(it, g, nameRect.Width, (int)LabelLinesMax);
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.None;
                sf.FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap;

                // 两层层叠阴影就够了：三层叠出来发糊，还多一倍绘制
                RectangleF s1 = nameRect; s1.Offset(0, Math.Max(0.8f, 0.8f * scale));
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(ScaleA(84, alpha), 0, 0, 0)))
                    g.DrawString(label, fontCard, sb, s1, sf);
                RectangleF s2 = nameRect; s2.Offset(0, Math.Max(1.6f, 1.6f * scale));
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(ScaleA(46, alpha), 0, 0, 0)))
                    g.DrawString(label, fontCard, sb, s2, sf);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(ScaleA(253, alpha), 255, 255, 255)))
                    g.DrawString(label, fontCard, sb, nameRect, sf);
            }
        }

        private string FitLabel(TileItem it, Graphics g, float maxWidth, int maxLines)
        {
            if (maxWidth < 8f) maxWidth = 8f;
            if (it.label != null && Math.Abs(it.labelMaxW - maxWidth) < 0.5f &&
                it.labelLines == maxLines && Math.Abs(it.labelFontSize - fontCard.Size) < 0.01f)
                return it.label;

            string text = it.name == null ? "" : it.name;
            float maxH = fontCard.GetHeight(g) * maxLines + 1f;
            string result;

            using (StringFormat sf = MeasureFormat())
            {
                string wrapped = SmartWrap(g, text, maxWidth, maxLines, sf);
                SizeF full = g.MeasureString(wrapped, fontCard, new SizeF(maxWidth, float.MaxValue), sf);
                if (full.Height <= maxH)
                {
                    result = wrapped;
                }
                else
                {
                    int lo = 1, hi = text.Length, best = 0;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        string cand = text.Substring(0, mid).TrimEnd() + "…";
                        SizeF s = g.MeasureString(cand, fontCard, new SizeF(maxWidth, float.MaxValue), sf);
                        if (s.Height <= maxH) { best = mid; lo = mid + 1; }
                        else hi = mid - 1;
                    }
                    result = best <= 0 ? "…" : text.Substring(0, best).TrimEnd() + "…";
                }
            }

            it.label = result;
            it.labelMaxW = (int)Math.Round(maxWidth);
            it.labelLines = maxLines;
            it.labelFontSize = fontCard.Size;
            return result;
        }

        /// <summary>中文友好折行：优先在空格/标点后断，其次按字拆，带基本禁则处理。</summary>
        private string SmartWrap(Graphics g, string text, float maxWidth, int maxLines, StringFormat sf)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (maxLines < 1) maxLines = 1;
            maxWidth -= 2f;
            if (maxWidth < 8f) maxWidth = 8f;

            const string breakAfter = " ：:！!？?，,、；;。.·-—~～/|";
            const string noLineStart = "。，、！？：；）】》」』…·:;,.!?)]>%";

            List<string> units = new List<string>();
            StringBuilder acc = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                acc.Append(c);
                if (breakAfter.IndexOf(c) >= 0) { units.Add(acc.ToString()); acc.Length = 0; }
            }
            if (acc.Length > 0) units.Add(acc.ToString());

            List<string> lines = new List<string>();
            StringBuilder line = new StringBuilder();
            Action<string> pushLine = delegate(string s) { lines.Add(s); line.Length = 0; };

            for (int ui = 0; ui < units.Count; ui++)
            {
                string u = units[ui];
                string trial = line.Length == 0 ? u : line.ToString() + u;
                if (MeasureLineWidth(g, trial, sf) <= maxWidth)
                {
                    line.Length = 0; line.Append(trial);
                    continue;
                }
                if (line.Length > 0) pushLine(line.ToString());

                string rest = u;
                while (rest.Length > 0)
                {
                    int take = rest.Length;
                    while (take > 1 && MeasureLineWidth(g, rest.Substring(0, take), sf) > maxWidth)
                        take--;
                    if (take < rest.Length && noLineStart.IndexOf(rest[take]) >= 0 && take > 1) take--;
                    pushLine(rest.Substring(0, take));
                    rest = rest.Substring(take);
                }
            }
            if (line.Length > 0) lines.Add(line.ToString());

            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                while (lines.Count > 1 && lines[lines.Count - 1].Trim().TrimEnd('…').Length <= 1)
                    lines.RemoveAt(lines.Count - 1);
                lines[lines.Count - 1] = lines[lines.Count - 1].TrimEnd() + "…";
            }
            return string.Join("\n", lines.ToArray());
        }

        private float MeasureLineWidth(Graphics g, string s, StringFormat sf)
        {
            return g.MeasureString(s, fontCard, new SizeF(100000f, 100000f), sf).Width;
        }

        private static Color ScrimTint(Color tint, int alpha, float k)
        {
            int r = (int)Math.Round(tint.R * k);
            int g = (int)Math.Round(tint.G * k);
            int b = (int)Math.Round(tint.B * k);
            if (r < 10) r = 10;
            if (g < 10) g = 10;
            if (b < 12) b = 12;
            return Color.FromArgb(alpha, r, g, b);
        }

        private static StringFormat MeasureFormat()
        {
            StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.None;
            sf.FormatFlags = 0;
            return sf;
        }

        // ================================================================
        //  颜色矩阵
        // ================================================================

        private static ColorMatrix BrightnessMatrix(float b)
        {
            return new ColorMatrix(new float[][]
            {
                new float[] { b, 0, 0, 0, 0 },
                new float[] { 0, b, 0, 0, 0 },
                new float[] { 0, 0, b, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 }
            });
        }

        private static ColorMatrix AlphaMatrix(float a)
        {
            return new ColorMatrix(new float[][]
            {
                new float[] { 1, 0, 0, 0, 0 },
                new float[] { 0, 1, 0, 0, 0 },
                new float[] { 0, 0, 1, 0, 0 },
                new float[] { 0, 0, 0, a, 0 },
                new float[] { 0, 0, 0, 0, 1 }
            });
        }

        private static void DrawCover(Graphics g, Bitmap src, Rectangle dest, ImageAttributes ia, float topBias)
        {
            double sr = (double)src.Width / src.Height;
            double dr = (double)dest.Width / dest.Height;
            double sw, sh;
            if (sr > dr) { sh = src.Height; sw = sh * dr; }
            else { sw = src.Width; sh = sw / dr; }
            float sx = (float)((src.Width - sw) / 2.0);
            float sy = (float)((src.Height - sh) * topBias);
            g.DrawImage(src, dest, sx, sy, (float)sw, (float)sh, GraphicsUnit.Pixel, ia);
        }

        // ================================================================
        //  渲染
        // ================================================================

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        private void Render(Rectangle region)
        {
            if (!IsHandleCreated || !Visible) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (!surface.Ensure(w, h)) return;
            EnsureAcrylic(false);          // 后台生成，第一次调用只是排个任务，不阻塞这一帧

            long t0 = Stopwatch.GetTimestamp();
            bool full = region.IsEmpty;

            try
            {
                List<TileItem> snapshot;
                lock (dataLock) { snapshot = view; }

                Graphics g = surface.G;
                if (full) surface.ClearAll();
                else surface.ClearRect(region);

                // ★★ 局部重绘必须把画笔**裁到刚擦干净的那块区域**里。
                //
                //   DrawTile 画的是整张 tile 位图（卡片 + 一圈半透明阴影），
                //   而局部重绘只把 region 擦成透明。落在 region 之外的那部分
                //   每一帧都是"在没擦过的旧像素上再叠一层"：
                //     · 悬停动画每帧都标脏 → 相邻卡片（tile 与 region 相交）被反复重画；
                //     · 半透明阴影 alpha 每次 +~0.1，几十帧后叠到 0.7 以上；
                //     · 叠加停止的地方正好是脏区边界，于是画面里出现
                //       **边界是硬直线的黑色方框**，看着像"阴影画错了"。
                //   裁到 region 之后：region 内擦干净重画，region 外一个像素都不碰
                //   （那部分本来就不上传，DIB 里留着上一帧的正确内容）。
                if (!full) g.SetClip(region, CombineMode.Intersect);

                // 屏幕右边缘垫一层 alpha=1 的隐形命中区：分层窗口按像素 alpha 命中，
                // 全透明处鼠标会直接穿到桌面，边缘手势的"起手"就收不到按下事件。
                if (full || region.Contains(new Rectangle(w - EdgeZone, 0, EdgeZone, h)))
                {
                    using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                        g.FillRectangle(hit, new Rectangle(w - EdgeZone, 0, EdgeZone, h));
                }

                if (!wallHidden)
                {
                    DrawPreviewBackdrop(g);

                    if (dragging && !cfg.autoArrange)
                    {
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            if (i == dragIndex) continue;
                            TileItem fi = snapshot[i];
                            Rectangle cr = fi.animCol >= 0f ? CellRectF(fi.animCol, fi.animRow) : CardRect(i);
                            if (!VisibleRect(cr)) continue;
                            if (!Touches(region, TileRectOf(i))) continue;
                            if (fi.cacheNormal == null) continue;
                            float ha = HoverBaseAlpha(i, snapshot);
                            if (ha <= 0.004f) continue;
                            DrawTile(g, fi.cacheNormal, cr, fi.launchAnim, ha);
                        }

                        int tc, tr;
                        DropCell(out tc, out tr);
                        if (!DragOverTrash()) DrawDropOutline(g, CellRect(tc, tr, (int)Math.Round(scroll)));
                        DrawDraggedCard(g, snapshot);
                    }
                    else if (dragging && dragPreview != null)
                    {
                        for (int slot = 0; slot < dragPreview.Length; slot++)
                        {
                            int itemIdx = dragPreview[slot];
                            if (itemIdx == dragIndex) continue;
                            if (itemIdx < 0 || itemIdx >= snapshot.Count) continue;
                            float aslot = snapshot[itemIdx].animSlot;
                            Rectangle cr = aslot >= 0f ? CardRectF(aslot) : CardRect(slot);
                            if (!VisibleRect(cr)) continue;
                            if (!Touches(region, TileRectOf(slot))) continue;
                            Bitmap bmp = snapshot[itemIdx].cacheNormal;
                            if (bmp == null) continue;
                            float ha = HoverBaseAlpha(itemIdx, snapshot);
                            if (ha <= 0.004f) continue;
                            DrawTile(g, bmp, cr, snapshot[itemIdx].launchAnim, ha);
                        }

                        int emptySlot = Array.IndexOf(dragPreview, dragIndex);
                        if (emptySlot >= 0 && !DragOverTrash())
                            DrawDropOutline(g, CardRect(emptySlot));
                        DrawDraggedCard(g, snapshot);
                    }
                    else
                    {
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            TileItem fi = snapshot[i];
                            Rectangle cr = fi.animCol >= 0f ? CellRectF(fi.animCol, fi.animRow) : CardRect(i);
                            if (!VisibleRect(cr)) continue;
                            if (!Touches(region, TileRectOf(i))) continue;
                            if (fi.cacheNormal == null) continue;
                            float ha = HoverBaseAlpha(i, snapshot);
                            if (ha <= 0.004f) continue;
                            DrawTile(g, fi.cacheNormal, cr, fi.launchAnim, ha);
                        }
                    }

                    // 选中框（只在右键菜单打开时出现）
                    if (selIndex >= 0 && selIndex != hoverIndex)
                    {
                        Rectangle sr = CardRect(selIndex);
                        if (!sr.IsEmpty && sr.Bottom >= -cardH && sr.Top <= h + cardH &&
                            Touches(region, TileRectOf(selIndex)))
                        {
                            using (GraphicsPath sp = Shapes.Round(new RectangleF(sr.X, sr.Y, sr.Width, sr.Height), radiusPx))
                            using (Pen pen = new Pen(Color.FromArgb(235, accent), Math.Max(1.6f, 1.9f * scale)))
                                g.DrawPath(pen, sp);
                        }
                    }

                    DrawHoverOverlay(g, snapshot, region);
                    DrawRipples(g, snapshot, region);
                    DrawControlButton(g, region);
                }

                if (!full) g.ResetClip();
            }
            catch (Exception ex) { Config.Log("渲染异常: " + ex.Message); }

            double drawMs = Elapsed(t0);
            long p0 = Stopwatch.GetTimestamp();
            surface.Present(Handle, Left, Top, region, fadeLevel);
            double presentMs = Elapsed(p0);

            perf.Frame(full, drawMs, presentMs);
            perf.Tick();

            if (!loggedFirstFrame)
            {
                loggedFirstFrame = true;
                Config.Log("首帧: " + (full ? "整窗" : "局部") + " 绘制 " + drawMs.ToString("0.0") +
                           " ms + 上传 " + presentMs.ToString("0.0") + " ms，画布 " +
                           surface.Width + "x" + surface.Height +
                           " 窗口=" + Bounds.X + "," + Bounds.Y + "," + Bounds.Width + "x" + Bounds.Height +
                           " grid=" + gridLeft + "," + gridTop + " scroll=" + scroll.ToString("0.0") +
                           " 卡片=" + ViewCount());
            }
        }

        private int ViewCount()
        {
            lock (dataLock) { return view.Count; }
        }

        private bool loggedFirstFrame;

        private static double Elapsed(long t0)
        {
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        private bool VisibleRect(Rectangle cr)
        {
            return cr.Bottom >= -tileH && cr.Top <= ClientSize.Height + tileH;
        }

        private bool Touches(Rectangle region, Rectangle r)
        {
            if (region.IsEmpty) return true;
            if (r.IsEmpty) return false;
            return r.IntersectsWith(region);
        }

        private void DrawTile(Graphics g, Bitmap bmp, Rectangle cr, float anim)
        {
            if (anim <= 0.01f)
            {
                if (bmp.Width != tileW || bmp.Height != tileH)
                    g.DrawImage(bmp, new Rectangle(cr.X - shadowPadPx, cr.Y - shadowPadPx, tileW, tileH));
                else
                    g.DrawImage(bmp, cr.X - shadowPadPx, cr.Y - shadowPadPx);
                return;
            }

            // 启动反馈：轻微放大 + 一圈强调色扩散
            float k = 1f + 0.05f * anim;
            float cx = cr.X + cr.Width / 2f;
            float cy = cr.Y + cr.Height / 2f;
            float w = tileW * k, h = tileH * k;

            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, new RectangleF(cx - w / 2f, cy - h / 2f, w, h));
            g.InterpolationMode = old;

            float grow = 10f * scale * (1f - anim);
            RectangleF ring = new RectangleF(cr.X - grow, cr.Y - grow,
                                             cr.Width + grow * 2, cr.Height + grow * 2);
            using (GraphicsPath rp = Shapes.Round(ring, radiusPx + grow))
            using (Pen pen = new Pen(Color.FromArgb((int)(225 * anim), accent), Math.Max(1.5f, 3f * scale)))
                g.DrawPath(pen, rp);
            using (GraphicsPath fp = Shapes.Round(new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), radiusPx))
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(52 * anim), accent)))
                g.FillPath(b, fp);
        }
        /// <summary>
        /// 带整体透明度的贴图。
        ///
        /// 悬停时底层卡片必须**按 1-e 淡出**，否则悬停层是"叠"在常态卡上的：
        /// 封面本身有 coverOpacity（默认 0.5），两张半透明封面叠在一起就是
        /// "放大版盖住原版、两层同时可见"。
        /// </summary>
        private void DrawTile(Graphics g, Bitmap bmp, Rectangle cr, float anim, float alpha)
        {
            if (bmp == null) return;
            if (alpha >= 0.996f) { DrawTile(g, bmp, cr, anim); return; }
            if (alpha <= 0.004f) return;

            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(AlphaMatrix(alpha));
                InterpolationMode old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, new Rectangle(cr.X - shadowPadPx, cr.Y - shadowPadPx, tileW, tileH),
                    0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                g.InterpolationMode = old;
            }
        }

        private void DrawDropOutline(Graphics g, Rectangle sr)
        {
            if (sr.IsEmpty) return;
            using (GraphicsPath sp = Shapes.Round(new RectangleF(sr.X, sr.Y, sr.Width, sr.Height), radiusPx))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(26, accent)))
                    g.FillPath(b, sp);
                using (Pen pen = new Pen(Color.FromArgb(150, accent), Math.Max(1.4f, 1.8f * scale)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, sp);
                }
            }
        }

        private Bitmap dragLiveCard;

        private void DrawDraggedCard(Graphics g, List<TileItem> snapshot)
        {
            if (dragIndex < 0 || dragIndex >= snapshot.Count) return;
            TileItem it = snapshot[dragIndex];
            if (it.cacheNormal == null) return;

            EnsureBitmap(ref dragLiveCard, tileW, tileH);
            int drawX = (int)Math.Round(dragPos.X - tileW / 2f);
            int drawY = (int)Math.Round(dragPos.Y - tileH / 2f);

            // 拖动中的卡片每帧重烤一张：它的底要跟着光标取壁纸色，
            // 而且只有一张，成本 1~2ms，可以接受。
            Rectangle content = new Rectangle(drawX + shadowPadPx, drawY + shadowPadPx, cardW, cardH);
            PaintCard(dragLiveCard, it, true, content.X, content.Y);

            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(AlphaMatrix(DragOverTrash() ? 0.40f : 0.88f));
                g.DrawImage(dragLiveCard, new Rectangle(drawX, drawY, tileW, tileH),
                    0, 0, tileW, tileH, GraphicsUnit.Pixel, ia);
            }
        }

        /// <summary>
        /// 悬停期间底层卡片该用的不透明度。
        ///
        /// 悬停**不是**在常态卡上再盖一层，而是同一张卡的位图替换：
        /// 底层用 1-e 淡出、悬停层用 e 淡入，两边权重相加恒为 1。
        /// 任一帧都不存在"两张封面同时全强度显示"。
        /// </summary>
        private float HoverBaseAlpha(int i, List<TileItem> snapshot)
        {
            if (i != hoverIndex || i < 0 || i >= snapshot.Count) return 1f;
            TileItem hi = snapshot[i];
            if (hi.hover <= 0.001f) return 1f;
            if (hi.cacheHover == null) return 1f;   // 悬停位图还没烤好：先按常态画，下一帧再交叉
            return 1f - Ease.OutCubic(hi.hover);
        }

        /// <summary>悬停层：预烤的 hover 位图从 1/(1+grow) 放大到 1.0，与底层互补交叉淡入。</summary>
        private void DrawHoverOverlay(Graphics g, List<TileItem> snapshot, Rectangle region)
        {
            if (hoverIndex < 0 || hoverIndex >= snapshot.Count) return;
            TileItem hit = snapshot[hoverIndex];
            float hov = hit.hover;
            if (hov <= 0.01f) return;
            if (!Touches(region, HoverRectOf(hoverIndex))) return;

            if (hit.cacheHover == null)
            {
                try { BuildHoverCard(hit, hoverIndex); }
                catch (Exception ex) { Config.Log("构建悬停卡片失败: " + ex.Message); }
            }
            if (hit.cacheHover == null) return;

            Rectangle cr = hit.animCol >= 0f ? CellRectF(hit.animCol, hit.animRow) : CardRect(hoverIndex);
            if (cr.IsEmpty) return;

            float e = Ease.OutCubic(hov);
            float s = 1f / (1f + HoverGrow) + (1f - 1f / (1f + HoverGrow)) * e;
            float ccx = cr.X + cr.Width / 2f;
            float ccy = cr.Y + cr.Height / 2f;
            float lift = HoverLift * scale * e;

            // ★ 悬停位图是 tileW+extra × tileH+extra（含放大后的影子），
            //   缩放基准也要用同一尺寸，否则卡片会被整体缩小一圈。
            int extraW = (int)Math.Round(cardW * HoverGrow);
            int extraH = (int)Math.Round(cardH * HoverGrow);
            int hoverW = tileW + extraW, hoverH = tileH + extraH;
            float dw = hoverW * s, dh = hoverH * s;

            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(AlphaMatrix(e));
                InterpolationMode oldIp = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(hit.cacheHover,
                    Rectangle.Round(new RectangleF(ccx - dw / 2f, ccy - dh / 2f - lift, dw, dh)),
                    0, 0, hoverW, hoverH, GraphicsUnit.Pixel, ia);
                g.InterpolationMode = oldIp;
            }

            if (!cfg.showLabels && cfg.labelsOnHover)
            {
                float la = (e - 0.30f) / 0.70f;
                if (la < 0f) la = 0f; else if (la > 1f) la = 1f;
                la = Ease.OutCubic(la);
                if (la > 0.004f)
                {
                    float cwF = cr.Width * (1f + HoverGrow);
                    float chF = cr.Height * (1f + HoverGrow);
                    RectangleF crFinal = new RectangleF(ccx - cwF / 2f, ccy - chF / 2f - lift, cwF, chF);
                    Region savedClip = g.Clip;
                    using (GraphicsPath lp = Shapes.Round(crFinal, radiusPx * (1f + HoverGrow)))
                    {
                        g.SetClip(lp, CombineMode.Intersect);
                        DrawLabel(g, crFinal, hit, false, la);
                        g.Clip = savedClip;
                    }
                }
            }
        }

        private void DrawRipples(Graphics g, List<TileItem> snapshot, Rectangle region)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem it = snapshot[i];
                if (it.ripple < 0f || it.ripple >= 1f) continue;
                Rectangle cr = CardRect(i);
                if (!VisibleRect(cr)) continue;
                if (!Touches(region, TileRectOf(i))) continue;

                float t = it.ripple;
                float maxR = (float)Math.Sqrt((double)cr.Width * cr.Width + (double)cr.Height * cr.Height) * 0.62f;
                float r = maxR * Ease.OutCubic(t);
                int a = (int)(120 * (1f - t) * (1f - t));
                if (a <= 0 || r <= 1f) continue;

                using (GraphicsPath clip = Shapes.Round(new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), radiusPx))
                {
                    Region saved = g.Clip;
                    g.SetClip(clip, CombineMode.Intersect);
                    float kx = cr.X + it.rippleX, ky = cr.Y + it.rippleY;
                    using (GraphicsPath gp = new GraphicsPath())
                    {
                        gp.AddEllipse(kx - r, ky - r, r * 2, r * 2);
                        using (PathGradientBrush pg = new PathGradientBrush(gp))
                        {
                            pg.CenterColor = Color.FromArgb(a, 255, 255, 255);
                            pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                            g.FillPath(pg, gp);
                        }
                    }
                    g.Clip = saved;
                }
            }
        }

        private Bitmap pendingPreview;
        private volatile bool previewReady;
        private volatile bool previewBuilding;

        private void EnsurePreviewBackdrop()
        {
            if (!windowed || previewBackdrop != null || previewBuilding) return;
            previewBuilding = true;
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                Bitmap b = null;
                try
                {
                    string wp = Native.GetWallpaperPath();
                    if (!string.IsNullOrEmpty(wp) && File.Exists(wp))
                    {
                        using (Image raw = Image.FromFile(wp))
                        using (Bitmap src = new Bitmap(raw))
                            b = ImageFx.CoverFit(src, w, h);
                    }
                }
                catch (Exception ex) { Config.Log("预览壁纸底生成失败: " + ex.Message); }
                pendingPreview = b;
                previewReady = true;
            });
        }

        private void AdoptPendingPreview()
        {
            if (!previewReady) return;
            previewReady = false;
            previewBuilding = false;
            if (previewBackdrop != null) previewBackdrop.Dispose();
            previewBackdrop = pendingPreview;
            pendingPreview = null;
            MarkDirtyAll();
        }

        private void DrawPreviewBackdrop(Graphics g)
        {
            if (!windowed) return;
            EnsurePreviewBackdrop();
            if (previewBackdrop == null) return;
            g.DrawImage(previewBackdrop, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                0, 0, previewBackdrop.Width, previewBackdrop.Height, GraphicsUnit.Pixel);
        }

        // ================================================================
        //  右下角按钮
        // ================================================================

        private const string GlyphTrash = "\uE74D";
        private const string GlyphGear = "\uE713";
        private static Font glyphFont;
        private static float glyphFontPx;

        private Font GlyphFont(float px)
        {
            if (glyphFont != null && Math.Abs(glyphFontPx - px) < 0.5f) return glyphFont;
            if (glyphFont != null) { glyphFont.Dispose(); glyphFont = null; }
            string fam = "Segoe Fluent Icons";
            try { using (FontFamily ff = new FontFamily(fam)) { } }
            catch { fam = "Segoe MDL2 Assets"; }
            glyphFont = new Font(fam, px, FontStyle.Regular, GraphicsUnit.Pixel);
            glyphFontPx = px;
            return glyphFont;
        }

        private void DrawControlButton(Graphics g, Rectangle region)
        {
            bool showTrash = dragging || cfg.showControlButton;
            if (showTrash && !trashRect.IsEmpty && Touches(region, Inflate(trashRect, 6)))
                DrawRoundButton(g, trashRect, trashHover, GlyphTrash, dragging);

            if (cfg.showControlButton && !ctrlRect.IsEmpty && Touches(region, Inflate(ctrlRect, 6)))
                DrawRoundButton(g, ctrlRect, ctrlHover, GlyphGear, false);
        }

        private static Rectangle Inflate(Rectangle r, int by)
        {
            r.Inflate(by, by);
            return r;
        }

        /// <summary>
        /// Win11 的「subtle button」：圆形玻璃底 + 1px 白描边 + 悬停加深。
        /// 平时只有 8% 的白，悬停 16%，按下缩到 0.94 —— 和系统按钮的手感一致。
        /// </summary>
        private void DrawRoundButton(Graphics g, Rectangle rect, float hover, string glyph, bool danger)
        {
            float k = 1f + 0.06f * hover;
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float r = rect.Width / 2f * k;
            RectangleF rr = new RectangleF(cx - r, cy - r, r * 2, r * 2);

            // 投影
            using (GraphicsPath sp = new GraphicsPath())
            {
                sp.AddEllipse(cx - r, cy - r + 1.5f * scale, r * 2, r * 2);
                using (PathGradientBrush pg = new PathGradientBrush(sp))
                {
                    pg.CenterColor = Color.FromArgb(60, 0, 0, 0);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, 0, 0, 0) };
                    g.FillPath(pg, sp);
                }
            }

            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(rr);
                int bg = (int)((hover > 0.02f ? 34 : 18) * (0.55f + 0.45f * hover));
                if (danger) bg = (int)((hover > 0.02f ? 96 : 62));
                Color fill = danger
                    ? Color.FromArgb(bg, 255, 96, 96)
                    : Color.FromArgb(bg, 255, 255, 255);
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);

                int strokeA = (int)((hover > 0.02f ? 74 : 40) * (0.6f + 0.4f * hover));
                using (Pen pen = new Pen(Color.FromArgb(strokeA, 255, 255, 255), Math.Max(1f, scale)))
                    g.DrawPath(pen, p);
            }

            Font f = GlyphFont(rect.Height * 0.40f);
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                int ga = (int)((215 + 40 * hover));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, ga), 255, 255, 255)))
                    g.DrawString(glyph, f, b, rr, sf);
            }
        }

        // ================================================================
        //  帧循环
        // ================================================================

        private void OnFrame()
        {
            if (clock != null) clock.FrameConsumed();
            if (!IsHandleCreated || IsDisposed) return;

            double now = Clock.Now;
            float dt = lastFrameTime == 0 ? 0.016f : (float)(now - lastFrameTime);
            lastFrameTime = now;
            if (dt <= 0f) dt = 0.016f;
            if (dt > 0.10f) dt = 0.10f;

            bool need = false;
            bool fadeChanged = false;

            // ---- 滚动 ----
            float diff = scrollTarget - scroll;
            if (Math.Abs(diff) > 0.4f)
            {
                scroll += diff * Math.Min(1f, 20f * dt);
                if (Math.Abs(scrollTarget - scroll) < 0.4f) scroll = scrollTarget;
                need = true;
                MarkDirtyGrid();
            }
            else if (scroll != scrollTarget)
            {
                scroll = scrollTarget;
                need = true;
                MarkDirtyGrid();
            }

            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }

            // ---- 悬停 ----
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem it = snapshot[i];
                float target = (i == hoverIndex) ? 1f : 0f;
                if (it.hover == target) continue;
                float rate = target > it.hover ? 15f : 18f;
                it.hover = Ease.Toward(it.hover, target, rate, dt);
                MarkDirty(HoverRectOf(i));
                need = true;
            }

            // ---- 按钮悬停 ----
            if (cfg.showControlButton)
            {
                float tc = hotButton == 2 ? 1f : 0f;
                float tt = hotButton == 1 ? 1f : 0f;
                if (Math.Abs(ctrlHover - tc) > 0.002f)
                {
                    ctrlHover = Ease.Toward(ctrlHover, tc, 18f, dt);
                    MarkDirty(Inflate(ctrlRect, 8));
                    need = true;
                }
                else if (ctrlHover != tc) { ctrlHover = tc; MarkDirty(Inflate(ctrlRect, 8)); need = true; }

                if (Math.Abs(trashHover - tt) > 0.002f)
                {
                    trashHover = Ease.Toward(trashHover, tt, 18f, dt);
                    MarkDirty(Inflate(trashRect, 8));
                    need = true;
                }
                else if (trashHover != tt) { trashHover = tt; MarkDirty(Inflate(trashRect, 8)); need = true; }
            }

            // ---- 空闲淡出 ----
            // ★ 只改整体透明度，**一个像素都不用重画**。原来这里也置 need = true，
            //   于是 0.4 秒的淡出会连做 25 次整窗重绘（4K 下每次 22ms）——
            //   纯浪费。现在单独走"只上传"的分支。
            float fadeWas = fadeLevel;
            if (Math.Abs(fadeLevel - fadeTarget) > 0.0015f)
            {
                fadeLevel = Ease.Toward(fadeLevel, fadeTarget, 1f / FadeSec * 2.2f, dt);
                if (Math.Abs(fadeLevel - fadeTarget) < 0.002f) fadeLevel = fadeTarget;
                fadeChanged = true;
            }
            else if (fadeLevel != fadeTarget)
            {
                fadeLevel = fadeTarget;
                fadeChanged = true;
            }
            // 淡入/淡出刚刚走完 → 需要排查"恢复后画面不对"时把渲染面导出来
            if (fadeChanged && fadeWas != fadeLevel && fadeLevel == fadeTarget)
                DumpFadeSurface(fadeLevel);

            // ---- 点击水波纹 ----
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem ri = snapshot[i];
                if (ri.ripple < 0f) continue;
                ri.ripple += dt / 0.42f;
                if (ri.ripple >= 1f) { ri.ripple = -1f; continue; }
                MarkDirty(TileRectOf(i));
                need = true;
            }

            // ---- 拖拽退让 ----
            if (dragging && dragPreview != null)
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    TileItem di = snapshot[i];
                    if (di.animSlot < 0f) continue;
                    int target = Array.IndexOf(dragPreview, i);
                    if (target < 0) continue;

                    Rectangle before = CardRectF(di.animSlot);
                    float d = target - di.animSlot;
                    if (Math.Abs(d) < 0.008f) { di.animSlot = target; continue; }
                    di.animSlot += d * Math.Min(1f, 16f * dt);
                    if (Math.Abs(target - di.animSlot) < 0.008f) di.animSlot = target;
                    Rectangle after = CardRectF(di.animSlot);

                    Rectangle dir = Rectangle.Union(before, after);
                    dir.Inflate(shadowPadPx + 2, shadowPadPx + 2);
                    MarkDirty(dir);
                    need = true;
                }
            }

            // ---- 落点驻留 ----
            if (dragging)
            {
                int pc, pr;
                CellAt(dragPos, out pc, out pr);
                if (pc != pendDropCol || pr != pendDropRow)
                {
                    pendDropCol = pc; pendDropRow = pr;
                    dropDwellStart = Clock.NowMs;
                }
                int pi = InsertIndexAt(dragPos);
                if (pi != pendDropIndex)
                {
                    pendDropIndex = pi;
                    dropDwellStart = Clock.NowMs;
                }
                ApplyPendingDrop(false);
            }

            // ---- 自由排列动画 ----
            if (!cfg.autoArrange)
            {
                freeAnimBusy = false;
                StepFreeAnim(snapshot, dt);
                if (freeAnimBusy) need = true;
            }

            // ---- 启动反馈 ----
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem li = snapshot[i];
                if (li.launchAnim <= 0f) continue;
                li.launchAnim -= dt / 0.45f;
                if (li.launchAnim < 0f) li.launchAnim = 0f;
                MarkDirty(TileRectOf(i));
                need = true;
            }

            if (demoHover >= 0) hoverIndex = demoHover;

            // ---- 烘焙 ----
            // 只负责烤，脏区由 EnsureCaches 自己按卡片标；这里不再额外置 need，
            // 否则"一张都没烤成"时会退化成每帧整窗重绘。
            if (CachesIncomplete()) EnsureCaches(7);

            if (dirty) { dirty = false; need = true; }

            // ---- 出帧 ----
            double frameMs = 0;
            if (need)
            {
                Rectangle region = dirtyFull ? Rectangle.Empty : dirtyRect;
                dirtyRect = Rectangle.Empty;
                dirtyFull = false;
                dirty = false;
                long ft0 = Stopwatch.GetTimestamp();
                Render(region);
                frameMs = Elapsed(ft0);
            }
            else if (fadeChanged)
            {
                // 画面没变，只是整体透明度变了：只把"真有内容"的那一块重新交给 DWM。
                // 整块画布其余像素本来就是 alpha=0，透明度怎么变都看不见。
                long ft0 = Stopwatch.GetTimestamp();
                surface.Present(Handle, Left, Top, FadePresentRegion(), fadeLevel);
                frameMs = Elapsed(ft0);
                perf.Fade(frameMs);
                perf.Tick();
            }

            // 动画连续跑一段之后报一次平均帧时间 —— 只在详细日志里写，
            // 平时一行都不产生。这是判断"到底哪一版更流畅"的唯一客观依据。
            if (need)
            {
                animStreak++;
                animStreakMs += frameMs;
            }
            else if (animStreak > 0)
            {
                if (animStreak >= 8 && Config.Verbose)
                    Config.Log("动画: " + animStreak + " 帧，平均 " + (animStreakMs / animStreak).ToString("0.0") +
                               " ms/帧（" + (1000.0 / Math.Max(0.1, animStreakMs / animStreak)).ToString("0") + " fps）");
                animStreak = 0;
                animStreakMs = 0;
            }

            // ---- 决定下一帧的节奏 ----
            bool animating = need || fadeChanged || CachesIncomplete();
            if (animating) Wake(120);
            if (clock != null)
            {
                int until = unchecked(animUntilMs - Clock.NowMs);
                clock.SetRate(until > 0 ? 60 : 0);
            }
        }

        private int animStreak;
        private double animStreakMs;

        /// <summary>
        /// 淡入淡出时真正需要重新上传的区域：所有可见卡片 + 右下角按钮。
        /// 拿不准的情况（拖拽中、墙是隐藏的）返回 Empty —— 那是"整窗"。
        /// </summary>
        private Rectangle FadePresentRegion()
        {
            if (dragging || wallHidden) return Rectangle.Empty;
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            Rectangle r = Rectangle.Empty;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i] == null || snapshot[i].cacheNormal == null) continue;
                Rectangle cr = CardRect(i);
                if (!VisibleRect(cr)) continue;
                cr.Inflate(shadowPadPx + 2, shadowPadPx + 2);
                r = r.IsEmpty ? cr : Rectangle.Union(r, cr);
            }
            if (!trashRect.IsEmpty) r = r.IsEmpty ? trashRect : Rectangle.Union(r, trashRect);
            if (cfg.showControlButton && !ctrlRect.IsEmpty)
                r = r.IsEmpty ? ctrlRect : Rectangle.Union(r, ctrlRect);
            r.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            return (r.Width > 0 && r.Height > 0) ? r : Rectangle.Empty;
        }

        // ================================================================
        //  逻辑节拍（250ms）
        // ================================================================

        private void OnLogicTick()
        {
            if (IsDisposed || !IsHandleCreated) return;
            logicTick++;

            // ---- 空闲淡出 ----
            int idleMs = Native.SystemIdleMs();
            int sinceStart = (int)(Clock.Now * 1000);
            if (sinceStart >= 0 && sinceStart < idleMs) idleMs = sinceStart;
            fadeTarget = (cfg.idleHideSeconds > 0.1 && idleMs >= cfg.idleHideSeconds * 1000.0) ? 0 : 1;

            // ★ 只改 fadeTarget 是**不会动**的：空闲时帧时钟的 rate 是 0（线程纯睡，
            //   这是 3.0 省 CPU 的核心），没人投递下一帧，fadeLevel 就永远停在原处。
            //   三个可见症状全出自这一处：
            //     · 「空闲隐藏失效」—— 等多久卡片都还在桌面常驻；
            //     · 更怪的：等你动一下鼠标、帧时钟被输入短暂唤醒时，墙才突然整块淡出；
            //     · 淡出之后再也回不来（唤醒只给了一瞬间的帧，淡入淡到一半就冻住）。
            //   所以淡入淡出必须自己把帧时钟叫起来，且要覆盖完整个过渡。
            if (fadeLevel != fadeTarget) Wake((int)(FadeSec * 1000) + 400);

            // ---- 后台加载完成 ----
            if (blurReady) AdoptPendingBlur();
            if (previewReady) AdoptPendingPreview();
            DrainDisposeQueue();

            // 调试：TILEDESK_DUMP_SURFACE=1 时把渲染面（含 alpha 通道）落盘成 PNG，
            // 用来证明"空白处真的是透明的"——普通截图只有合成后的像素，看不出透明。
            // 放在逻辑节拍里而不是首帧：首帧时卡片还没烤出来，导出的面是空的。
            // ★ TILEDESK_DUMP_FADE=1 时还会在每次淡入淡出**结束时**各导出一张：
            //   判断"空闲隐藏后恢复"有没有把卡片/阴影画坏，只能看渲染面本身 ——
            //   屏幕截图里叠着别的窗口和壁纸，分不清是谁变了。
            if (!_surfaceDumped && logicTick >= 20 && !CachesIncomplete() && DumpSurfaceEnabled)
            {
                _surfaceDumped = true;
                DumpSurface("boot");
            }

            if (reloadPending)
            {
                reloadPending = false;
                ApplyFilter();
                Wake(400);
            }
            if (artPending)
            {
                artPending = false;
                ApplyPendingArt();
            }

            // ---- 桌面变化 / 保存 ----
            if (saveDueMs != 0 && Clock.Passed(saveDueMs, 0))
            {
                saveDueMs = 0;
                cfg.Save();
            }
            if (watchDueMs != 0 && Clock.Passed(watchDueMs, 0))
            {
                watchDueMs = 0;
                Config.Log("桌面有变化，自动刷新");
                RefreshItems();
            }

            // ---- 浮层同步 ----
            if (npWindow != null && !npWindow.IsDisposed)
            {
                // 设置里关掉「正在播放控件」时要真的收起来。
                // 原版这个开关只影响"卡片禁区"的计算，控件本身照样显示。
                if (!cfg.showNowPlaying)
                {
                    if (npWindow.Visible) npWindow.Hide();
                }
                else
                {
                    Point npLoc = PointToScreen(new Point(
                        (int)Math.Round(cfg.npMargin * scale),
                        ClientSize.Height - (int)Math.Round(cfg.npMargin * scale) -
                            (int)Math.Round(cfg.npHeight * scale)));
                    npWindow.Sync(Handle, npLoc, (logicTick % 8) == 0, fadeLevel);
                }
            }
            if (deskInfo != null && !deskInfo.IsDisposed)
            {
                Rectangle dr = DeskInfoRect();
                if (!dr.IsEmpty) deskInfo.Sync(Handle, PointToScreen(new Point(dr.X, dr.Y)), (logicTick % 8) == 0);
            }
            if (nowPlaying != null && (logicTick % 16) == 0) nowPlaying.DrainRetired();

            // ---- 层级 / 挂载 ----
            if (logicTick % 8 == 0) EnsurePinned();
            if (logicTick % 8 == 4) KeepAboveDesktop();

            // ---- 幽灵拖拽自愈 ----
            if ((pressCandidate || dragging || edgeArmed) && !Native.KeyDown(Native.VK_LBUTTON))
            {
                pressCandidate = false; pressIndex = -1;
                edgeArmed = false;
                if (dragging) EndDrag(true);
                try { Native.ReleaseCapture(); } catch { }
            }

            // ---- 遮挡采样（每 500ms 一次，别每帧 WindowFromPoint）----
            if (logicTick % 2 == 0)
            {
                int now = Clock.NowMs;
                if (unchecked(now - lastCoveredCheck) > 500)
                {
                    lastCoveredCheck = now;
                    covered = WallCovered();
                }
            }
            bool specPlaying = cfg.showSpectrum && AudioSpectrum.Ready && AudioSpectrum.Active && !covered;
            if (npWindow != null && !npWindow.IsDisposed)
            {
                npWindow.SpectrumActive = specPlaying;
                if (npWindow.SpectrumPainter == null)
                    npWindow.SpectrumPainter = new Action<Graphics, Rectangle>(DrawSpectrumInto);
            }
        }

        private bool WallCovered()
        {
            try
            {
                uint me = (uint)Process.GetCurrentProcess().Id;
                int y = Math.Max(1, ClientSize.Height - 40);
                int[] xs = new int[] { ClientSize.Width / 4, ClientSize.Width / 2, ClientSize.Width * 3 / 4 };
                for (int i = 0; i < xs.Length; i++)
                {
                    Point p = PointToScreen(new Point(xs[i], y));
                    IntPtr h = Native.WindowFromPoint(new Native.POINT(p.X, p.Y));
                    if (h == IntPtr.Zero || h == Handle) return false;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if (pid == me) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private void DrawSpectrumInto(Graphics g, Rectangle strip)
        {
            if (!cfg.showSpectrum) return;
            int n = Math.Max(8, Math.Min(128, cfg.spectrumBars));
            float[] lv = AudioSpectrum.Bands(n);
            if (lv == null || lv.Length != n) return;

            int wantW = Math.Max(64, (int)Math.Round(strip.Width *
                        Math.Max(0.1, Math.Min(1.0, cfg.spectrumWidth))));
            double barRatio = Math.Max(0.15, Math.Min(1.0, cfg.spectrumBarWidth));
            double step = wantW / (double)n;
            int barW = Math.Max(2, (int)Math.Round(step * barRatio));
            if (barRatio >= 0.999) barW = Math.Max(2, (int)Math.Ceiling(step));
            int maxH = Math.Max(20, (int)Math.Round(cfg.spectrumHeight * scale));
            int baseline = strip.Height;
            int x0 = (strip.Width - wantW) / 2;

            for (int i = 0; i < n; i++)
            {
                float h = lv[i] * maxH;
                if (h < 3f) h = 3f;
                RectangleF r = new RectangleF((float)(x0 + i * step), baseline - h, barW, h);
                int op = (int)((46 + 82 * lv[i]) * Math.Max(0.1, Math.Min(2.0, cfg.spectrumOpacity)));
                int a = op > 255 ? 255 : op;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 240, 244, 252)))
                    g.FillRectangle(b, r);
            }
        }

        // ================================================================
        //  鼠标
        // ================================================================

        private Point MouseClient()
        {
            Native.POINT p;
            if (Native.GetCursorPos(out p)) { Native.ScreenToClient(Handle, ref p); return new Point(p.X, p.Y); }
            return Point.Empty;
        }

        private void HandleMouseMove(Point pt, bool leftDown)
        {
            if (!trackingLeave)
            {
                Native.TRACKMOUSEEVENT tme = new Native.TRACKMOUSEEVENT();
                tme.cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
                tme.dwFlags = Native.TME_LEAVE;
                tme.hwndTrack = Handle;
                tme.dwHoverTime = 0;
                if (Native.TrackMouseEvent(ref tme)) trackingLeave = true;
            }

            if (dragging)
            {
                if (!leftDown) { EndDrag(true); return; }
                dragPos = pt;
                if (cfg.autoArrange)
                {
                    int di = InsertIndexAt(pt);
                    pendDropIndex = di;
                    ApplyPendingDrop(false);
                }
                else
                {
                    int c, r;
                    CellAt(pt, out c, out r);
                    if (c != pendDropCol || r != pendDropRow)
                    {
                        pendDropCol = c; pendDropRow = r;
                        dropDwellStart = Clock.NowMs;
                    }
                    ApplyPendingDrop(false);
                }
                MarkDirtyAll();
                Wake(300);
                return;
            }

            int i = IndexAt(pt);

            int btn = 0;
            if (cfg.showControlButton || dragging)
            {
                if (!ctrlRect.IsEmpty && ctrlRect.Contains(pt)) btn = 2;
                else if (!trashRect.IsEmpty && trashRect.Contains(pt)) btn = 1;
            }
            if (btn != hotButton) { hotButton = btn; Wake(200); }

            if (btn != 0)
            {
                if (hoverIndex != -1) { MarkDirty(HoverRectOf(hoverIndex)); hoverIndex = -1; }
                Cursor = Cursors.Hand;
                Wake(200);
                return;
            }

            if (edgeArmed && leftDown)
            {
                if (pressAt.X - pt.X >= SwipeDist)
                {
                    edgeArmed = false;
                    Native.OpenNotificationCenter();
                    Config.Log("边缘手势: 右边缘左滑 -> 唤起通知中心");
                }
                return;
            }

            if (pressCandidate && pressIndex >= 0 && leftDown)
            {
                int ddx = Math.Abs(pt.X - pressAt.X);
                int ddy = Math.Abs(pt.Y - pressAt.Y);
                if (ddx > TapSlop || ddy > TapSlop) { StartDrag(pressIndex, pt); return; }
            }

            if (i != hoverIndex)
            {
                if (hoverIndex >= 0) MarkDirty(HoverRectOf(hoverIndex));
                if (i >= 0) MarkDirty(HoverRectOf(i));
                hoverIndex = i;
                Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
                Wake(400);
            }
        }

        private void ClearHover()
        {
            trackingLeave = false;
            if (hoverIndex != -1)
            {
                MarkDirty(HoverRectOf(hoverIndex));
                hoverIndex = -1;
                Cursor = Cursors.Default;
                Wake(400);
            }
            if (hotButton != 0) { hotButton = 0; Wake(200); }
        }

        // ---------------- 拖拽 ----------------

        private void StartDrag(int index, Point pt)
        {
            BeginDragAnim(index);
            dragging = true;
            dragIndex = index;
            dragPos = pt;
            if (cfg.autoArrange)
            {
                dropIndex = InsertIndexAt(pt);
                pendDropIndex = dropIndex;
                RebuildDragPreview();
            }
            else
            {
                dropIndex = -1;
                dragPreview = null;
                CellAt(pt, out dropCol, out dropRow);
                pendDropCol = dropCol; pendDropRow = dropRow;
                BeginFreeDragAnim(index);
            }
            selIndex = -1;
            hoverIndex = -1;
            Cursor = Cursors.SizeAll;
            try { Native.SetCapture(Handle); } catch { }
            MarkDirtyAll();
            Wake(600);
        }

        private void BeginDragAnim(int from)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            if (animSlotFrom.Length != snapshot.Count) animSlotFrom = new float[snapshot.Count];
            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].animSlot = i;
                snapshot[i].hover = 0f;
                animSlotFrom[i] = i;
            }
        }

        private void ResetDragAnim()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++) snapshot[i].animSlot = -1f;
        }

        private void BeginFreeDragAnim(int from)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count && i < cellCol.Length; i++)
            {
                TileItem it = snapshot[i];
                it.animCol = cellCol[i];
                it.animRow = cellRow[i];
                it.hover = 0f;
            }
            dragFromCol = (from >= 0 && from < cellCol.Length) ? cellCol[from] : 0;
            dragFromRow = (from >= 0 && from < cellRow.Length) ? cellRow[from] : 0;
            freeSwapIndex = -1;
        }

        private void ApplyPendingDrop(bool force)
        {
            if (!dragging) return;
            bool dwellOk = force || Clock.Passed(dropDwellStart, DropDwellMs);
            if (cfg.autoArrange)
            {
                if (pendDropIndex >= 0) dropIndex = pendDropIndex;
                if (dwellOk) RebuildDragPreview();
            }
            else
            {
                if (pendDropCol >= 0) { dropCol = pendDropCol; dropRow = pendDropRow; }
                if (dwellOk) UpdateFreeSwapTarget();
            }
        }

        private void UpdateFreeSwapTarget()
        {
            int owner;
            freeSwapIndex = (dropCol >= 0 && cellOwner.TryGetValue(CellKey(dropCol, dropRow), out owner) &&
                             owner != dragIndex) ? owner : -1;
        }

        private void SettleDraggedFromCursor()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            if (dragIndex < 0 || dragIndex >= snapshot.Count) return;
            float fc, fr;
            CellAtF(dragPos, out fc, out fr);
            snapshot[dragIndex].animCol = fc;
            snapshot[dragIndex].animRow = fr;
        }

        private void StepFreeAnim(List<TileItem> snapshot, float dt)
        {
            for (int i = 0; i < snapshot.Count && i < cellCol.Length; i++)
            {
                TileItem it = snapshot[i];
                if (cellCol[i] < 0 || cellRow[i] < 0) continue;
                if (it.animCol < 0f)
                {
                    bool needMove = dragging && i == freeSwapIndex &&
                                    (Math.Abs(dragFromCol - cellCol[i]) > 0.01f ||
                                     Math.Abs(dragFromRow - cellRow[i]) > 0.01f);
                    if (!needMove) continue;
                    it.animCol = cellCol[i];
                    it.animRow = cellRow[i];
                }
                if (dragging && i == dragIndex) continue;
                float tc = cellCol[i], tr = cellRow[i];
                if (dragging && i == freeSwapIndex) { tc = dragFromCol; tr = dragFromRow; }

                float dc = tc - it.animCol, dr = tr - it.animRow;
                if (Math.Abs(dc) < 0.01f && Math.Abs(dr) < 0.01f)
                {
                    it.animCol = tc; it.animRow = tr;
                    if (!dragging) { it.animCol = -1f; it.animRow = -1f; }
                    continue;
                }
                Rectangle before = CellRectF(it.animCol, it.animRow);
                it.animCol += dc * Math.Min(1f, 16f * dt);
                it.animRow += dr * Math.Min(1f, 16f * dt);
                Rectangle after = CellRectF(it.animCol, it.animRow);

                Rectangle dir = Rectangle.Union(before, after);
                dir.Inflate(shadowPadPx + 2, shadowPadPx + 2);
                MarkDirty(dir);
                freeAnimBusy = true;
            }
        }

        private void RebuildDragPreview()
        {
            int n;
            lock (dataLock) { n = view.Count; }
            if (n <= 0 || dragIndex < 0 || dragIndex >= n) { dragPreview = null; return; }

            List<int> seq = new List<int>(n);
            for (int i = 0; i < n; i++) if (i != dragIndex) seq.Add(i);
            int insertAt = dragIndex < dropIndex ? dropIndex - 1 : dropIndex;
            if (insertAt < 0) insertAt = 0;
            if (insertAt > seq.Count) insertAt = seq.Count;
            seq.Insert(insertAt, dragIndex);
            dragPreview = seq.ToArray();
        }

        private int InsertIndexAt(Point pt)
        {
            int count;
            lock (dataLock) { count = view.Count; }
            int relX = pt.X - gridLeft;
            int relY = pt.Y - gridTop - gridTopPad + (int)Math.Round(scroll);
            int cellX = cardW + gapPx;
            int cellY = cardH + gapPx;
            int col = relX / cellX;
            int row = relY / cellY;
            if (col < 0) col = 0;
            if (col >= cols) col = cols - 1;
            if (row < 0) row = 0;
            int idx = row * cols + col;
            if (relX - col * cellX > cardW / 2) idx++;
            return Math.Max(0, Math.Min(count, idx));
        }

        private bool DragOverTrash()
        {
            return dragging && !trashRect.IsEmpty && trashRect.Contains(dragPos);
        }

        private void EndDrag(bool commit)
        {
            int from = dragIndex;
            if (dragging && !DragOverTrash())
            {
                CellAt(dragPos, out pendDropCol, out pendDropRow);
                pendDropIndex = InsertIndexAt(dragPos);
            }
            ApplyPendingDrop(true);
            int to = dropIndex;
            bool onTrash = DragOverTrash();

            if (!cfg.autoArrange && from >= 0 && !onTrash) SettleDraggedFromCursor();

            List<TileItem> reordered = null;
            TileItem movedItem = null;
            int finalSlot = -1;
            bool freeMoved = false;
            int freeCol = 0, freeRow = 0;

            if (commit && from >= 0 && !onTrash)
            {
                List<TileItem> snap;
                lock (dataLock) { snap = new List<TileItem>(view); }
                if (from < snap.Count)
                {
                    if (!cfg.autoArrange)
                    {
                        DropCell(out freeCol, out freeRow);
                        TileItem moved = snap[from];
                        int other;
                        if (cellOwner.TryGetValue(CellKey(freeCol, freeRow), out other) &&
                            other >= 0 && other < snap.Count && other != from && other < cellCol.Length)
                        {
                            snap[other].cellCol = cellCol[from];
                            snap[other].cellRow = cellRow[from];
                        }
                        moved.cellCol = freeCol;
                        moved.cellRow = freeRow;
                        freeMoved = true;
                        movedItem = moved;
                        AssignCells();
                        SavePositions();
                        Relayout();
                        InvalidateAcrylicCards();
                    }
                    else if (to >= 0 && !(to == from || to == from + 1))
                    {
                        TileItem moved = snap[from];
                        snap.RemoveAt(from);
                        int ins = to > from ? to - 1 : to;
                        ins = Math.Max(0, Math.Min(snap.Count, ins));
                        snap.Insert(ins, moved);
                        reordered = snap;
                        movedItem = moved;
                        finalSlot = ins;
                    }
                }
            }

            if (reordered != null)
            {
                cfg.order = new List<string>();
                foreach (TileItem it in reordered) cfg.order.Add(Path.GetFileName(it.path));
                cfg.sortMode = "Custom";
                cfg.sortDescending = false;
                ApplyFilter();
            }

            dragging = false; dragIndex = -1; dropIndex = -1; dragPreview = null;
            dropCol = -1; dropRow = -1;
            ResetDragAnim();
            pressCandidate = false; pressIndex = -1;
            Cursor = Cursors.Default;
            try { Native.ReleaseCapture(); } catch { }
            MarkDirtyAll();
            Wake(400);

            if (!commit) return;
            if (onTrash && from >= 0) { DeleteToRecycleBin(from); return; }

            if (freeMoved)
            {
                cfg.Save();
                Config.Log("卡片位置: " + movedItem.name + " -> 第 " + (freeRow + 1) + " 行第 " + (freeCol + 1) + " 列");
            }
            else if (reordered != null)
            {
                cfg.Save();
                Config.Log("拖拽重排: " + movedItem.name + " -> 第 " + (finalSlot + 1) + " 位");
            }
        }

        private int IndexAt(Point pt)
        {
            int relX = pt.X - gridLeft;
            int relY = pt.Y - gridTop - gridTopPad + (int)Math.Round(scroll);
            if (relX < 0 || relY < 0) return -1;

            int cellX = cardW + gapPx;
            int cellY = cardH + gapPx;
            int col = relX / cellX;
            int row = relY / cellY;
            if (relX - col * cellX > cardW) return -1;
            if (relY - row * cellY > cardH) return -1;
            int idx;
            if (cellOwner.TryGetValue(CellKey(col, row), out idx))
                return (idx >= 0 && idx < cellCol.Length) ? idx : -1;
            return -1;
        }

        private void StartRipple(int index, Point pt)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            if (index < 0 || index >= snapshot.Count) return;
            Rectangle cr = CardRect(index);
            TileItem it = snapshot[index];
            it.rippleX = pt.X - cr.X;
            it.rippleY = pt.Y - cr.Y;
            it.ripple = 0.0001f;
            MarkDirty(TileRectOf(index));
            Wake(500);
        }

        private void ZoomCards(int dir)
        {
            int step = Math.Max(2, (int)Math.Round(cfg.cardWidth * 0.025));
            int w = cfg.cardWidth + dir * step;
            w = Math.Max(70, Math.Min(360, w));
            if (w == cfg.cardWidth) return;
            cfg.cardWidth = w;
            cfg.cardHeight = (int)Math.Round(w * 1.5);
            RecomputeMetrics();
            InvalidateAllCards();
            ScheduleSave();
            Wake(400);
            MarkDirtyAll();
        }

        private void ScheduleSave()
        {
            saveDueMs = Clock.NowMs + 1200;
        }

        // ================================================================
        //  右键菜单
        // ================================================================

        private void OpenMenu(Point at, List<MenuEntry> items)
        {
            if (items == null || items.Count == 0) return;
            try
            {
                Point screenPt = PointToScreen(at);
                int idx = WpfMenu.Show(items, screenPt, scale);
                if (idx >= 0 && idx < items.Count)
                {
                    MenuEntry e = items[idx];
                    if (e != null && e.enabled && !e.separator && e.action != null) e.action();
                }
            }
            catch (Exception ex) { Config.Log("弹出菜单失败: " + ex.Message); }
            // 菜单关了就把"选中态"清掉：那个框只是用来指示菜单作用于哪张卡，
            // 留着的话关掉菜单之后蓝框会一直挂在那儿（原版的遗留问题）。
            if (selIndex >= 0) selIndex = -1;
            MarkDirtyAll();
            Wake(300);
        }

        private void ShowControlMenu(Point at)
        {
            List<MenuEntry> items = new List<MenuEntry>();
            items.Add(MenuEntry.Item("设置…", delegate() { OpenSettings(); }));
            items.Add(MenuEntry.Item("刷新磁贴", delegate() { RefreshItems(); }));
            items.Add(MenuEntry.Check("自动排列卡片", cfg.autoArrange, delegate() { ToggleAutoArrange(); }));
            if (!cfg.autoArrange)
                items.Add(MenuEntry.Item("重置卡片位置", delegate() { ResetCardPositions(); }));
            items.Add(MenuEntry.Item("重新载入壁纸", delegate()
            {
                acrylicDirty = true;
                EnsureAcrylic(true);
                MarkDirtyAll();
            }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Check("隐藏原生桌面图标", SystemIntegration.DesktopIconsHidden,
                delegate()
                {
                    if (!SystemIntegration.SetDesktopIcons(!SystemIntegration.DesktopIconsHidden))
                        MessageBox.Show("切换桌面图标没有生效。\n可以手动右键桌面 → 查看 → 显示桌面图标。",
                            "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            items.Add(MenuEntry.Check("开机自动启动", SystemIntegration.AutoStartEnabled,
                delegate()
                {
                    SystemIntegration.SetAutoStart(!SystemIntegration.AutoStartEnabled);
                    cfg.autoStartWithWindows = SystemIntegration.AutoStartEnabled;
                    cfg.Save();
                }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Check("写详细日志", cfg.verboseLog, delegate()
            {
                cfg.verboseLog = !cfg.verboseLog;
                Config.Verbose = cfg.verboseLog;
                cfg.Save();
                Config.Log("=== 详细日志已" + (cfg.verboseLog ? "打开" : "关闭") + " ===");
            }));
            items.Add(MenuEntry.Sep());
            MenuEntry ver = MenuEntry.Item("TileDesk v" + AppVersion.Text, null);
            ver.enabled = false;
            items.Add(ver);
            items.Add(MenuEntry.Item("退出 TileDesk", delegate()
            {
                if (tray != null) tray.Visible = false;
                Application.Exit();
            }));
            OpenMenu(at, items);
        }

        private void ShowItemMenu(int index, Point at)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            bool hasCustom = FindCustomCover(it) != null;
            List<MenuEntry> items = new List<MenuEntry>();
            items.Add(MenuEntry.Item("打开", delegate() { Launch(index); }));
            items.Add(MenuEntry.Item("打开文件所在位置", delegate()
            {
                int r = Native.ShowInExplorer(it.path);
                if (r != 0) Config.Log("打开位置失败: hr=0x" + r.ToString("X8"));
            }));
            items.Add(MenuEntry.Item("属性", delegate()
            {
                try { Native.ShowProperties(Handle, it.path); }
                catch (Exception ex) { Config.Log("打开属性失败: " + ex.Message); }
            }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Item("设置自定义封面…", delegate() { SetCustomCover(index); }));
            items.Add(MenuEntry.Item("用 Steam appid 设置封面…", delegate() { SetCoverByAppId(index); }));
            if (hasCustom)
                items.Add(MenuEntry.Item("清除自定义封面", delegate()
                {
                    DeleteCustomCoverFiles(it);
                    RefreshItems();
                }));
            items.Add(MenuEntry.Item("从磁贴中隐藏", delegate()
            {
                string fn = Path.GetFileName(it.path);
                if (!cfg.hidden.Contains(fn)) { cfg.hidden.Add(fn); cfg.Save(); }
                RefreshItems();
            }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Item("重命名…", delegate() { RenameItem(index); }));
            items.Add(MenuEntry.Item("删除", delegate() { DeleteToRecycleBin(index); }));
            items.Add(MenuEntry.Danger("永久删除", delegate() { DeletePermanently(index); }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Item("设置…", delegate() { OpenSettings(); }));
            OpenMenu(at, items);
        }

        private void ShowTrashMenu(Point at)
        {
            long items = 0, bytes = 0;
            bool ok = Native.QueryRecycleBin(out items, out bytes);

            List<MenuEntry> list = new List<MenuEntry>();
            MenuEntry head = MenuEntry.Item(
                ok ? ("回收站 · " + items + " 项 · " + FormatSize(bytes)) : "回收站：不可用", null);
            head.enabled = false;
            list.Add(head);
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Item("打开回收站窗口", delegate() { OpenRecycleBin(); }));
            if (ok && items > 0)
                list.Add(MenuEntry.Item("清空回收站…", delegate() { EmptyRecycleBin(); }));
            OpenMenu(at, list);
        }

        private void EmptyRecycleBin()
        {
            long items = 0, bytes = 0;
            Native.QueryRecycleBin(out items, out bytes);
            if (MessageBox.Show(
                    "确定清空回收站？\n\n" + items + " 项，约 " + FormatSize(bytes) +
                    "\n\n清空后里面的东西就无法还原了。",
                    "TileDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            int hr = Native.EmptyRecycleBin(Handle);
            if (hr == 0)
            {
                Config.Log("已清空回收站");
                MessageBox.Show("回收站已清空。", "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Config.Log("清空回收站失败: hr=0x" + hr.ToString("X8"));
                MessageBox.Show("清空回收站失败（0x" + hr.ToString("X8") + "）。", "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        }

        private void OpenRecycleBin()
        {
            int err = Native.OpenRecycleBin(Handle);
            if (err == 0) return;
            Config.Log("打开回收站失败: err=0x" + err.ToString("X8"));
            MessageBox.Show(
                "打不开回收站（系统返回 0x" + err.ToString("X8") + "）。\n\n" +
                "当前运行环境：" + Native.DescribeEnvironment(),
                "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================================================================
        //  托盘
        // ================================================================

        private void ShowTrayMenu()
        {
            Point sp = Cursor.Position;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = (int)Math.Round(8 * scale);
            int w = (int)Math.Round(330 * scale * 1.25);
            Point local = PointToClient(sp);

            if (sp.X <= wa.Left) local.X = margin;
            else if (sp.X >= wa.Right - 1) local.X = ClientSize.Width - w - margin;
            if (sp.Y <= wa.Top) local.Y = margin;
            else if (sp.Y >= wa.Bottom - 1) local.Y = Math.Max(margin, ClientSize.Height - margin * 3);

            OpenMenu(local, BuildTrayEntries());
        }

        private List<MenuEntry> BuildTrayEntries()
        {
            bool desktopMode = string.Equals(cfg.windowMode, "Desktop", StringComparison.OrdinalIgnoreCase);
            List<MenuEntry> list = new List<MenuEntry>();

            list.Add(MenuEntry.Item(wallHidden ? "显示磁贴墙" : "隐藏磁贴墙", delegate()
            {
                SetWallHidden(!wallHidden);
            }));
            list.Add(MenuEntry.Item("刷新磁贴", delegate() { RefreshItems(); }));
            list.Add(MenuEntry.Check("卡片深色模式", cfg.darkCards, delegate()
            {
                cfg.darkCards = !cfg.darkCards;
                cfg.Save();
                InvalidateAcrylicCards();
                MarkDirtyAll();
                Wake(300);
            }));
            list.Add(MenuEntry.Check("自动排列卡片", cfg.autoArrange, delegate() { ToggleAutoArrange(); }));
            if (!cfg.autoArrange)
                list.Add(MenuEntry.Item("重置卡片位置", delegate() { ResetCardPositions(); }));
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Item("设置…", delegate() { OpenSettings(); }));
            list.Add(MenuEntry.Item("打开数据目录", delegate() { Native.ShowInExplorer(Config.DataDir); }));
            list.Add(MenuEntry.Item("查看日志", delegate()
            {
                try { Process.Start("notepad.exe", "\"" + Config.LogPath + "\""); } catch { }
            }));
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Check("隐藏原生桌面图标", SystemIntegration.DesktopIconsHidden,
                delegate()
                {
                    if (!SystemIntegration.SetDesktopIcons(!SystemIntegration.DesktopIconsHidden))
                        MessageBox.Show("切换桌面图标没有生效。\n可以手动右键桌面 → 查看 → 显示桌面图标。",
                            "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            list.Add(MenuEntry.Check("开机自动启动", SystemIntegration.AutoStartEnabled, delegate()
            {
                SystemIntegration.SetAutoStart(!SystemIntegration.AutoStartEnabled);
                cfg.autoStartWithWindows = SystemIntegration.AutoStartEnabled;
                cfg.Save();
            }));
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Check("窗口停在桌面底层", !desktopMode, delegate() { SwitchMode("Bottom"); }));
            list.Add(MenuEntry.Check("窗口停在壁纸层", desktopMode, delegate() { SwitchMode("Desktop"); }));
            list.Add(MenuEntry.Sep());
            MenuEntry ver = MenuEntry.Item("TileDesk v" + AppVersion.Text, null);
            ver.enabled = false;
            list.Add(ver);
            list.Add(MenuEntry.Item("退出 TileDesk", delegate()
            {
                if (tray != null) tray.Visible = false;
                Application.Exit();
            }));
            return list;
        }

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = BuildTrayIcon();
            tray.Text = "TileDesk v" + AppVersion.Text + " — 桌面磁贴";
            tray.Visible = true;
            tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };
            tray.DoubleClick += delegate(object s, EventArgs e)
            {
                if (MessageBox.Show("要退出 TileDesk 吗？\n\n选「否」则只是显示 / 隐藏磁贴墙。",
                        "TileDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    tray.Visible = false;
                    Application.Exit();
                }
                else SetWallHidden(!wallHidden);
            };
        }

        private Icon BuildTrayIcon()
        {
            try
            {
                Bitmap b = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 76, 154, 255)))
                    using (GraphicsPath p = Shapes.Round(new RectangleF(1, 1, 13, 19), 3))
                        g.FillPath(br, p);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 156, 205, 255)))
                    using (GraphicsPath p = Shapes.Round(new RectangleF(17, 12, 13, 19), 3))
                        g.FillPath(br, p);
                }
                trayIconHandle = b.GetHicon();
                b.Dispose();
                if (trayIconHandle != IntPtr.Zero)
                {
                    Icon ic = Icon.FromHandle(trayIconHandle);
                    if (ic != null) return ic;
                }
            }
            catch (Exception ex) { Config.Log("生成托盘图标失败: " + ex.Message); }
            return SystemIcons.Application;
        }

        private void SetWallHidden(bool h)
        {
            wallHidden = h;
            MarkDirtyAll();
            Wake(400);
            Config.Log(h ? "磁贴墙: 已隐藏" : "磁贴墙: 已显示");
        }

        // ================================================================
        //  加载桌面项目
        // ================================================================

        public void StartLoad()
        {
            int ver = Interlocked.Increment(ref loadVersion);
            ThreadPool.QueueUserWorkItem(delegate(object o) { LoadWorker(ver); });
        }

        public void RefreshItems() { StartLoad(); }

        private List<string> CollectPaths()
        {
            List<string> dirs = new List<string>();
            string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(user) && Directory.Exists(user)) dirs.Add(user);
            if (!string.IsNullOrEmpty(pub) && Directory.Exists(pub)) dirs.Add(pub);
            foreach (string extra in cfg.extraFolders)
            {
                try { if (!string.IsNullOrEmpty(extra) && Directory.Exists(extra)) dirs.Add(extra); }
                catch { }
            }

            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                try
                {
                    foreach (string f in Directory.GetFileSystemEntries(dir))
                    {
                        string name = Path.GetFileName(f);
                        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if ((File.GetAttributes(f) & FileAttributes.Hidden) != 0)
                        {
                            string ext0 = Path.GetExtension(f).ToLowerInvariant();
                            if (ext0 != ".lnk" && ext0 != ".url") continue;
                        }
                        if (IsExcluded(name)) continue;
                        if (cfg.hidden.Contains(name)) continue;
                        if (seen.Add(name)) files.Add(f);
                    }
                }
                catch (Exception ex) { Config.Log("枚举目录失败 " + dir + " : " + ex.Message); }
            }
            return files;
        }

        private bool IsExcluded(string name)
        {
            foreach (string pat in cfg.exclude)
            {
                if (string.IsNullOrEmpty(pat)) continue;
                if (string.Equals(pat, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (pat.StartsWith("*") && name.EndsWith(pat.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 后台扫描 + 加载素材。
        ///
        /// 分两段发布：先把"有哪些卡片"交出去（墙立刻能画出亚克力底），
        /// 再逐个把图标/封面填进去。这样启动时不是空白等三秒，而是卡片先出现、
        /// 封面随后"长"出来 —— 观感和系统自己加载缩略图一致。
        /// </summary>
        private void LoadWorker(int ver)
        {
            List<TileItem> old;
            lock (dataLock) { old = allItems; }

            // ★ 按路径索引旧卡片：桌面文件一有风吹草动就会走 RefreshItems，
            //   如果每次都新建 TileItem，art/icon 会先变成 null 再重新加载 ——
            //   卡片就在"亚克力"和"封面"之间反复闪（用户实测：部分游戏回退到默认图标）。
            //   同路径的旧素材直接转移过来，刷新时画面完全不变。
            Dictionary<string, TileItem> oldByPath =
                new Dictionary<string, TileItem>(StringComparer.OrdinalIgnoreCase);
            if (old != null)
            {
                foreach (TileItem o in old)
                {
                    if (o == null || string.IsNullOrEmpty(o.path)) continue;
                    oldByPath[o.path] = o;
                }
            }

            List<TileItem> list = new List<TileItem>();
            foreach (string p in CollectPaths())
            {
                try
                {
                    TileItem it = new TileItem();
                    it.path = p;
                    it.cellCol = -1;
                    it.cellRow = -1;
                    it.name = Path.GetFileNameWithoutExtension(p);
                    if (Directory.Exists(p)) it.name = Path.GetFileName(p);
                    it.kind = KindOf(p);
                    it.stamp = File.GetLastWriteTime(p);

                    // ---- 复用旧素材 ----
                    TileItem prev;
                    if (oldByPath.TryGetValue(p, out prev))
                    {
                        it.icon = prev.icon;
                        it.art = prev.art;
                        it.header = prev.header;
                        it.tint = prev.tint;
                        it.artPath = prev.artPath;
                        it.steamId = prev.steamId;
                        it.iconSource = prev.iconSource;
                        // 从旧 item 摘走：旧 item 之后只会被 DropCache（只释放位图缓存），
                        // 但置空可以避免任何意外的重复释放。
                        prev.icon = null;
                        prev.art = null;
                        prev.header = null;
                    }

                    if (it.kind == "url" || it.kind == "lnk")
                    {
                        if (it.steamId == null) it.steamId = SteamArt.DetectAppId(p);
                        if (it.kind == "url")
                        {
                            string iconFile = SteamArt.ParseIconFile(p);
                            if (!string.IsNullOrEmpty(iconFile) && File.Exists(iconFile))
                                it.iconSource = iconFile;
                        }
                    }
                    if (it.iconSource == null) it.iconSource = it.path;
                    list.Add(it);
                }
                catch (Exception ex) { Config.Log("读取项目失败 " + p + " : " + ex.Message); }
            }

            if (ver != loadVersion) return;    // 期间又刷新过一次，这轮作废

            // ★★ 先把素材全部加载完，再发布列表。
            //
            //   旧流程是"先发布空列表 → 卡片以亚克力+默认图标出现 → 再逐张补封面"，
            //   观感上确实是"卡片先出现、封面随后长出来"，但代价是**启动后 1 秒内
            //   每张卡都有一个"失去封面"的中间态**。素材是逐张加载的，用户每次
            //   启动都会随机看到某一张卡卡在默认图标上（实测："这次只是换了个游戏"）。
            //
            //   本地素材（图标 + Steam 缓存封面）总共 1 秒左右，直接一次性加载完
            //   再发布，卡片一出现就是最终样子；网络下载的封面仍然异步补（不阻塞）。
            foreach (TileItem it in list)
            {
                if (ver != loadVersion) return;
                try
                {
                    if (it.icon == null)
                    {
                        // ★ 必须克隆：ShellIcons 内部有静态缓存，两个快捷方式指向同一个
                        //   目标（游戏 + 它的启动器）时会拿到**同一个 Bitmap 实例**。
                        //   之后后台线程对它做主色分析、UI 线程同时在画它 ——
                        //   GDI+ 先抛「对象当前正在其他地方使用」，多来几次直接硬崩溃
                        //   （进程无日志消失，就是这一轮"墙整个不见了"的元凶）。
                        Bitmap shared = ShellIcons.Get(it.iconSource, 256);
                        it.icon = shared == null ? null : new Bitmap(shared);
                        it.tint = ColorUtils.Dominant(it.icon, Color.FromArgb(58, 68, 96));
                    }
                    if (it.art == null)
                    {
                        string art = ResolveLocalArt(it);
                        if (art != null)
                        {
                            it.artPath = art;
                            it.art = LoadArtCopy(art);
                            if (it.art == null) it.artPath = null;
                        }
                        if (Environment.GetEnvironmentVariable("TILEDESK_DEBUG_ART") == "1")
                            Config.Log("素材加载 [" + it.name + "] resolved=" + (art ?? "null") +
                                       " loaded=" + (it.art != null ? "有" : "无") +
                                       " steam=" + (it.steamId ?? "null") +
                                       " kind=" + it.kind);
                    }
                    else if (Environment.GetEnvironmentVariable("TILEDESK_DEBUG_ART") == "1")
                    {
                        Config.Log("素材复用 [" + it.name + "] art=有 path=" + (it.artPath ?? "null"));
                    }
                }
                catch (Exception ex) { Config.Log("加载图标失败 " + it.path + " : " + ex.Message); }
            }

            if (ver != loadVersion) return;

            // ---- 素材就绪，发布列表 ----
            lock (dataLock) { allItems = list; }
            if (old != null)
            {
                // ★ 不能在这里直接 DropCache：后台线程释放位图时，UI 线程可能正在
                //   DrawImage 同一张图 —— GDI+ 会抛「对象当前正在其他地方使用」
                //   或者直接访问已释放内存。统一交给 UI 线程处理。
                lock (disposeQueue) { foreach (TileItem o in old) if (o != null && !list.Contains(o)) disposeQueue.Add(o); }
            }
            reloadPending = true;
            artPending = true;   // 让 UI 线程立刻烤卡（素材已就绪）

            int noArt = 0, noIcon = 0;
            foreach (TileItem it in list)
            {
                if (it.art == null) noArt++;
                if (it.icon == null) noIcon++;
            }
            Config.Log("卡片素材: 共 " + list.Count + " 张，无封面 " + noArt + "，无图标 " + noIcon);

            if (cfg.downloadSteamArt)
            {
                List<TileItem> need = new List<TileItem>();
                foreach (TileItem it in list) if (it.steamId != null && it.art == null) need.Add(it);

                // ★ 不能用 artSlots.WaitOne() 在**调度**循环里限流：那会把加载线程
                //   本身堵住（第 4 个就开始等下载完），后面的收尾日志、缩略图统计
                //   全都推迟到十几秒之后。改成固定几个工作线程从同一个队列里取任务。
                int next = 0;
                int workers = Math.Min(3, need.Count);
                for (int w = 0; w < workers; w++)
                {
                    ThreadPool.QueueUserWorkItem(delegate(object o)
                    {
                        while (true)
                        {
                            TileItem captured = null;
                            lock (need)
                            {
                                if (next >= need.Count) return;
                                captured = need[next++];
                            }
                            try { FetchArt(captured); }
                            catch (Exception ex) { Config.Log("补齐封面失败 " + captured.name + " : " + ex.Message); }
                        }
                    });
                }
            }
        }

        /// <summary>给一张卡补 Steam 封面；拿不到竖版就用横版 header 裁剪成竖版做封面。</summary>
        private void FetchArt(TileItem captured)
        {
            string file = SteamArt.CachedPortrait(captured.steamId);
            if (file == null) file = SteamArt.DownloadPortrait(captured.steamId);
            if (file != null)
            {
                Bitmap b = LoadArtCopy(file);
                if (b != null) QueueArt(captured, file, b, null);
                return;
            }

            string hdr = SteamArt.DownloadHeader(captured.steamId);
            Bitmap wash = null;
            Bitmap portraitFromHeader = null;
            if (hdr != null)
            {
                wash = LoadHeaderWash(hdr);
                // ★ 很多游戏（Demo/工具/某些地区独占）没有官方竖版封面，
                //   但 header.jpg 总是有的。把它裁剪成竖版比例（2:3，取中央竖条）
                //   作为封面，比"只有模糊色底+图标"好得多。
                portraitFromHeader = CropHeaderToPortrait(hdr);
                if (portraitFromHeader != null)
                {
                    QueueArt(captured, hdr, portraitFromHeader, wash);
                    Config.Log("header 裁竖版封面: " + captured.name + " (appid " + captured.steamId + ")");
                    return;
                }
            }
            QueueArt(captured, null, null, wash);
            Config.Log("无竖版封面，使用 header 色底: " + captured.name +
                       " (appid " + captured.steamId + ") " + (wash != null ? "OK" : "也没有"));
        }

        private static string KindOf(string p)
        {
            if (Directory.Exists(p)) return "folder";
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext == ".lnk") return "lnk";
            if (ext == ".url") return "url";
            if (ext == ".exe") return "exe";
            return "file";
        }

        private static string ResolveLocalArt(TileItem it)
        {
            string custom = FindCustomCover(it);
            if (custom != null) return custom;
            if (it.steamId != null)
            {
                string p = SteamArt.LocalPortrait(it.steamId);
                if (p != null) return p;
                return SteamArt.CachedPortrait(it.steamId);
            }
            return SteamArt.CustomCover(it.path, it.name);
        }

        private static string ArtKey(TileItem it)
        {
            if (!string.IsNullOrEmpty(it.steamId)) return "app_" + it.steamId;
            string n = it.name ?? "item";
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in n) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return "name_" + sb.ToString();
        }

        private static readonly string[] CoverExts = new string[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

        private static string FindCustomCover(TileItem it)
        {
            try
            {
                string key = ArtKey(it);
                foreach (string ext in CoverExts)
                {
                    string p = Path.Combine(Config.CustomCoverDir, key + ext);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        private static void DeleteCustomCoverFiles(TileItem it)
        {
            try
            {
                string key = ArtKey(it);
                foreach (string ext in CoverExts)
                {
                    string p = Path.Combine(Config.CustomCoverDir, key + ext);
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }

        private static Bitmap LoadBitmapCopy(string path)
        {
            lock (Gdi.Lock) { return LoadBitmapCopyRaw(path); }
        }

        private static Bitmap LoadBitmapCopyRaw(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (Image img = Image.FromStream(fs))
                {
                    Bitmap b = new Bitmap(img);
                    b.SetResolution(96, 96);
                    return b;
                }
            }
            catch (Exception ex) { Config.Log("读取图片失败 " + path + " : " + ex.Message); return null; }
        }

        private static Bitmap LoadHeaderWash(string path)
        {
            lock (Gdi.Lock) { return LoadHeaderWashRaw(path); }
        }

        private static Bitmap LoadHeaderWashRaw(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (Image img = Image.FromStream(fs))
                {
                    int w = 48;
                    int h = Math.Max(8, (int)Math.Round(w * (double)img.Height / img.Width));
                    Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, w, h));
                    }
                    ImageFx.BoxBlur(small, 6);
                    return small;
                }
            }
            catch (Exception ex) { Config.Log("生成 header 色底失败 " + path + " : " + ex.Message); return null; }
        }

        /// <summary>
        /// 把横版 header 裁剪成竖版比例（2:3）当封面用。
        /// 策略：按高度定宽（width = height * 2/3），从中央取竖条——
        /// 游戏 header 的视觉中心通常在中轴附近。
        /// </summary>
        private static Bitmap CropHeaderToPortrait(string path)
        {
            lock (Gdi.Lock) { return CropHeaderToPortraitRaw(path); }
        }

        private static Bitmap CropHeaderToPortraitRaw(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (Image img = Image.FromStream(fs))
                {
                    int srcW = img.Width, srcH = img.Height;
                    if (srcW <= 0 || srcH <= 0) return null;
                    // 目标比例 2:3（宽:高）
                    int dstW = srcH * 2 / 3;
                    int dstH = srcH;
                    if (dstW > srcW)
                    {
                        // 原图比目标还"方"（极少见），按宽度定高
                        dstW = srcW;
                        dstH = srcW * 3 / 2;
                        if (dstH > srcH) dstH = srcH;
                    }
                    int x = (srcW - dstW) / 2;
                    int y = (srcH - dstH) / 2;
                    Bitmap dst = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.Clear(Color.Transparent);
                        g.DrawImage(img, new Rectangle(0, 0, dstW, dstH),
                                    new Rectangle(x, y, dstW, dstH), GraphicsUnit.Pixel);
                    }
                    return dst;
                }
            }
            catch (Exception ex) { Config.Log("裁剪 header 失败 " + path + " : " + ex.Message); return null; }
        }

        private Bitmap LoadArtCopy(string path)
        {
            lock (Gdi.Lock) { return LoadArtCopyRaw(path); }
        }

        private Bitmap LoadArtCopyRaw(string path)
        {
            Bitmap full = LoadBitmapCopyRaw(path);
            if (full == null) return null;
            try
            {
                // 按当前卡片尺寸存一份够用的：2x 封面是 1200x1800，
                // 26 张原图留在内存里要 200MB+。
                int wantW = Math.Max(160, (int)(cardW * 1.35));
                if (full.Width <= wantW) return full;
                int wantH = Math.Max(160, (int)Math.Round(full.Height * (double)wantW / full.Width));
                Bitmap small = new Bitmap(wantW, wantH, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(full, new Rectangle(0, 0, wantW, wantH));
                }
                full.Dispose();
                return small;
            }
            catch { return full; }
        }

        private void ApplyFilter()
        {
            List<TileItem> src;
            lock (dataLock) { src = new List<TileItem>(allItems); }

            string mode = cfg.sortMode == null ? "Name" : cfg.sortMode;
            Comparison<TileItem> cmp;
            if (string.Equals(mode, "Date", StringComparison.OrdinalIgnoreCase))
                cmp = delegate(TileItem a, TileItem b) { return b.stamp.CompareTo(a.stamp); };
            else if (string.Equals(mode, "Kind", StringComparison.OrdinalIgnoreCase))
                cmp = delegate(TileItem a, TileItem b)
                {
                    int k = string.Compare(a.kind, b.kind, StringComparison.OrdinalIgnoreCase);
                    return k != 0 ? k : string.Compare(a.name, b.name, StringComparison.CurrentCulture);
                };
            else
                cmp = delegate(TileItem a, TileItem b)
                {
                    return string.Compare(a.name, b.name, StringComparison.CurrentCulture);
                };

            src.Sort(cmp);
            if (cfg.sortDescending) src.Reverse();

            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) && cfg.order.Count > 0)
            {
                Dictionary<string, int> rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < cfg.order.Count; i++) rank[cfg.order[i]] = i;
                src.Sort(delegate(TileItem a, TileItem b)
                {
                    int ra, rb;
                    bool ha = rank.TryGetValue(Path.GetFileName(a.path), out ra);
                    bool hb = rank.TryGetValue(Path.GetFileName(b.path), out rb);
                    if (ha && hb) return ra.CompareTo(rb);
                    if (ha) return -1;
                    if (hb) return 1;
                    return string.Compare(a.name, b.name, StringComparison.CurrentCulture);
                });
                List<string> alive = new List<string>();
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TileItem it in src) names.Add(Path.GetFileName(it.path));
                foreach (string o in cfg.order) if (names.Contains(o)) alive.Add(o);
                if (alive.Count != cfg.order.Count) { cfg.order = alive; ScheduleSave(); }
            }

            lock (dataLock) { view = src; }
            Relayout();
            InvalidateAllCards();
            MarkDirtyAll();
            Wake(400);

            if (Config.Verbose)
            {
                int n = Math.Min(6, src.Count);
                for (int i = 0; i < n; i++)
                {
                    Rectangle r = (i < cellCol.Length) ? CellRect(cellCol[i], cellRow[i], 0) : Rectangle.Empty;
                    Config.Log("卡片 " + i + " [" + src[i].name + "] 格=(" +
                               (i < cellCol.Length ? cellCol[i] : -9) + "," +
                               (i < cellRow.Length ? cellRow[i] : -9) + ") 位置=" + r);
                }
                Config.Log("网格: left=" + gridLeft + " top=" + gridTop + " cols=" + cols +
                           " card=" + cardW + "x" + cardH + " gap=" + gapPx + " 共 " + src.Count + " 张");
            }
        }

        // ================================================================
        //  素材异步套用
        // ================================================================

        private class ArtUpdate
        {
            public TileItem item;
            public string path;
            public Bitmap art;
            public Bitmap header;
        }

        private readonly List<ArtUpdate> pendingArt = new List<ArtUpdate>();
        private readonly object pendingLock = new object();

        private void QueueArt(TileItem item, string path, Bitmap art, Bitmap header)
        {
            if (art == null && header == null && path == null) return;
            ArtUpdate u = new ArtUpdate();
            u.item = item;
            u.path = path;
            u.art = art;
            u.header = header;
            lock (pendingLock) pendingArt.Add(u);
            artPending = true;
        }

        private void ApplyPendingArt()
        {
            List<ArtUpdate> batch;
            lock (pendingLock)
            {
                if (pendingArt.Count == 0) return;
                batch = new List<ArtUpdate>(pendingArt);
                pendingArt.Clear();
            }
            foreach (ArtUpdate u in batch)
            {
                if (u.art != null)
                {
                    // 换封面时把旧的那张释放掉，否则每次启动都会多留几十兆
                    if (u.item.art != null && !object.ReferenceEquals(u.item.art, u.art))
                    {
                        try { u.item.art.Dispose(); } catch { }
                    }
                    u.item.artPath = u.path;
                    u.item.art = u.art;
                }
                if (u.header != null) u.item.header = u.header;
                // ★ 立刻丢掉悬停缓存：封面刚变，悬停卡若还是旧的那张，
                //   鼠标移上去就会看到"和常态不一致"的画面。
                if (u.item.cacheHover != null)
                {
                    try { u.item.cacheHover.Dispose(); } catch { }
                    u.item.cacheHover = null;
                }
                u.item.cacheEpoch = -1;
            }
            MarkDirtyAll();
            Wake(300);
        }

        // ================================================================
        //  启动 / 打开 / 删除
        // ================================================================

        private void Launch(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }

            try
            {
                if (File.Exists(it.path) || Directory.Exists(it.path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + it.path + "\"")
                    {
                        UseShellExecute = true
                    });
                    it.launchAnim = 1f;
                    MarkDirtyAll();
                    Wake(600);
                    Config.Log("启动（经 shell）: " + it.name);
                    return;
                }
            }
            catch (Exception ex) { Config.Log("经 shell 启动失败 " + it.name + " : " + ex.Message); }

            int err;
            try { err = Native.ShellExecuteOpen(Handle, it.path); }
            catch (Exception ex)
            {
                err = -1;
                Config.Log("启动异常 " + it.path + " : " + ex.Message);
            }

            if (err == 0)
            {
                it.launchAnim = 1f;
                MarkDirtyAll();
                Wake(600);
                Config.Log("启动: " + it.name);
                return;
            }

            string msg;
            if (err == 1223) msg = "你在权限提示（UAC）上点了「否」。";
            else if (err == 5) msg = "系统拒绝了访问（可能需要管理员权限）。";
            else if (err == 2 || err == 3) msg = "找不到目标文件，快捷方式可能已经失效。";
            else msg = new System.ComponentModel.Win32Exception(err).Message;

            Config.Log("启动失败 " + it.name + " : err=" + err + " " + msg);
            MessageBox.Show("打不开「" + it.name + "」。\n\n" + it.path + "\n\n系统返回：" + err + "\n" + msg,
                "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void DeleteToRecycleBin(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            if (!Native.SendToRecycleBin(Handle, it.path))
            {
                MessageBox.Show("删除失败，可以手动在桌面删掉它。", "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Config.Log("已放入回收站: " + it.path);
            selIndex = -1;
            RefreshItems();
        }

        private void RenameItem(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            if (it == null || string.IsNullOrEmpty(it.path)) return;

            string ext = "";
            try { ext = Path.GetExtension(it.path); } catch { }
            string oldName = it.name;
            string input = InputDialog.Ask(this, "重命名", "新名字（不含扩展名 " + ext + "）", oldName);
            if (input == null) return;
            input = input.Trim();
            if (input.Length == 0 || input == oldName) return;
            if (input.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("名字里不能包含 \\ / : * ? \" < > | 这些字符。", "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string dir = Path.GetDirectoryName(it.path);
            string newPath = Path.Combine(dir, input + ext);
            if (File.Exists(newPath))
            {
                MessageBox.Show("已经有同名的文件了。", "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try { File.Move(it.path, newPath); }
            catch (Exception ex)
            {
                Config.Log("重命名失败: " + ex.Message);
                MessageBox.Show("重命名失败：" + ex.Message, "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                string oldKey = Path.GetFileName(it.path);
                string newKey = Path.GetFileName(newPath);
                if (cfg.positions != null)
                {
                    for (int i = 0; i < cfg.positions.Count; i++)
                    {
                        string p = cfg.positions[i];
                        if (string.IsNullOrEmpty(p)) continue;
                        int bar = p.IndexOf('|');
                        string key = bar > 0 ? p.Substring(0, bar) : p;
                        if (string.Equals(key, oldKey, StringComparison.OrdinalIgnoreCase))
                        {
                            cfg.positions[i] = newKey + p.Substring(bar);
                            break;
                        }
                    }
                }
                cfg.Save();
            }
            catch (Exception ex) { Config.Log("重命名后同步位置失败: " + ex.Message); }

            Config.Log("重命名: " + oldName + " -> " + input);
            selIndex = -1;
            RefreshItems();
        }

        private void DeletePermanently(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            if (it == null || string.IsNullOrEmpty(it.path)) return;

            if (MessageBox.Show("永久删除？", "TileDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            try
            {
                bool isDir = Directory.Exists(it.path);
                if (isDir) Directory.Delete(it.path, true);
                else File.Delete(it.path);
                Config.Log("永久删除: " + it.path);
            }
            catch (Exception ex)
            {
                Config.Log("永久删除失败: " + ex.Message);
                MessageBox.Show("删除失败：" + ex.Message, "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            selIndex = -1;
            RefreshItems();
        }

        private void SetCoverByAppId(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }

            string hint = string.IsNullOrEmpty(it.steamId)
                ? "（这张卡没有识别出 appid）"
                : "（当前识别到的 appid：" + it.steamId + "）";
            string input = InputDialog.Ask(this, "用 Steam appid 设置封面",
                "输入 Steam 游戏 appid（纯数字，例如 730、413150）：\n" + hint,
                it.steamId ?? "");
            if (input == null) return;

            string id = input.Trim();
            bool digits = id.Length >= 2 && id.Length <= 9;
            if (digits)
                foreach (char c in id) if (!char.IsDigit(c)) { digits = false; break; }
            if (!digits)
            {
                MessageBox.Show("appid 应该是 2~9 位纯数字。\n可以在 Steam 商店页面地址里找到，例如\n" +
                                "store.steampowered.com/app/413150/... 里的 413150。",
                                "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Cursor = Cursors.WaitCursor;
            try
            {
                string file = SteamArt.CachedPortrait(id);
                if (file == null) file = SteamArt.DownloadPortrait(id);
                if (file == null)
                {
                    Cursor = Cursors.Default;
                    MessageBox.Show("拿不到 appid " + id + " 的竖版封面。\n" +
                                    "可能这个 appid 没有 library_600x900 素材（未发售 / Demo 常见）。",
                                    "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                DeleteCustomCoverFiles(it);
                string ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                string dest = Path.Combine(Config.CustomCoverDir, ArtKey(it) + ext);
                File.Copy(file, dest, true);
                it.artPath = dest;
                it.art = LoadArtCopy(dest);
                it.cacheEpoch = -1;
                MarkDirtyAll();
                Wake(300);
                Config.Log("按 appid 设置封面: " + it.name + " <- appid " + id);
            }
            catch (Exception ex)
            {
                Config.Log("按 appid 设置封面失败: " + ex.Message);
                MessageBox.Show("设置封面失败：\n" + ex.Message, "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void SetCustomCover(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "给「" + it.name + "」选一张封面（建议竖版 2:3）";
                dlg.Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    DeleteCustomCoverFiles(it);
                    string dest = Path.Combine(Config.CustomCoverDir,
                        ArtKey(it) + Path.GetExtension(dlg.FileName).ToLowerInvariant());
                    File.Copy(dlg.FileName, dest, true);
                    it.artPath = dest;
                    it.art = LoadArtCopy(dest);
                    it.cacheEpoch = -1;
                    MarkDirtyAll();
                    Wake(300);
                    Config.Log("自定义封面: " + it.name + " -> " + dest);
                }
                catch (Exception ex)
                {
                    Config.Log("设置自定义封面失败: " + ex.Message);
                    MessageBox.Show("设置封面失败：\n" + ex.Message, "TileDesk",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ================================================================
        //  桌面目录监视
        // ================================================================

        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        private void SetupWatchers()
        {
            DisposeWatchers();
            if (!cfg.watchDesktop) return;

            List<string> dirs = new List<string>();
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            foreach (string extra in cfg.extraFolders) dirs.Add(extra);

            foreach (string d in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    FileSystemWatcher w = new FileSystemWatcher(d);
                    w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                    w.IncludeSubdirectories = false;
                    w.Created += OnDesktopFsChanged;
                    w.Deleted += OnDesktopFsChanged;
                    w.Renamed += OnDesktopFsRenamed;
                    w.Changed += OnDesktopFsChanged;
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch (Exception ex) { Config.Log("监视目录失败 " + d + " : " + ex.Message); }
            }
            Config.Log("已监视 " + watchers.Count + " 个桌面目录");
        }

        private void OnDesktopFsChanged(object sender, FileSystemEventArgs e)
        {
            string n = e.Name ?? "";
            string ext = Path.GetExtension(n).ToLowerInvariant();
            // ★ 只关心快捷方式的变化。桌面上的截图、下载、壁纸文件一变动就
            //   重新扫描 35 张卡的图标/封面毫无意义，反而会让卡片反复闪。
            //   需要识别新放的封面图时，右键「刷新磁贴」即可。
            if (ext != ".lnk" && ext != ".url") return;
            watchDueMs = Clock.NowMs + 1200;
        }

        private void OnDesktopFsRenamed(object sender, RenamedEventArgs e) { OnDesktopFsChanged(sender, e); }

        private void DisposeWatchers()
        {
            foreach (FileSystemWatcher w in watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            watchers.Clear();
        }

        // ================================================================
        //  设置界面
        // ================================================================

        public void OpenSettings()
        {
            try
            {
                if (settingsForm != null && !settingsForm.IsDisposed)
                {
                    if (settingsForm.WindowState == FormWindowState.Minimized)
                        settingsForm.WindowState = FormWindowState.Normal;
                    settingsForm.Activate();
                    return;
                }
                SettingsForm f = new SettingsForm(cfg, this);
                settingsForm = f;
                // 预览模式（--windowed）下磁贴墙自己是 TOPMOST，设置窗口不置顶就会被它压住。
                if (windowed) f.TopMost = true;
                f.FormClosed += delegate { if (settingsForm == f) settingsForm = null; };
                f.Show();
                f.Activate();
            }
            catch (Exception ex)
            {
                Config.Log("打开设置界面失败: " + ex.Message);
                MessageBox.Show("打开设置界面失败：\n" + ex.Message, "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public NowPlayingMonitor NowPlaying { get { return nowPlaying; } }

        public void ApplySettings()
        {
            // ★ 强调色 / 投影色必须**每次都从 cfg 重读**。
            //   原来只在构造函数里读一次，于是设置里把强调色从蓝改成红之后，
            //   设置界面自己变了，磁贴墙上的悬停描边、右键选中框、拖拽落点虚线
            //   还全是那一个写死的蓝色 —— 用户看到的就是"框选颜色不跟随主题色"。
            accent = ColorUtils.FromHex(cfg.accentColor, Color.FromArgb(76, 154, 255));
            shadowCol = ColorUtils.FromHex(cfg.shadowColor, Color.Black);

            acrylicDirty = true;
            SetupWatchers();
            RecomputeMetrics();     // 内部会 InvalidateAllCards（连阴影银行一起重做）
            EnsureAcrylic(true);
            ApplyFilter();
            cfg.Save();
            MarkDirtyAll();
            Wake(500);
        }

        // ================================================================
        //  窗口层级
        // ================================================================

        public void ApplyWindowMode(string mode)
        {
            if (windowed) return;
            if (!string.Equals(mode, "Desktop", StringComparison.OrdinalIgnoreCase)) mode = "Bottom";
            cfg.windowMode = mode;

            Unpin();

            if (string.Equals(mode, "Desktop", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr parent = DesktopHost.FindDesktopHost();
                if (parent != IntPtr.Zero && PinTo(parent))
                {
                    desktopParent = parent;
                    stickBottom = false;
                    Config.Log("已挂载到桌面层 0x" + parent.ToInt64().ToString("X"));
                }
                else
                {
                    Config.Log("挂载桌面层失败（系统拒绝 SetParent），改用桌面底层模式");
                    cfg.windowMode = "Bottom";
                }
            }

            if (string.Equals(cfg.windowMode, "Bottom", StringComparison.OrdinalIgnoreCase))
            {
                stickBottom = true;
                FitToWorkingArea();
                PlaceAboveDesktop();
            }
            MarkDirtyAll();
            Wake(300);
        }

        private void FitToWorkingArea()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (Bounds != wa) Bounds = wa;
        }

        private void PlaceAboveDesktop()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return;

            uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW;
            IntPtr bottom = new IntPtr(1);

            IntPtr np = IntPtr.Zero, di = IntPtr.Zero;
            try { if (npWindow != null && !npWindow.IsDisposed && npWindow.IsHandleCreated) np = npWindow.Handle; }
            catch { }
            try { if (deskInfo != null && !deskInfo.IsDisposed && deskInfo.IsHandleCreated) di = deskInfo.Handle; }
            catch { }

            Native.SetWindowPos(Handle, bottom, 0, 0, 0, 0, flags);
            if (np != IntPtr.Zero) Native.SetWindowPos(np, bottom, 0, 0, 0, 0, flags);
            if (di != IntPtr.Zero) Native.SetWindowPos(di, bottom, 0, 0, 0, 0, flags);

            if (np != IntPtr.Zero) Native.SetWindowPos(Handle, np, 0, 0, 0, 0, flags);
            if (di != IntPtr.Zero) Native.SetWindowPos(Handle, di, 0, 0, 0, 0, flags);

            Native.SetWindowPos(progman, Handle, 0, 0, 0, 0, flags);
        }

        private void KeepAboveDesktop()
        {
            if (windowed || !stickBottom) return;
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return;

            IntPtr h = Native.GetWindow(Handle, Native.GW_HWNDNEXT);
            for (int i = 0; i < 60 && h != IntPtr.Zero; i++)
            {
                if (h == progman) return;
                if (Native.IsWindowVisible(h)) break;
                h = Native.GetWindow(h, Native.GW_HWNDNEXT);
            }
            PlaceAboveDesktop();
        }

        private bool PinTo(IntPtr parent)
        {
            try
            {
                int style0 = Native.GetWindowLong(Handle, Native.GWL_STYLE);
                Native.SetParent(Handle, parent);
                int err = Marshal.GetLastWin32Error();
                if (Native.GetParent(Handle) != parent)
                {
                    Config.Log("SetParent 到 0x" + parent.ToInt64().ToString("X") + " 被拒绝, err=" + err);
                    return false;
                }
                Native.SetWindowLong(Handle, Native.GWL_STYLE, (style0 & ~Native.WS_POPUP) | Native.WS_CHILD);

                RECT pr;
                if (!Native.GetWindowRect(parent, out pr)) return false;
                int w = pr.Right - pr.Left, h = pr.Bottom - pr.Top;
                if (w < 200 || h < 200) { Unpin(); return false; }
                Bounds = new Rectangle(pr.Left, pr.Top, w, h);
                return true;
            }
            catch (Exception ex) { Config.Log("PinTo 异常: " + ex.Message); return false; }
        }

        private void Unpin()
        {
            if (desktopParent != IntPtr.Zero)
            {
                Native.SetParent(Handle, IntPtr.Zero);
                int style = Native.GetWindowLong(Handle, Native.GWL_STYLE);
                Native.SetWindowLong(Handle, Native.GWL_STYLE, (style & ~Native.WS_CHILD) | Native.WS_POPUP);
                desktopParent = IntPtr.Zero;
            }
        }

        private void EnsurePinned()
        {
            if (windowed) return;
            if (!string.Equals(cfg.windowMode, "Desktop", StringComparison.OrdinalIgnoreCase)) return;
            if (desktopParent == IntPtr.Zero) return;
            if (Native.GetParent(Handle) == desktopParent) return;
            if (pinRetries >= 8) return;
            pinRetries++;
            if (!PinTo(desktopParent))
            {
                cfg.windowMode = "Bottom";
                desktopParent = IntPtr.Zero;
                stickBottom = true;
                FitToWorkingArea();
                PlaceAboveDesktop();
            }
            MarkDirtyAll();
        }

        private void SwitchMode(string mode)
        {
            ApplyWindowMode(mode);
            cfg.Save();
        }

        // ================================================================
        //  窗口消息
        // ================================================================

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)FrameClock.WM_APP_FRAME)
            {
                OnFrame();
                return;
            }

            if (m.Msg == Native.WM_SETTINGCHANGE)
            {
                try
                {
                    string what = m.LParam != IntPtr.Zero ? Marshal.PtrToStringUni(m.LParam) : "";
                    if (string.IsNullOrEmpty(what) ||
                        what.IndexOf("Wallpaper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        what.IndexOf("Desktop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        what.IndexOf("ImmersiveColorSet", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        acrylicDirty = true;
                        EnsureAcrylic(false);
                        MarkDirtyAll();
                        Wake(300);
                    }
                }
                catch { }
            }
            if (m.Msg == WM_THEMECHANGED || m.Msg == WM_DWMCOMPOSITIONCHANGED || m.Msg == WM_DISPLAYCHANGE)
            {
                acrylicDirty = true;
                try { EnsureAcrylic(false); MarkDirtyAll(); Wake(300); } catch { }
            }

            if (fadeLevel < FadeDead &&
                (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONUP || m.Msg == WM_LBUTTONDBLCLK ||
                 m.Msg == WM_RBUTTONUP || m.Msg == WM_MOUSEWHEEL))
                return;

            if (m.Msg == WM_MOUSEMOVE)
            {
                bool leftDown = (m.WParam.ToInt32() & 0x0001) != 0;
                HandleMouseMove(MouseClient(), leftDown);
                return;
            }
            if (m.Msg == WM_MOUSELEAVE) { ClearHover(); return; }

            if (m.Msg == WM_CAPTURECHANGED && dragging)
            {
                if (Native.KeyDown(Native.VK_LBUTTON)) { try { Native.SetCapture(Handle); } catch { } }
                else { Config.Log("拖拽兜底: 捕获被抢且左键已松开 → 落位"); EndDrag(true); return; }
            }

            if (m.Msg == WM_LBUTTONDOWN)
            {
                Point pt = MouseClient();
                pressCandidate = false; pressIndex = -1;

                if (cfg.showControlButton && !ctrlRect.IsEmpty && ctrlRect.Contains(pt))
                {
                    ShowControlMenu(pt);
                    return;
                }
                if (cfg.showControlButton && !trashRect.IsEmpty && trashRect.Contains(pt))
                {
                    OpenRecycleBin();
                    return;
                }
                if (cfg.edgeSwipeNotify && pt.X >= ClientSize.Width - EdgeZone)
                {
                    edgeArmed = true; pressAt = pt; pressCandidate = false; pressIndex = -1;
                    Native.SetCapture(Handle);
                    return;
                }

                int i = IndexAt(pt);
                if (i >= 0) { pressCandidate = true; pressAt = pt; pressIndex = i; pressStamp = Clock.NowMs; }
                return;
            }

            if (m.Msg == WM_LBUTTONUP)
            {
                if (dragging) { EndDrag(true); return; }
                if (edgeArmed) { edgeArmed = false; try { Native.ReleaseCapture(); } catch { } return; }

                if (pressCandidate && pressIndex >= 0)
                {
                    Point up = MouseClient();
                    int ddx = Math.Abs(up.X - pressAt.X);
                    int ddy = Math.Abs(up.Y - pressAt.Y);
                    bool quick = Clock.Passed(pressStamp, 0) && (Clock.NowMs - pressStamp) <= 900;
                    if (quick && ddx <= TapSlop && ddy <= TapSlop && IndexAt(up) == pressIndex)
                    {
                        StartRipple(pressIndex, up);
                        Launch(pressIndex);
                    }
                }
                pressCandidate = false; pressIndex = -1;
                return;
            }

            if (m.Msg == WM_LBUTTONDBLCLK)
            {
                if (dragging)
                {
                    dragging = false; dragIndex = -1; dropIndex = -1; dragPreview = null;
                    ResetDragAnim();
                    try { Native.ReleaseCapture(); } catch { }
                    Cursor = Cursors.Default;
                }
                pressCandidate = false; pressIndex = -1;
                return;
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                Point pt = MouseClient();
                if (cfg.showControlButton && !ctrlRect.IsEmpty && ctrlRect.Contains(pt)) { ShowControlMenu(pt); return; }
                if (cfg.showControlButton && !trashRect.IsEmpty && trashRect.Contains(pt)) { ShowTrashMenu(pt); return; }
                int i = IndexAt(pt);
                if (i >= 0) { selIndex = i; MarkDirtyAll(); ShowItemMenu(i, pt); }
                return;
            }

            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (Native.CtrlPressed) { ZoomCards(delta > 0 ? 1 : -1); return; }
                scrollTarget = ClampF(scrollTarget - delta * 0.55f * scale, 0, maxScroll);
                MarkDirtyAll();
                Wake(400);
                return;
            }

            if (m.Msg == Native.WM_WINDOWPOSCHANGING && stickBottom && !windowed)
            {
                Native.WINDOWPOS wp = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
                if ((wp.flags & Native.SWP_NOZORDER) == 0 && wp.hwndInsertAfter == IntPtr.Zero)
                {
                    IntPtr progman = Native.FindWindow("Progman", null);
                    if (progman != IntPtr.Zero) wp.hwndInsertAfter = progman;
                    else wp.flags |= Native.SWP_NOZORDER;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
            }
            else if (m.Msg == Native.WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == Native.SC_MINIMIZE)
            {
                return;
            }
            else if (m.Msg == Native.WM_MOUSEACTIVATE && !windowed)
            {
                m.Result = new IntPtr(Native.MA_NOACTIVATE);
                return;
            }
            else if (m.Msg == Native.WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE)
            {
                acrylicDirty = true;
                if (!windowed && stickBottom)
                {
                    Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                    if (Bounds != wa) { Bounds = wa; RecomputeMetrics(); }
                }
                InvalidateAllCards();
                MarkDirtyAll();
            }
            base.WndProc(ref m);
        }

        // ================================================================

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (logicTimer != null) { logicTimer.Stop(); logicTimer.Dispose(); logicTimer = null; }
                if (clock != null) { clock.Dispose(); clock = null; }
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                if (surface != null) { surface.Dispose(); surface = null; }
                if (trayIconHandle != IntPtr.Zero) { Native.DestroyIcon(trayIconHandle); trayIconHandle = IntPtr.Zero; }
                if (wallpaperBlur != null) { wallpaperBlur.Dispose(); wallpaperBlur = null; }
                if (previewBackdrop != null) { previewBackdrop.Dispose(); previewBackdrop = null; }
                if (dragLiveCard != null) { dragLiveCard.Dispose(); dragLiveCard = null; }
                shadows.Dispose();
                DisposeAllCardCaches();
                DisposeFonts();
                DisposeWatchers();
                if (npWindow != null) { npWindow.Dispose(); npWindow = null; }
                if (deskInfo != null) { deskInfo.Dispose(); deskInfo = null; }
                if (nowPlaying != null) { nowPlaying.Dispose(); nowPlaying = null; }
            }
            base.Dispose(disposing);
        }
    }
}
