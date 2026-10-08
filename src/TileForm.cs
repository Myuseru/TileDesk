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
    public partial class TileForm : Form
    {
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
        private int gridLeft = 0, gridTop = -1000, viewH = 0, gridTopPad = 0;   // gridTop 初值故意离谱，保证首次一定赋值

        // ---- 滚动 / 交互 ----
        private float scroll = 0f, scrollTarget = 0f, maxScroll = 0f;
        private int hoverIndex = -1;
        private int selIndex = -1;

        // ---- 分层窗口（逐像素透明）----
        private IntPtr dib = IntPtr.Zero;
        private IntPtr dibBits = IntPtr.Zero;
        private IntPtr memDc = IntPtr.Zero;
        private IntPtr oldObj = IntPtr.Zero;
        private Bitmap surface;
        private int surfW, surfH;

        // ---- 亚克力素材 ----
        private Bitmap wallpaperBlur;
        private int wpScreenW, wpScreenH;
        private volatile bool acrylicDirty = true;
        private Bitmap previewBackdrop;

        // ---- 卡片缓存：一次画好，之后每帧只贴图 ----
        // 缓存位图现在挂在 TileItem 身上（见 Core.cs）—— 按槽位存的话，重排之后
        // 数组里还是旧顺序的图，松手那一瞬间整面墙会用旧顺序重画一次（"闪回原位"）。
        private int cacheEpoch = 0;

        private Bitmap shadowBmp;
        private int shadowPadPx = 32;
        private int shadowForW, shadowForH, shadowForPad;
        private int tileW, tileH;

        private volatile bool dirty = true;
        private static readonly Semaphore artSlots = new Semaphore(3, 3);
        private bool stickBottom = true;
        private IntPtr desktopParent = IntPtr.Zero;
        private int pinRetries = 0, tickCount = 0, mouseLogs = 0;
        private int demoHover = -1;
        private bool firstRun = false;
        private int startTick = Environment.TickCount;
        private IntPtr trayIconHandle = IntPtr.Zero;

        // 磁贴墙右下角的常驻控制按钮（不依赖托盘，保证任何时候都能设置/退出）
        private Rectangle ctrlRect = Rectangle.Empty;
        private bool ctrlHover = false;
        private Rectangle trashRect = Rectangle.Empty;
        private bool trashHover = false;

        // 拖拽重排 / 拖到回收站
        private bool pressCandidate = false;
        private int pressTick = 0;         // 按下的时刻（用来判断"这次抬起"是不是同一次点击）
        /// <summary>触摸容差：按下到松开的位移在这个范围内就算"点中"而不是拖拽。</summary>
        private int TapSlop { get { return (int)Math.Round(14 * scale); } }


        /// <summary>边缘起手区宽度（从屏幕右边缘往内这么多像素内按下才算）。</summary>
        private int EdgeZone { get { return (int)Math.Round(6 * scale); } }

        /// <summary>往左拖够这个距离才唤起通知中心。</summary>
        private int SwipeDist { get { return (int)Math.Round(40 * scale); } }
        /// <summary>右边缘左滑唤起通知中心：按下时从右边缘起手就置位。</summary>

        // ---- 空闲淡出 ----
        // fadeLevel: 1 = 完全可见，0 = 完全隐藏。用分层的 SourceConstantAlpha 整体淡，
        // 不需要重画任何像素 —— 所以淡入淡出只走 Present，几毫秒。
        private float fadeLevel = 1f;
        private int fadeTarget = 1;
        private const float FadeSec = 0.45f;
        private bool needPresentOnly = false;
        /// <summary>低于这个值就认为"已隐藏"，此时不接受任何点击（防误触）。</summary>
        private const float FadeDead = 0.04f;

        /// <summary>
        /// 磁贴墙是否"隐藏"。注意**不是**把窗口 Visible 置 false ——
        /// 菜单画在这块画布上，窗口一隐藏菜单就出不来也没法点。
        /// 这里只是一个绘制开关：不画卡片和按钮，整块保持透明（鼠标穿透）。
        /// </summary>
        private bool wallHidden;

        private void SetWallHidden(bool h)
        {
            wallHidden = h;
            MarkDirtyAll();
            Wake();
            Config.Log(h ? "磁贴墙: 已隐藏（仅保留菜单）" : "磁贴墙: 已显示");
        }
        private bool edgeArmed = false;
        private Point pressAt = Point.Empty;
        private int pressIndex = -1;
        private bool dragging = false;
        private int dragIndex = -1;
        private int[] dragPreview;
        private Point dragPos = Point.Empty;
        private int dropIndex = -1;
        private int dropCol = -1, dropRow = -1;    // 自由排列时的落点格子

        // 自动保存防抖
        private int saveDueMs = 0;

        private int watchDueMs = 0;
        private int lastTickMs = Environment.TickCount;
        private Point lastMouse = Point.Empty;
        private bool snapHover = false;
        private bool spectrumOn;   // 本拍频谱是否在跑（影响 tick 频率）
        private int diagTickMs = 0;    // 上一拍的时刻（诊断卡顿用）
        private int lastUpX = int.MinValue, lastUpY = int.MinValue, lastUpW = 0, lastUpH = 0;   // 上次整窗上传的参数
        private int npRenderFrames = 0;
        private double npRenderMs = 0;
        private double npRenderDrawMs = 0;
        private double npRenderPresentMs = 0;
        private int npRenderWindowStart = 0;
        private int npFullFrames = 0;
        private int npPartialFrames = 0;
        private int npFadeFrames = 0;      // 淡入淡出（只换透明度、不重画）的帧数
        private double npFullMs = 0;        // 整窗帧的（绘制 + 上传）总耗时，用来单独看整窗成本

        /// <summary>立刻把计时器提到最高帧率，别让空闲时 120ms 的轮询拖慢交互响应。</summary>
        private void Wake()
        {
            // Interval = 8 已经是 WM_TIMER 能给到的最快节奏（每个系统 tick 一帧，≈62fps）
            if (tick != null && tick.Interval > 8) tick.Interval = 8;
        }

        private static string FirstRunMarker
        {
            get { return Path.Combine(Config.DataDir, ".first-run-done"); }
        }

        private Font fontCard;
        private Color accent = Color.FromArgb(76, 154, 255);

        private System.Windows.Forms.Timer tick;
        private NotifyIcon tray;
        private DeskInfoWindow deskInfo;   // 右下角常驻时钟/日期/天气（独立分层窗口，不跟墙淡出）

        // 「正在播放」监控器：只提供数据和上一首/下一首/播放暂停/跳转这些控制，
        // 不参与任何绘制 —— 控件长什么样、怎么摆，由 UI 那边决定。
        private NowPlayingMonitor nowPlaying;
        private NowPlayingWindow npWindow;


        // ---- 性能统计 ----
        private int frameCount = 0;
        private double frameMsTotal = 0;
        private bool fpsWindowOpen = false;

        private const int LabelLinesMax = 2;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_THEMECHANGED = 0x031A;
        private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

        public TileForm(Config config, bool asWindow)
        {
            cfg = config;
            windowed = asWindow;
            accent = ColorUtils.FromHex(cfg.accentColor, Color.FromArgb(76, 154, 255));

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

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 16;
            tick.Tick += OnTick;
            tick.Start();

            try { firstRun = !File.Exists(FirstRunMarker); }
            catch { firstRun = false; }

            string dh = Environment.GetEnvironmentVariable("TILEDESK_DEMO_HOVER");
            if (!string.IsNullOrEmpty(dh)) { int v; if (int.TryParse(dh, out v)) demoHover = v; }

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
                cp.ExStyle |= Native.WS_EX_LAYERED;          // 逐像素透明，让壁纸透出来
                if (!windowed)
                {
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                    cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
                }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RecomputeMetrics();
            StartLoad();
            SetupWatchers();

            nowPlaying = new NowPlayingMonitor();
            nowPlaying.Start();
            Config.Log("NowPlaying 监控已启动（后台线程会去连 SMTC）");

            // 控件用**自己的一块分层窗口** —— 这样它每动一下进度条都不会牵动
            // 磁贴墙那块 3840x2064 的画布。Windows 原生控件也是「各自窗口各自重绘」。
            //
            // 层级用 SetWindowPos 维持（见 RaiseNowPlayingAboveWall），
            // **绝不能设 npWindow.Owner = this**：那样控件成了墙的被拥有窗口，
            // 点它会先尝试激活拥有者，而墙是 WS_EX_NOACTIVATE 的最底层窗口，
            // 激活失败 → 鼠标点击被整个丢掉。实测：上一首/播放/下一首全部点不动、
            // 日志一条都没有；把 owner 去掉后立刻恢复（日志出现"正在播放控件：…"）。
            try
            {
                npWindow = new NowPlayingWindow(nowPlaying, cfg, scale);
            }
            catch (Exception ex) { Config.Log("创建正在播放窗口失败: " + ex.Message); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
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
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecomputeMetrics();
            MarkDirtyAll();
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
            shadowPadPx = Math.Max(4, (int)Math.Round(cfg.shadowSize * scale));
            tileW = cardW + shadowPadPx * 2;
            tileH = cardH + shadowPadPx * 2;

            if (changed || fontCard == null)
            {
                DisposeFonts();
                string fam = PickFontFamily(cfg.fontFamily);
                fontCard = new Font(fam, cardW * 0.098f, FontStyle.Bold, GraphicsUnit.Pixel);
                // 和系统菜单对齐：9pt @96dpi = 12px（SystemFonts.MenuFont 就是雅黑 9pt）
                shadowBmp = null;
            }

            Relayout();
            InvalidateCards();
            EnsureDeskInfo();
            Config.Log("UI: dpi=" + dpi + " scale=" + scale.ToString("0.##") +
                       " card=" + cardW + "x" + cardH + " tile=" + tileW + "x" + tileH +
                       " cols=" + cols + " client=" + ClientSize.Width + "x" + ClientSize.Height);
        }

        /// <summary>
        /// 右下角常驻信息条（时间/日期/天气）：按开关和 scale 创建或重建。
        /// 它是独立分层窗口 —— 墙空闲淡出时它**不跟着消失**（这是需求：常驻）。
        /// </summary>
        private void EnsureDeskInfo()
        {
            try
            {
                if (!cfg.showDeskInfo)
                {
                    if (deskInfo != null) { deskInfo.Dispose(); deskInfo = null; }
                    return;
                }
                int wantW = (int)Math.Round(360 * scale), wantH = (int)Math.Round(88 * scale);
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

        /// <summary>
        /// 信息条在客户区里的位置：垃圾桶按钮左边；**垂直方向与播放器进度条同一条水平线**
        /// （播放器没开或还没布局时，退回与右下角按钮底边对齐）。
        /// </summary>
        private Rectangle DeskInfoRect()
        {
            if (deskInfo == null || deskInfo.IsDisposed) return Rectangle.Empty;
            int gap = (int)Math.Round(12 * scale);
            int w = deskInfo.ClientSize.Width, h = deskInfo.ClientSize.Height;
            int x = trashRect.Left - gap - w;
            int y = ctrlRect.Bottom - h;   // ctrlRect 已含 bottomLift，信息条自动跟随
            if (cfg.showNowPlaying && npWindow != null && !npWindow.IsDisposed && npWindow.BarCenterY > 0)
            {
                Point np = PointToClient(npWindow.Location);
                // 中线对齐播放器进度条，再整体下移 24 逻辑像素 —— 让时间文字的**底部**
                // 和左边专辑封面的底部齐平（实测字底 2078、封面底 2126，差 48 物理像素）。
                y = np.Y + npWindow.BarCenterY - h / 2 + (int)Math.Round(24 * scale);
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
            if (availW < cardW) cols = 1;
            else cols = Math.Max(1, (availW + gapPx) / (cardW + gapPx));

            int gridW = cols * cardW + Math.Max(0, cols - 1) * gapPx;
            gridLeft = padPx + Math.Max(0, (availW - gridW) / 2);

            // gridTop / viewH 在下面算完"活的"上界留空之后赋值

            int cs = Math.Max(20, (int)Math.Round(34 * scale));
            int cm = Math.Max(6, padPx / 2);
            int bgap = Math.Max(4, (int)Math.Round(6 * scale));
            // 底部一横排控件整体上提（用户要求："这些控件总体上提一点"）：
            // 右下按钮、播放器、时钟天气条都用同一个 lift，保持一条基线
            int lift = Math.Max(0, (int)Math.Round(cfg.bottomLift * scale));
            ctrlRect = new Rectangle(ClientSize.Width - cm - cs, ClientSize.Height - cm - cs - lift, cs, cs);
            trashRect = new Rectangle(ctrlRect.X - bgap - cs, ctrlRect.Y, cs, cs);

            gridTopPad = 0;   // 顶部对齐：不再垂直居中，卡片整体往上提

            // 位置表（自动排列 = 顺序即位置；自由排列 = 各自记着的格子）
            AssignCells();

            // ---- 上界留空是"活的" ----
            // 右下角按钮 / 左下角播放器是浮在墙上面的。与其给它们单独挖禁区（那要按
            // 最高的那个控件留白，旁边跟着一起浪费），不如把**整块网格往上顶**：
            // 只要顶部还有余量，就把上界留空压到"内容底边刚好停在控件上方"为止 ——
            // 顶部省多少、底部就能多用多少，既不重叠也能多塞卡。
            int contentH = ContentHeight();
            int topPad = padPx;
            if (contentH > 0)
            {
                int obstacleTop = ClientSize.Height - ObstacleBandPx();
                int wantTop = obstacleTop - (int)Math.Round(12 * scale) - contentH;
                if (wantTop < topPad) topPad = Math.Max((int)Math.Round(8 * scale), wantTop);
            }
            // ★ 迟滞：gridTop 是**整墙的垂直原点**，它的微小变化会让所有卡片一起跳。
            //   而 contentH 是由"卡片占了几行"算出来的 —— 用户拖动一张卡到更下面一行
            //   就会让 contentH 变化，于是整墙上下抖一下（用户实测："重启后第一次拖动
            //   会抖一下"、"只有我交互了才抖"，日志里两轮对比 gridTop 16 → 26）。
            //   所以变化幅度小于 12px 时保持原值不动；只有真的需要重排才改。
            int newTop = Math.Max(0, topPad);
            int hyst = Math.Max(6, (int)Math.Round(6 * scale));
            if (Math.Abs(newTop - gridTop) >= hyst) gridTop = newTop;
            viewH = Math.Max(cardH, ClientSize.Height - gridTop - padPx);

            int baseScroll = Math.Max(0, contentH - viewH);
            // 内容真的超过一屏时，再多给一段滚动余量（= 挡板高度）：
            // 挡板浮在视口上，靠这点余量才能把"最后一行"滚到它上方去。
            maxScroll = baseScroll > 0 ? baseScroll + ObstacleBandPx() : 0;
            scrollTarget = ClampF(scrollTarget, 0, maxScroll);
            scroll = ClampF(scroll, 0, maxScroll);

            NudgeCardsOutOfObstacles();
        }

        /// <summary>
        /// 把"被浮层盖住"的卡片往上挪，直到它不再和禁区相交。
        ///
        /// 为什么需要：卡片位置是会存盘的（自由模式按像素记），所以改变窗口大小/缩放、
        /// 或者用户把卡片拖到控件下面之后，**存下来的位置会一直压着控件** ——
        /// 光修禁区判定不够，得把已经落进去的卡片挪出来。
        /// 只在真的相交时才动，其他卡片的位置一个都不碰。
        /// </summary>
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
                // 先把本卡片从占用表里摘掉，下面才好判断"这一格是不是别人占着"
                int oldKey = CellKey(col, row0);
                int owner;
                if (cellOwner.TryGetValue(oldKey, out owner) && owner == i) cellOwner.Remove(oldKey);

                int row = row0;
                while (row > 0)
                {
                    int key = CellKey(col, row);
                    bool taken = cellOwner.ContainsKey(key);      // ★ 别撞到别的卡片
                    if (!taken)
                    {
                        cellRow[i] = row;
                        Rectangle r = CardRect(i);
                        bool hit = false;
                        for (int k = 0; k < obs.Length; k++) if (r.IntersectsWith(obs[k])) { hit = true; break; }
                        if (!hit) break;                           // 既没压控件、也没撞卡片，就是它了
                    }
                    row--;
                }
                cellRow[i] = row;
                cellOwner[CellKey(col, row)] = i;
                if (row != row0)
                {
                    it.cellRow = row;      // 自由模式优先用 item 里的格坐标，这里一起改
                    moved++;
                }
            }
            if (moved > 0)
            {
                // ★★ 挪完之后统一消解冲突：同一格里绝不能有两张卡。
                //
                // 原来这里只写"被挪的那张"的占用，不管有没有覆盖掉别人、也不把格号同步回
                // 卡片自身 —— 于是下一轮布局时数组和卡片各算各的，两张卡叠在同一格、
                // 每帧互相覆盖，看起来就是"两张卡片重叠着闪"
                // （用户实测：小黄鸭和吸血鬼幸存者叠在一起，拖开一张就不闪了）。
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
                        // 撞格了：按阅读顺序找最近的空格安置
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
                    it.cellCol = c; it.cellRow = nr;      // 同步回卡片自身，两侧永远一致
                }
                Config.Log("布局: " + moved + " 张卡片压在浮层上，已自动上移让开");
                MarkDirtyAll();
                try { cfg.Save(); } catch { }
            }
        }

        /// <summary>挡板从客户区底边往上占的最大高度（右下角按钮 / 左下角控件里更高的那个）。</summary>
        private int ObstacleBandPx()
        {
            Rectangle[] obs = ObstacleRects();
            int band = 0;
            for (int i = 0; i < obs.Length; i++)
                band = Math.Max(band, ClientSize.Height - obs[i].Top);
            return band;
        }

        /// <summary>内容（所有行）的总高度。</summary>
        private int ContentHeight()
        {
            int maxRow = 0;
            for (int i = 0; i < cellRow.Length; i++) if (cellRow[i] > maxRow) maxRow = cellRow[i];
            int rows = cellRow.Length > 0 ? maxRow + 1 : 0;
            return rows > 0 ? rows * cardH + (rows - 1) * gapPx : 0;
        }

        /// <summary>
        /// 浮在墙上面的两块挡板（客户区坐标）：右下角常驻按钮、左下角正在播放控件。
        /// 各边内缩一点做容差 —— 卡片边缘稍微压到透明面板底下不算被盖住。
        /// </summary>
        private Rectangle[] ObstacleRects()
        {
            int tol = (int)Math.Round(10 * scale);
            List<Rectangle> list = new List<Rectangle>(2);
            if (cfg.showControlButton && !ctrlRect.IsEmpty)
            {
                Rectangle b = ctrlRect;
                if (!trashRect.IsEmpty) b = Rectangle.Union(b, trashRect);
                b.Inflate(tol, tol);
                list.Add(b);
            }
            if (cfg.showNowPlaying && npWindow != null)
            {
                // ★ 单位统一用**逻辑**：ClientSize / npWindow.Width / npWindow.Height /
                // cfg.npMargin 全都是逻辑单位，**不要再乘 scale**。
                // 以前写成 cfg.npWidth * scale、cfg.npMargin * scale —— 那是把逻辑值又
                // 放大了一遍（200% 缩放下翻倍），播放器的禁区宽度快占半个屏幕、
                // 位置也偏了：底部明明空着的列全被判成"被挡住"，卡片拖不到最下面一排，
                // 而真正该让开的地方却没让开（用户实测：卡片被播放器盖住）。
                // 控件还没建好（宽高为 0）时跳过，免得加一个空矩形把坐标原点也算成禁区。
                if (npWindow.Width > 0 && npWindow.Height > 0)
                {
                    int m = cfg.npMargin;
                    Rectangle np = new Rectangle(m, ClientSize.Height - m - npWindow.Height,
                                                 npWindow.Width, npWindow.Height);
                    np.Inflate(tol, tol);
                    list.Add(np);
                }
            }
            // 常驻信息条也在禁区里：播放器关掉时禁区只剩按钮那么高，不把它算进来
            // 卡片就会压到时钟上
            Rectangle di = DeskInfoRect();
            if (!di.IsEmpty) { di.Inflate(tol, tol); list.Add(di); }
            return list.ToArray();
        }

        /// <summary>这个格子（按指定滚动位置）会不会被那两块挡板盖住。</summary>
        private bool CellBlockedAt(int col, int row, int scrollPx)
        {
            Rectangle[] obs = ObstacleRects();
            if (obs.Length == 0) return false;
            int x = gridLeft + col * (cardW + gapPx);
            int y = gridTop + gridTopPad + row * (cardH + gapPx) - scrollPx;
            Rectangle r = new Rectangle(x, y, cardW, cardH);
            for (int i = 0; i < obs.Length; i++)
                if (r.IntersectsWith(obs[i])) return true;
            return false;
        }


        // ================================================================
        //  卡片位置：自动排列 / 自由排列
        //
        //  自动排列（cfg.autoArrange = true，默认）：位置就是 view 里的序号，
        //  和以前一样；拖拽 = 重排顺序。
        //  自由排列（关掉之后）：每张卡自己记着一个网格格子，可以拖到任意（包括空的）
        //  格子上，摆出别的形状；位置按桌面项文件名存进 config.json。
        //  这就是桌面图标那个「自动排列图标」开关的等价物。
        // ================================================================

        private int[] cellCol = new int[0];
        private int[] cellRow = new int[0];
        private readonly Dictionary<int, int> cellOwner = new Dictionary<int, int>();   // 格子 -> view 下标
        private static int CellKey(int col, int row) { return col * 4096 + row; }

        /// <summary>项目在位置表里的键：桌面项文件名（和 cfg.order 一致）。</summary>
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

        /// <summary>把 cfg.positions 解析成 文件名 -> [col,row]。</summary>
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

        /// <summary>按当前 view 重新算一遍每张卡的格子（并建立格子 -> 下标的反查表）。</summary>
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
                // 内存里记着的格子优先（拖拽刚落下的就是这个），没有才去配置里找 ——
                // 反过来会让"刚拖完的卡片被配置里的旧位置按回去"。
                int c = it.cellCol, r = it.cellRow;
                int[] p;
                if ((c < 0 || r < 0) && saved.TryGetValue(PosKey(it), out p)) { c = p[0]; r = p[1]; }
                // 落在右下角按钮 / 左下角播放器盖住的格子上就换个位置 ——
                if (c < 0 || c >= useCols || r < 0 || cellOwner.ContainsKey(CellKey(c, r))) { c = -1; r = -1; }
                it.cellCol = c; it.cellRow = r;
            }
            // 没位置（或位置被占了）的卡塞进最前面的空格子
            int scan = 0;
            for (int i = 0; i < n; i++)
            {
                TileItem it = snapshot[i];
                if (it.cellCol >= 0)
                {
                    cellCol[i] = it.cellCol; cellRow[i] = it.cellRow;
                    cellOwner[CellKey(cellCol[i], cellRow[i])] = i;
                    continue;
                }
                while (true)
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

        /// <summary>把当下每张卡的位置写回配置（自由排列用）。</summary>
        private void SavePositions()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            List<string> list = new List<string>();
            for (int i = 0; i < snapshot.Count && i < cellCol.Length; i++)
                list.Add(PosKey(snapshot[i]) + "|" + cellCol[i] + "|" + cellRow[i]);
            cfg.positions = list;
        }

        /// <summary>光标落在哪个格子上（自由排列的落点）。</summary>
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
            // 拖到右下角按钮 / 左下角控件盖住的那几个格子上，就自动往上顶，
            // 免得卡片落下去正好被那两样盖住（旁边的格子照常可用）。
            int guard = 0;
            while (CellBlockedAt(col, row, (int)Math.Round(scroll)) && row > 0 && guard++ < 128) row--;
        }

        /// <summary>自由排列时被拖卡片的落点格子（没拖动过就用它自己的格子）。</summary>
        private void DropCell(out int col, out int row)
        {
            col = dropCol; row = dropRow;
            if (col < 0 || row < 0)
            {
                List<TileItem> snapshot;
                lock (dataLock) { snapshot = view; }
                if (dragIndex >= 0 && dragIndex < cellCol.Length) { col = cellCol[dragIndex]; row = cellRow[dragIndex]; }
                else { CellAt(dragPos, out col, out row); }
            }
        }

        /// <summary>开关「自动排列卡片」。开->关时先按当前格子固定下来，画面不跳。</summary>
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
            InvalidateCards();
            cfg.Save();
            MarkDirtyAll();
            Wake();
            Config.Log(cfg.autoArrange
                ? "自动排列卡片: 开（回到网格顺序排列）"
                : "自动排列卡片: 关（可以把卡片拖到任意格子）");
        }

        /// <summary>自由排列 -> 清掉记录的位置，让所有卡片回到网格自动排列的样子。</summary>
        private void ResetCardPositions()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++) { snapshot[i].cellCol = -1; snapshot[i].cellRow = -1; }
            cfg.positions.Clear();
            Relayout();
            InvalidateCards();
            cfg.Save();
            MarkDirtyAll();
            Wake();
            Config.Log("卡片位置已重置");
        }


        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ================================================================
        //  分层窗口表面
        // ================================================================

        private bool EnsureSurface()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return false;
            if (surface != null && surfW == w && surfH == h) return true;

            ReleaseSurface();

            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h;
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr newDib = IntPtr.Zero, bits = IntPtr.Zero;
            try { newDib = Native.CreateDIBSection(screenDc, ref bi, 0, out bits, IntPtr.Zero, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }

            if (newDib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Config.Log("CreateDIBSection 失败: " + Marshal.GetLastWin32Error());
                return false;
            }

            IntPtr newMemDc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (newMemDc == IntPtr.Zero) { Native.DeleteObject(newDib); return false; }
            IntPtr prev = Native.SelectObject(newMemDc, newDib);

            Bitmap bmp;
            try
            {
                // 直接包住 DIB 内存，省掉每帧 30MB 拷贝
                bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
            }
            catch (Exception ex)
            {
                Config.Log("包装 DIB 失败: " + ex.Message);
                Native.SelectObject(newMemDc, prev);
                Native.DeleteDC(newMemDc);
                Native.DeleteObject(newDib);
                return false;
            }

            dib = newDib; dibBits = bits; memDc = newMemDc; oldObj = prev;
            surface = bmp; surfW = w; surfH = h;
            using (Graphics g = Graphics.FromImage(surface)) g.Clear(Color.Transparent);
            return true;
        }

        private void ReleaseSurface()
        {
            if (surface != null) { surface.Dispose(); surface = null; }
            if (memDc != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero) Native.SelectObject(memDc, oldObj);
                Native.DeleteDC(memDc);
                memDc = IntPtr.Zero; oldObj = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
            dibBits = IntPtr.Zero;
            surfW = surfH = 0;
        }

        /// <summary>
        /// 把 surface 交给 DWM。
        ///
        /// dirty 为空 = 整窗上传；否则走 UpdateLayeredWindowIndirect 并带上 prcDirty，
        /// 告诉 DWM「只有这一块变了」—— 它就不用把整张 3840x2064 位图重新合成一遍。
        /// </summary>
        private void Present(Rectangle dirty)
        {
            if (memDc == IntPtr.Zero || surface == null) return;

            Native.POINT dst = new Native.POINT(Left, Top);
            Native.POINT src = new Native.POINT(0, 0);
            SIZE size = new SIZE(surfW, surfH);
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = 0;
            bf.BlendFlags = 0;
                // 整体透明度 = 淡出进度。分层窗口的 SourceConstantAlpha 作用于整块画面，
                // 也影响命中测试（0 时鼠标穿透），所以淡入淡出不用重画任何像素。
                bf.SourceConstantAlpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(255 * fadeLevel)));
            bf.AlphaFormat = 1;

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (dirty.IsEmpty)
            {
                // 诊断：整窗上传的目标点/位图尺寸一旦发生变化就记一条。
                // 平时（目标点不变）完全不写，所以日志不会刷屏；
                // 而"某一帧按错误偏移上传"这类问题正好会在这里留下一对记录。
                if (Config.Verbose && (dst.X != lastUpX || dst.Y != lastUpY || surfW != lastUpW || surfH != lastUpH))
                {
                    lastUpX = dst.X; lastUpY = dst.Y; lastUpW = surfW; lastUpH = surfH;
                    Config.Log("整窗上传: 目标=(" + dst.X + "," + dst.Y + ") 位图=" + surfW + "x" + surfH +
                               " 窗口=(" + Left + "," + Top + ") 客户区=" + ClientSize.Width + "x" + ClientSize.Height +
                               " fade=" + fadeLevel.ToString("0.00") + " gridTop=" + gridTop + " scroll=" + scroll.ToString("0.00"));
                }
                try
                {
                    Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                                               memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
                }
                finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
                return;
            }

            // 局部上传：把各个子结构放进非托管内存，API 要的是指针
            IntPtr pDst = IntPtr.Zero, pSrc = IntPtr.Zero, pSize = IntPtr.Zero;
            IntPtr pBlend = IntPtr.Zero, pDirty = IntPtr.Zero;
            try
            {
                pDst = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.POINT)));
                Marshal.StructureToPtr(dst, pDst, false);
                pSrc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.POINT)));
                Marshal.StructureToPtr(src, pSrc, false);
                pSize = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SIZE)));
                Marshal.StructureToPtr(size, pSize, false);
                pBlend = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.BLENDFUNCTION)));
                Marshal.StructureToPtr(bf, pBlend, false);

                RECT rc;
                rc.Left = dirty.Left; rc.Top = dirty.Top;
                rc.Right = dirty.Right; rc.Bottom = dirty.Bottom;
                pDirty = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
                Marshal.StructureToPtr(rc, pDirty, false);

                Native.UPDATELAYEREDWINDOWINFO info = new Native.UPDATELAYEREDWINDOWINFO();
                info.cbSize = Marshal.SizeOf(typeof(Native.UPDATELAYEREDWINDOWINFO));
                info.hdcDst = screenDc;
                info.pptDst = pDst;
                info.psize = pSize;
                info.hdcSrc = memDc;
                info.pptSrc = pSrc;
                info.crKey = 0;
                info.pblend = pBlend;
                info.dwFlags = Native.ULW_ALPHA;
                info.prcDirty = pDirty;
                Native.UpdateLayeredWindowIndirect(Handle, ref info);
            }
            finally
            {
                if (pDst != IntPtr.Zero) Marshal.FreeHGlobal(pDst);
                if (pSrc != IntPtr.Zero) Marshal.FreeHGlobal(pSrc);
                if (pSize != IntPtr.Zero) Marshal.FreeHGlobal(pSize);
                if (pBlend != IntPtr.Zero) Marshal.FreeHGlobal(pBlend);
                if (pDirty != IntPtr.Zero) Marshal.FreeHGlobal(pDirty);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ================================================================
        //  素材
        // ================================================================

        private void EnsureAcrylic(bool force)
        {
            Size screen = Screen.PrimaryScreen.Bounds.Size;
            if (!force && wallpaperBlur != null && !acrylicDirty &&
                wpScreenW == screen.Width && wpScreenH == screen.Height) return;
            acrylicDirty = false;

            // 读壁纸文件生成，不需要隐藏窗口（曾试过抓屏，会把其它窗口一起糊进去）
            Bitmap old = wallpaperBlur;
            wallpaperBlur = Acrylic.BuildWallpaperBlur(screen.Width, screen.Height, cfg.acrylicBlur, scale);
            wpScreenW = screen.Width;
            wpScreenH = screen.Height;
            if (old != null) old.Dispose();

            InvalidateCards();
            Config.Log("亚克力壁纸: " + (wallpaperBlur == null ? "不可用（退回深色底）" : wallpaperBlur.Width + "x" + wallpaperBlur.Height));
        }

        /// <summary>
        /// 圆角矩形的柔和投影模板（**全分辨率**，贴图时 1:1，不缩放）。
        ///
        /// 原来是 1/4 分辨率再拉伸 4 倍贴上去，两个毛病：
        ///   · 双三次放大会在边缘产生过冲/欠冲，卡片上方出现一条难看的暗边；
        ///   · 模糊半径按 1/4 尺度算，放大后扩散显得很窄，边缘发硬（用户实测：
        ///     "阴影不够平滑，太突兀"）。
        /// 全分辨率烤一次（尺寸变了才重做），之后每帧 1:1 贴，既没有放大伪影，
        /// 也能用上足够大的模糊半径。
        /// </summary>
        private void EnsureShadow()
        {
            if (shadowBmp != null) return;
            int w = Math.Max(8, tileW), h = Math.Max(8, tileH);
            Bitmap tmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                // 三次盒式模糊的扩散支持约 3*blurR，必须完整落在留白之内，
                // 否则模糊会把颜色 clamp 到位图边缘，在卡片的上下沿形成硬切边。
                // 垂直方向的实际余量是 (inset - off + deflate)，按它反推半径上限。
                float inset = shadowPadPx;
                float off = inset * 0.15f;                 // 阴影向下偏移
                // ★ 阴影形状比卡片缩进一圈：缩进之后，最陡的那段衰减藏在卡片底下，
                //   露在卡片外面的只剩平缓的尾巴 —— 边缘才像"255 到 0 的过渡"。
                //   形状和卡片等大时，卡片边缘处 alpha 还有约 40%，再用十几像素掉到 0，
                //   看起来就是一圈硬边（用户实测："边缘有明显断层，没有过渡"）。
                float deflate = inset * 0.18f;
                int blurR = Math.Max(3, (int)Math.Round((inset - off + deflate) / 3.0f));

                using (Graphics g = Graphics.FromImage(tmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    int alpha = (int)(Math.Max(0.0, Math.Min(1.0, cfg.shadowOpacity)) * 255);
                    using (GraphicsPath p = RoundedRect(
                        new RectangleF(inset + deflate, inset + off + deflate,
                                       cardW - deflate * 2f, cardH - deflate * 2f),
                        Math.Max(2f, radiusPx - deflate * 0.5f)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                        g.FillPath(b, p);
                }
                ImageFx.BoxBlur(tmp, blurR);
                shadowBmp = tmp;
                tmp = null;
                shadowForW = cardW; shadowForH = cardH; shadowForPad = shadowPadPx;
            }
            catch (Exception ex) { Config.Log("生成投影失败: " + ex.Message); }
            finally { if (tmp != null) tmp.Dispose(); }
        }

        // ================================================================
        //  卡片缓存
        // ================================================================

        private void InvalidateCards()
        {
            cacheEpoch++;
            shadowBmp = null;
            cardRetry.Clear();
            cardRetryTries.Clear();
            MarkDirtyAll();
        }

        /// <summary>把所有卡片的位图缓存释放掉（退出、项目列表被整个换掉时用）。</summary>
        private void DisposeAllCardCaches()
        {
            List<TileItem> list;
            lock (dataLock) { list = new List<TileItem>(allItems); }
            foreach (TileItem it in list) if (it != null) it.DropCache();
        }

        // 构建失败的卡片（极偶发的 GDI+ 竞争），下个 tick 重试，别让它一直是白板。
        // 直接拿着 TileItem 引用 —— 缓存是跟着项目走的，不存在"槽位变了"的问题。
        private readonly List<TileItem> cardRetry = new List<TileItem>();
        private readonly Dictionary<TileItem, int> cardRetryTries = new Dictionary<TileItem, int>();

        /// <summary>这张卡需不需要（重新）烤一张常态位图。</summary>
        private bool CacheStale(TileItem it)
        {
            return it.cacheNormal == null || it.cacheEpoch != cacheEpoch;
        }

        /// <summary>增量构建卡片缓存，每次最多 budget 张，避免卡住消息循环。</summary>
        private void EnsureCaches(int budget)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }

            // 时间预算：单张卡片重烤实测约 20ms，按"张数"限流会让一拍吃掉 188ms
            // （用户实测的"卡顿 313 毫秒"就是这么来的，而这一顿正好是卡片抖一下的时机）。
            // 改成按时间限流后，一拍最多占用 10ms，整墙会分批烤完，中途不再冻结。
            const int MsBudget = 10;
            int bakeT0 = unchecked(Environment.TickCount);
            int done = 0;
            for (int i = 0; i < snapshot.Count && done < budget; i++)
            {
                if (done > 0 && unchecked(Environment.TickCount - bakeT0) >= MsBudget) break;
                TileItem it = snapshot[i];
                if (it == null || !CacheStale(it)) continue;
                try { BuildCard(it); }
                catch (Exception ex)
                {
                    Config.Log("构建卡片失败 " + it.name + ": " + ex.Message);
                    if (!cardRetry.Contains(it)) cardRetry.Add(it);
                }
                done++;
            }
            if (done > 0)
            {
                if (Config.Verbose)
                {
                    // 若某一帧用到了半成品位图，画面就会跳一下。剩余=0 表示这是最后一批。
                }
                MarkDirtyAll();
            }

            if (done == 0 && cardRetry.Count > 0)
            {
                // 全部建完了，回头重试之前失败的那几张
                TileItem[] again = cardRetry.ToArray();
                cardRetry.Clear();
                foreach (TileItem it in again)
                {
                    int tries;
                    cardRetryTries.TryGetValue(it, out tries);
                    if (tries >= 8) { cardRetryTries.Remove(it); continue; }
                    try { BuildCard(it); cardRetryTries.Remove(it); }
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

        /// <summary>缓存是否还有没画完的卡片。</summary>
        private bool CachesIncomplete()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i] != null && CacheStale(snapshot[i])) return true;
            return cardRetry.Count > 0;
        }

        private Rectangle CardRect(int i)
        {
            if (i < 0 || i >= cellCol.Length) return Rectangle.Empty;
            return CellRect(cellCol[i], cellRow[i]);
        }

        /// <summary>网格里某个"格子"在客户区的位置（自动排列和自由排列共用）。</summary>
        private Rectangle CellRect(int col, int row)
        {
            int x = gridLeft + col * (cardW + gapPx);
            int y = gridTop + gridTopPad + row * (cardH + gapPx) - (int)Math.Round(scroll);
            return new Rectangle(x, y, cardW, cardH);
        }

        /// <summary>小数槽位版本 —— 自动排列时拖拽退让动画靠它插值。</summary>
        private Rectangle CardRectF(float slot)
        {
            if (slot < 0f) slot = 0f;
            int row = (int)(slot / cols);
            float col = slot - row * cols;
            float x = gridLeft + col * (cardW + gapPx);
            float y = gridTop + gridTopPad + row * (cardH + gapPx) - scroll;
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), cardW, cardH);
        }

        /// <summary>自由排列：小数格子位置 -> 客户区矩形（拖拽让位/落位动画用）。</summary>
        private Rectangle CellRectF(float col, float row)
        {
            float x = gridLeft + col * (cardW + gapPx);
            float y = gridTop + gridTopPad + row * (cardH + gapPx) - scroll;
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), cardW, cardH);
        }

        /// <summary>自由排列：某个客户区坐标换算成"小数格子"（拖拽落位动画的起点）。</summary>
        private void CellAtF(Point pt, out float col, out float row)
        {
            col = (pt.X - tileW / 2f - gridLeft) / (float)(cardW + gapPx);
            row = (pt.Y - tileH / 2f - gridTop - gridTopPad + scroll) / (float)(cardH + gapPx);
        }

        /// <summary>烤一张常态卡片位图（含投影）。必须已经持有 GDI 锁。</summary>
        private void BuildCard(TileItem it)
        {
            lock (Gdi.Lock) { BuildCardRaw(it); }
        }
        private void BuildCardRaw(TileItem it)
        {
            EnsureShadow();          // 投影模板（尺寸变了要重做，这里是最省事的挂点）
            EnsureBitmap(ref it.cacheNormal, tileW, tileH);
            Rectangle cr = CardRectOf(it);
            it.cacheX = cr.X;
            it.cacheY = cr.Y;

            DrawCardInto(it.cacheNormal, it, cr.X, cr.Y, false);
            ApplyOpacity(it.cacheNormal);
            if (it.cacheHover != null) { it.cacheHover.Dispose(); it.cacheHover = null; }
            it.cacheEpoch = cacheEpoch;
            it.label = null;              // 尺寸/位置变了，标签缓存也作废
        }

        /// <summary>烤一张悬停卡片位图（含投影 + 放大 + 名称）。</summary>
        private void BuildHoverCard(TileItem it)
        {
            lock (Gdi.Lock) { BuildHoverCardRaw(it); }
            MarkDirtyAll();
        }
        private void BuildHoverCardRaw(TileItem it)
        {
            EnsureBitmap(ref it.cacheHover, tileW, tileH);
            Rectangle cr = CardRectOf(it);
            DrawCardInto(it.cacheHover, it, cr.X, cr.Y, true);
            ApplyOpacity(it.cacheHover);
        }

        /// <summary>项目当前所在的卡片矩形（自由排列也走这个）。</summary>
        private Rectangle CardRectOf(TileItem it)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
                if (object.ReferenceEquals(snapshot[i], it) && i < cellCol.Length)
                    return CellRect(cellCol[i], cellRow[i]);
            return Rectangle.Empty;
        }

        /// <summary>
        /// 把整张卡片（含投影）整体调成半透明。
        /// 用 ColorMatrix 的 Matrix33 乘 alpha —— 卡片多的时候壁纸还能透出来。
        /// 必须在缓存里烤进去，不能每帧现算，否则 26 张卡每帧都要多一次全尺寸合成。
        /// </summary>
        private void ApplyOpacity(Bitmap bmp)
        {
            double op = cfg.cardOpacity;
            if (bmp == null || op >= 0.999) return;
            if (op < 0.05) op = 0.05;
            int w = bmp.Width, h = bmp.Height;
            try
            {
                using (Bitmap tmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(tmp))
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ColorMatrix m = new ColorMatrix();
                        m.Matrix33 = (float)op;
                        ia.SetColorMatrix(m);
                        g.DrawImage(bmp, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
                    }
                    using (Graphics g2 = Graphics.FromImage(bmp))
                    {
                        g2.CompositingMode = CompositingMode.SourceCopy;
                        g2.DrawImageUnscaled(tmp, 0, 0);
                    }
                }
            }
            catch (Exception ex) { Config.Log("应用卡片不透明度失败: " + ex.Message); }
        }

        /// <summary>
        /// 贴一张卡片缓存。anim > 0 时是「启动成功」的反馈：
        /// 卡片轻微放大 + 一圈强调色脉冲扩散，用来确认点击真的生效了。
        /// </summary>
        /// <summary>点一下的反馈：从点击位置扩散一圈水波纹。</summary>
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
            Wake();
        }

        private void DrawRipples(Graphics g, List<TileItem> snapshot)
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem it = snapshot[i];
                if (it.ripple < 0f || it.ripple >= 1f) continue;
                Rectangle cr = CardRect(i);
                if (cr.Bottom < -tileH || cr.Top > ClientSize.Height + tileH) continue;

                float t = it.ripple;
                // 半径按对角线取，保证能铺满整张卡；指数让它"先快后慢"
                float maxR = (float)Math.Sqrt((double)cr.Width * cr.Width + (double)cr.Height * cr.Height) * 0.60f;
                float r = maxR * (float)Math.Pow(t, 0.55);
                int a = (int)(135 * (1f - t));   // 线性淡出：二次方衰减到后面几乎看不见
                if (a <= 0 || r <= 1f) continue;

                using (GraphicsPath clip = RoundedRect(new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), radiusPx))
                {
                    Region saved = g.Clip;
                    g.SetClip(clip, CombineMode.Intersect);
                    float kx = cr.X + it.rippleX, ky = cr.Y + it.rippleY;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                        g.FillEllipse(b, kx - r, ky - r, r * 2, r * 2);
                    g.Clip = saved;
                }
            }
        }

        private void DrawTile(Graphics g, Bitmap bmp, Rectangle cr, float anim)
        {
            if (anim <= 0.01f)
            {
                // 尺寸对不上（刚缩放完、缓存还没重建）就拉伸一下，别按老尺寸画歪
                if (bmp.Width != tileW || bmp.Height != tileH)
                    g.DrawImage(bmp, new Rectangle(cr.X - shadowPadPx, cr.Y - shadowPadPx, tileW, tileH));
                else
                    g.DrawImage(bmp, cr.X - shadowPadPx, cr.Y - shadowPadPx);
                return;
            }

            float k = 1f + 0.06f * anim;
            float cx = cr.X + cr.Width / 2f;
            float cy = cr.Y + cr.Height / 2f;
            float w = tileW * k, h = tileH * k;

            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, new RectangleF(cx - w / 2f, cy - h / 2f, w, h));
            g.InterpolationMode = old;

            // 强调色脉冲：扩散 + 淡出
            float grow = 10f * scale * (1f - anim);
            RectangleF ring = new RectangleF(cr.X - grow, cr.Y - grow, cr.Width + grow * 2, cr.Height + grow * 2);
            using (GraphicsPath rp = RoundedRect(ring, radiusPx + grow))
            using (Pen pen = new Pen(Color.FromArgb((int)(230 * anim), accent), Math.Max(1.5f, 3f * scale)))
                g.DrawPath(pen, rp);

            using (GraphicsPath fp = RoundedRect(new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), radiusPx))
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(54 * anim), accent)))
                g.FillPath(b, fp);
        }

        private static void EnsureBitmap(ref Bitmap bmp, int w, int h)
        {
            if (bmp != null && bmp.Width == w && bmp.Height == h) return;
            if (bmp != null) bmp.Dispose();
            bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }

        private Bitmap dragLiveCard;      // 拖动中那张卡的实时位图（见 DrawDraggedCard）

        /// <summary>正在被拖的那张卡：跟着光标、半透明；拖到垃圾桶上时更淡。</summary>
        private void DrawDraggedCard(Graphics g, List<TileItem> snapshot)
        {
            if (dragIndex < 0 || dragIndex >= snapshot.Count) return;
            TileItem it = snapshot[dragIndex];
            if (it.cacheNormal == null) return;

            // ★ 每帧实时重烤这一张，不能直接用缓存位图。
            //   卡片底是从模糊壁纸里**按屏幕位置**取样的，缓存位图在烤的时候就把取样
            //   坐标定死在原来那个格子上了 —— 卡片被拖到别处，底下的模糊内容却停在原地
            //   （用户实测："拖动的时候底下的模糊内容是固定不刷新的"）。
            //   只重烤这一张，每帧约 1~2 ms，拖动期间可以接受。
            EnsureBitmap(ref dragLiveCard, tileW, tileH);
            int drawX = (int)Math.Round(dragPos.X - tileW / 2f);
            int drawY = (int)Math.Round(dragPos.Y - tileH / 2f);
            DrawCardInto(dragLiveCard, it, drawX + shadowPadPx, drawY + shadowPadPx, true);

            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(AlphaMatrix(DragOverTrash() ? 0.42f : 0.85f));
                g.DrawImage(dragLiveCard, new Rectangle(drawX, drawY, tileW, tileH),
                    0, 0, tileW, tileH, GraphicsUnit.Pixel, ia);
            }
        }

        // ================================================================
        //  单张卡片绘制（画进缓存位图）
        // ================================================================

        private void DrawCardInto(Bitmap target, TileItem it, int destX, int destY, bool hover)
        {
            using (Graphics g = Graphics.FromImage(target))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                // 悬停时略微放大
                float grow = hover ? 0.05f : 0f;   // 与渲染时 HoverGrow 保持一致
                float gw = cardW * grow, gh = cardH * grow;
                RectangleF cr = new RectangleF(
                    shadowPadPx - gw / 2f, shadowPadPx - gh / 2f, cardW + gw, cardH + gh);
                float rad = radiusPx + gw * 0.5f;

                // 投影：模板已是全分辨率，1:1 贴（缩放会引入边缘伪影）
                if (shadowBmp != null && cfg.shadowOpacity > 0.01)
                {
                    g.DrawImage(shadowBmp, 0, 0, tileW, tileH);
                }

                using (GraphicsPath path = RoundedRect(cr, rad))
                {
                    Region saved = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);

                    if (it.art != null)
                    {
                        // 封面直接铺满整张卡
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(BrightnessMatrix(hover ? 1.06f : 1f));
                            DrawCover(g, it.art, Rectangle.Round(cr), ia, 0.10f);
                        }
                    }
                    else
                    {
                        DrawAcrylicFallback(g, cr, it, destX, destY, hover);
                    }

                    // 之前只看 labelsOnHover，showLabels 压根没被读过 —— 所以那个开关没反应
                    // 标签是否烤进缓存：
                    //   · 常显（showLabels）→ 烤进去，卡片一建就有；
                    //   · 只在悬停时显示 → **不烤**，改由渲染时单独画。
                    //     这样才给得了它独立的淡入曲线（和图标长大分开），
                    //     否则文字跟着整张位图一起出现，面积大、看着就像"啪"地弹出来。
                    if (cfg.showLabels) DrawLabel(g, cr, it, true);

                    g.Clip = saved;
                }

                // 描边
                using (GraphicsPath bp = RoundedRect(cr, rad))
                {
                    if (hover)
                    {
                        using (Pen pen = new Pen(Color.FromArgb(235, accent), Math.Max(1.6f, 1.9f * scale)))
                            g.DrawPath(pen, bp);
                    }
                    else
                    {
                        using (LinearGradientBrush lb = new LinearGradientBrush(
                            new RectangleF(cr.X, cr.Y - 1, cr.Width, cr.Height + 2),
                            Color.FromArgb(72, 255, 255, 255), Color.FromArgb(22, 255, 255, 255),
                            LinearGradientMode.Vertical))
                        using (Pen pen = new Pen(lb, Math.Max(1f, 1f * scale)))
                            g.DrawPath(pen, bp);
                    }
                }
            }
        }

        /// <summary>没有封面时：模糊壁纸 + 白色着色 + 玻璃渐变 + 图标主色柔光 + 大图标。</summary>
        // ================================================================
        //  Windows 11 亚克力的两个关键特征
        //
        //  真正的 Win11 亚克力是「模糊 -> 降饱和（luminosity 混合）-> 中性色调 ->
        //  噪点」，而单纯的"模糊壁纸"只是毛玻璃。降饱和和噪点才是让它看起来
        //  像系统材质的原因，所以这两步必须补上。
        // ================================================================

        /// <summary>降饱和 + 亮度缩放的颜色矩阵。</summary>
        /// <summary>
        /// 降饱和 + 亮度缩放的颜色矩阵。
        ///
        /// 注意 GDI+ 的 ColorMatrix 是「行向量 × 矩阵」：
        ///     R' = R*M00 + G*M10 + B*M20
        ///     G' = R*M01 + G*M11 + B*M21
        ///     B' = R*M02 + G*M12 + B*M22
        /// 也就是 M01 表示「源 R 对输出 G 的贡献」。
        /// 之前把亮度权重摆错了位置（写成了转置），结果绿色权重 0.587 跑到了
        /// 不该在的地方，整块卡片都泛脏绿 —— 这个矩阵必须按上面的行来填。
        /// </summary>
        private static ColorMatrix SatBrightMatrix(float sat, float bright)
        {
            if (sat < 0f) sat = 0f;
            if (sat > 1f) sat = 1f;
            float inv = 1f - sat;
            float r = 0.299f * inv, g = 0.587f * inv, b = 0.114f * inv;   // 亮度权重
            ColorMatrix m = new ColorMatrix();
            m.Matrix00 = (r + sat) * bright; m.Matrix01 = r * bright;         m.Matrix02 = r * bright;
            m.Matrix10 = g * bright;         m.Matrix11 = (g + sat) * bright; m.Matrix12 = g * bright;
            m.Matrix20 = b * bright;         m.Matrix21 = b * bright;         m.Matrix22 = (b + sat) * bright;
            return m;
        }

        private Bitmap noiseTile;

        /// <summary>一小块可平铺的细噪点 —— Win11 亚克力那层几乎看不见的砂砾感。</summary>
        private Bitmap NoiseTile()
        {
            if (noiseTile != null) return noiseTile;
            const int n = 96;
            Bitmap b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            Random rnd = new Random(20251004);   // 固定种子：每次启动纹理一致
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int v = rnd.Next(256);
                    b.SetPixel(x, y, Color.FromArgb(9, v, v, v));
                }
            noiseTile = b;
            return b;
        }
        private void DrawAcrylicFallback(Graphics g, RectangleF cr, TileItem it, int destX, int destY, bool hover)
        {
            bool painted = false;
            // 壁纸优先 —— header 色底只在拿不到壁纸时兜底，
            // 否则有 header 的卡就完全不会跟随它背后的壁纸颜色。
            if (wallpaperBlur == null && it.header != null)
            {
                // 用这个游戏自己的 header 色底（已经压成很小的模糊块，拉满整张卡）
                using (ImageAttributes ia = new ImageAttributes())
                {
                      ia.SetColorMatrix(SatBrightMatrix(1.9f,
                        (float)Math.Max(0.05, Math.Min(1.5, cfg.acrylicBrightness)) + (hover ? 0.08f : 0f)));
                    // 必须显式把 ia 传进去 —— 不传的话上面 SetColorMatrix 白设了，
                    // header 色底会以原始亮度画出来（明显偏亮）
                    g.DrawImage(it.header, Rectangle.Round(cr),
                        0, 0, it.header.Width, it.header.Height, GraphicsUnit.Pixel, ia);
                }
                painted = true;
            }
            else if (wallpaperBlur != null)
            {
                float sx = (Left + destX) / (float)Acrylic.Div;
                float sy = (Top + destY) / (float)Acrylic.Div;
                float sw = cr.Width / (float)Acrylic.Div;
                float sh = cr.Height / (float)Acrylic.Div;
                if (sx < 0) { sw += sx; sx = 0; }
                if (sy < 0) { sh += sy; sy = 0; }
                sw = Math.Min(sw, wallpaperBlur.Width - sx);
                sh = Math.Min(sh, wallpaperBlur.Height - sy);
                if (sw > 1f && sh > 1f)
                {
                    float bright = (float)Math.Max(0.05, Math.Min(1.5, cfg.acrylicBrightness)) + (hover ? 0.08f : 0f);
                    // 深色模式：轻微压暗（0.62 太深，卡片会显得闷）。
                    if (cfg.darkCards) bright *= 0.85f;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                          // 1.9 = 增强饱和：卡片位置的壁纸本来就偏灰白，原样透出没有颜色（用户实测"这么灰"），
                          ia.SetColorMatrix(SatBrightMatrix(1.9f, bright));
                        g.DrawImage(wallpaperBlur, Rectangle.Round(cr), sx, sy, sw, sh, GraphicsUnit.Pixel, ia);
                    }
                    painted = true;
                }
            }
            if (!painted)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 20, 22, 28)))
                    g.FillRectangle(b, cr);

            // 注意：这里**不要**再加一层深冷色去"对齐菜单"。
            // 试过一层 #211F23（alpha 0x78），结果卡片整片发灰、壁纸的色彩全被压掉，
            // 与菜单那种"透出壁纸色彩"的效果反而差得更远（用户实测："灰朦朦的"）。
            // 菜单能绚烂是因为系统 Acrylic 只加很淡的色调，色彩基本保留 —— 卡片也要这样。

            int tint = (int)(Math.Max(0.0, Math.Min(1.0, cfg.acrylicTint)) * 255) + (hover ? 18 : 0);
            if (tint > 0)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, tint), 255, 255, 255)))
                    g.FillRectangle(b, cr);

              // Win11 亚克力那层噪点。少了它怎么看都只是"模糊的图"。
              using (TextureBrush nb = new TextureBrush(NoiseTile()))
                  g.FillRectangle(nb, cr);

            // 渐变刷的矩形要比填充区域**更大一圈**：采样点在高精度像素偏移下会略超出
            // 刷子边界，越界后按 Tile 环绕会取到渐变另一端（最深的颜色），
            // 在边缘留下一像素暗线（用户实测：图标和标题之间的那条黑线）。
            // ★ 不能靠 WrapMode.Clamp 解决 —— LinearGradientBrush 只接受
            //   Tile / TileFlipX / TileFlipY / TileFlipXY，设 Clamp 会抛 ArgumentException
            //   （"参数无效"），整张卡片都画不出来（这个坑刚踩过）。
            using (LinearGradientBrush lg = new LinearGradientBrush(
                new RectangleF(cr.X, cr.Y - 2f, cr.Width, cr.Height + 4f), Color.White, Color.White,
                LinearGradientMode.Vertical))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = new Color[]
                {
                      Color.FromArgb(22, 255, 255, 255),
                    Color.FromArgb(2, 255, 255, 255),
                      Color.FromArgb(30 + (hover ? 10 : 0), 0, 0, 0)
                };
                cb.Positions = new float[] { 0f, 0.38f, 1f };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, cr);
            }

            // 图标主色柔光
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(cr);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = ColorUtils.WithAlpha(
                        ColorUtils.Saturate(it.tint, 1.3f), (int)(115 + (hover ? 45 : 0)));
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, it.tint) };
                    g.FillPath(pg, gp);
                }
            }

            if (it.icon != null)
            {
                float size = Math.Min(cr.Width, cr.Height) * 0.46f;
                float cx = cr.X + cr.Width / 2f;
                float cy = cr.Y + cr.Height / 2f;
                g.DrawImage(it.icon, new RectangleF(cx - size / 2f, cy - size / 2f, size, size));
            }
        }

        /// <summary>
        /// 画卡片名称（含遮罩）。
        ///
        /// `alpha` 是整体不透明度：悬停时由渲染层按自己的曲线传进来，
        /// 让文字和遮罩能有独立于图标长大的淡入淡出。
        /// </summary>
        private void DrawLabel(Graphics g, RectangleF cr, TileItem it, bool withScrim)
        {
            DrawLabel(g, cr, it, withScrim, 1f);
        }

        /// <summary>把 0-255 的 alpha 按整体系数缩放（C# 5 没有局部函数，只能提成静态方法）。</summary>
        private static int ScaleA(int a, float k)
        {
            int v = (int)Math.Round(a * k);
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        /// <summary>
        /// 画卡片名称（含遮罩）。
        ///
        /// `alpha` 是整体不透明度：悬停时由渲染层按自己的曲线传进来，
        /// 让文字和遮罩能有独立于图标长大的淡入淡出。
        /// </summary>
        private void DrawLabel(Graphics g, RectangleF cr, TileItem it, bool withScrim, float alpha)
        {
            if (alpha <= 0.004f) return;
            float lineH = fontCard.GetHeight(g);
            float nameH = lineH * LabelLinesMax + 2f;
            // 遮罩高度占卡片比例：0.10 太矮，暗部在很短的纵向距离里就要从 0 压到 240，
            // 看着就是"到这儿突然暗下来"。拉长到 0.22，过渡距离翻倍（用户实测："断层"）。
            float scrimH = nameH + cr.Height * 0.22f;

            if (withScrim)
            {
                float top = cr.Bottom - scrimH;
                // 遮罩底色用"卡片主题色压暗"，不是纯黑（见 ScrimTint 的说明）
                Color sc = ScrimTint(it.tint, 255, 0.30f);
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(cr.X, top - 2f, cr.Width, scrimH + 4f),
                    Color.FromArgb(0, sc.R, sc.G, sc.B), Color.FromArgb(235, sc.R, sc.G, sc.B),
                    LinearGradientMode.Vertical))
                {
                    // 刷子矩形比填充区大 2px（见上面 DrawAcrylicFallback 里的说明）：
                    // 采样越界会按 Tile 环绕到渐变另一端，在上沿留下一条一像素黑线。
                    // 不能用 WrapMode.Clamp —— LinearGradientBrush 不支持，会抛异常。
                    ColorBlend cb = new ColorBlend(5);
                    cb.Colors = new Color[]
                    {
                        Color.FromArgb(ScaleA(0, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(34, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(92, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(176, alpha), sc.R, sc.G, sc.B),
                        Color.FromArgb(ScaleA(242, alpha), sc.R, sc.G, sc.B)
                    };
                    cb.Positions = new float[] { 0f, 0.28f, 0.55f, 0.8f, 1f };
                    lg.InterpolationColors = cb;
                    g.FillRectangle(lg, cr.X, top, cr.Width, scrimH);
                }
            }

            RectangleF nameRect = new RectangleF(
                cr.X + cr.Width * 0.06f, cr.Bottom - nameH - cr.Height * 0.045f,
                cr.Width * 0.88f, nameH + 4f);

            string label = FitLabel(it, g, nameRect.Width, LabelLinesMax);
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.None;
                // NoWrap：我们已经在 SmartWrap 里断好行了（用 \n），
                // 再让 GDI+ 自动换行的话它会按"任意位置断"重排一遍，断点全废。
                sf.FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap;

                // 文字阴影：原来是 alpha 205 的**实心副本**，边缘是硬切的黑边
                // （用户实测："阴影的边缘还是有明显断层，没有那种 255 到 0 的过度"）。
                // 改成多层低透明度偏移副本叠出柔和过渡：贴近字最实，向外递减到没有。
                for (int k = 1; k <= 3; k++)
                {
                    RectangleF sr = nameRect;
                    sr.Offset(0, Math.Max(0.8f, 0.8f * scale) * k);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(ScaleA(96, alpha), 0, 0, 0)))
                        g.DrawString(label, fontCard, sb, sr, sf);
                }
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(ScaleA(252, alpha), 255, 255, 255)))
                    g.DrawString(label, fontCard, sb, nameRect, sf);
            }
        }

        private void DrawCover(Graphics g, Bitmap src, Rectangle dest, ImageAttributes ia, float topBias)
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

        /// <summary>
        /// 中文友好的折行。
        ///
        /// GDI+ 自带的换行对中文是**任意位置断**，于是标题会被切成
        /// "刺客信条：黑旗 记 / 忆重置"、"牧场物语 来吧！风 / 之繁华集市"这种（用户实测）。
        /// 这里做三件事：
        ///   1. 先在"空格 / 标点之后"找断点，整块搬运，尽量不在词中间断；
        ///   2. 单块本身超宽才退化成按字拆分；
        ///   3. 基本的禁则处理：收尾标点不许跑到行首（往回挪一个字）。
        /// 行数超过 maxLines 时按 maxLines 截断并加省略号。
        /// </summary>
        private string SmartWrap(Graphics g, string text, float maxWidth, int maxLines, StringFormat sf)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (maxLines < 1) maxLines = 1;
            // 留 2px 安全余量：折行器用的 Graphics 和绘制时那个不是同一个，度量有细微差异。
            // 量得"刚好放下"的话，绘制时 GDI+ 会再折一次，前面算好的断点就白费了
            // （实测：Palworld 幻兽帕鲁、异形丛生：虫启天降 都被二次折行切开）。
            maxWidth -= 2f;
            if (maxWidth < 8f) maxWidth = 8f;

            const string breakAfter = " ：:！!？?，,、；;。.·-—~～/|";
            const string noLineStart = "。，、！？：；）】》」』…·:;,.!?)]>%";

            // 1) 切成"首选断点块"（标点跟着前面的内容走）
            List<string> units = new List<string>();
            StringBuilder acc = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                acc.Append(c);
                if (breakAfter.IndexOf(c) >= 0) { units.Add(acc.ToString()); acc.Length = 0; }
            }
            if (acc.Length > 0) units.Add(acc.ToString());

            // 2) 贪心装行
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

                // 单块自己也放不下 -> 按字拆
                string rest = u;
                while (rest.Length > 0)
                {
                    int take = rest.Length;
                    while (take > 1 && MeasureLineWidth(g, rest.Substring(0, take), sf) > maxWidth)
                        take--;
                    // 禁则：拆出来那行的第一个字是收尾标点的话，把它挪回上一行
                    if (take < rest.Length && noLineStart.IndexOf(rest[take]) >= 0 && take > 1) take--;
                    pushLine(rest.Substring(0, take));
                    rest = rest.Substring(take);
                }
            }
            if (line.Length > 0) lines.Add(line.ToString());

            // 3) 超出允许行数就截断
            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                // 末行如果只剩标点（比如 "Vampire Survivors ｜ -"），删掉它、把省略号并到上一行，
                // 免得出现"第二行只有一个横杠"这种。
                while (lines.Count > 1 && lines[lines.Count - 1].Trim().TrimEnd('…').Length <= 1)
                    lines.RemoveAt(lines.Count - 1);
                lines[lines.Count - 1] = lines[lines.Count - 1].TrimEnd() + "…";
            }
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>
        /// 量一行文字的真实宽度。
        ///
        /// ★ 必须用**很大**的布局矩形去量：MeasureString 在开启自动换行的 StringFormat 下
        ///   会把返回值压到布局宽度以内，用 maxWidth 去量的话"是否放得下"永远为真，
        ///   折行器就一个换行都不插（实测就是这个 bug，标题最后仍是 GDI+ 任意位置断的）。
        /// </summary>
        private float MeasureLineWidth(Graphics g, string s, StringFormat sf)
        {
            return g.MeasureString(s, fontCard, new SizeF(100000f, 100000f), sf).Width;
        }

        /// <summary>
        /// 名称遮罩的底色：卡片主题色（图标主色）压暗之后的颜色，而不是近纯黑。
        /// 纯黑遮罩和卡片本身没关系，看着像贴了块黑布；压暗的主题色会带一点卡片自己的
        /// 色调（用户要求："卡片内阴影能从全黑改成稍暗的主题色吗"）。
        /// 压暗系数保证足够暗，白字对比度不受影响。
        /// </summary>
        private static Color ScrimTint(Color tint, int alpha, float k)
        {
            int r = (int)Math.Round(tint.R * k);
            int g = (int)Math.Round(tint.G * k);
            int b = (int)Math.Round(tint.B * k);
            // 保底：别低于 10，否则和纯黑没区别
            if (r < 10) r = 10;
            if (g < 10) g = 10;
            if (b < 12) b = 12;
            return Color.FromArgb(alpha, r, g, b);
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
                // 先用中文友好的折行器断好，再量高度
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

        private static StringFormat MeasureFormat()
        {
            StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.None;
            sf.FormatFlags = 0;
            return sf;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            float max = Math.Min(r.Width, r.Height);
            if (d > max) d = max;
            if (d <= 0.5f)
            {
                p.AddRectangle(r);
                p.CloseFigure();
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ================================================================
        //  渲染
        // ================================================================

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }



        // ================================================================
        //  脏区
        //
        //  一帧完整重绘要 20ms（26 张卡的 alpha 混合）+ 5ms（整屏上传）。
        //  悬停动画、启动脉冲这类变化只影响一两张卡，却让整屏重画太亏。
        //  所以引入「脏区」：
        //    dirtyFull = true            -> 整窗重绘（默认，安全的兜底）
        //    dirtyFull = false + 一个矩形 -> 只清、只画、只上传那一块
        //  任何拿不准的改动都走 MarkDirtyAll()，只有明确知道影响范围的地方
        //  （悬停卡片、启动动画卡片）才用 MarkDirty(rect)。
        // ================================================================

        private Rectangle dirtyRect = Rectangle.Empty;
        private bool dirtyFull = true;

        private void MarkDirtyAll()
        {
            dirty = true;
            dirtyFull = true;
            dirtyRect = Rectangle.Empty;
        }

        /// <summary>标记一块区域需要重画。已经是整窗重绘时这个调用自然被忽略。</summary>
        private void MarkDirty(Rectangle r)
        {
            dirty = true;
            if (dirtyFull) return;
            if (r.Width <= 0 || r.Height <= 0) return;
            r.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            if (r.Width <= 0 || r.Height <= 0) return;
            dirtyRect = dirtyRect.IsEmpty ? r : Rectangle.Union(dirtyRect, r);


        }

        /// <summary>第 i 张卡实际占的图块（含投影外扩）。</summary>
        private Rectangle TileRectOf(int i)
        {
            Rectangle cr = CardRect(i);
            cr.Inflate(shadowPadPx + 2, shadowPadPx + 2);
            return cr;
        }

        private bool Touches(Rectangle region, Rectangle r)
        {
            return region.IsEmpty || r.IntersectsWith(region);
        }

        /// <summary>
        /// 淡入淡出只换整体透明度、一个像素都不重画，所以 Present 也不需要整窗上传：
        /// 返回画布上真正画了东西的那块（卡片图块 + 右下角按钮）。其余像素 alpha 本来
        /// 就是 0，整体透明度怎么变都看不见。
        /// 拿不准的情况（拖拽中、菜单开着、墙是隐藏的）返回 Empty —— 那是"整窗"。
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
                if (cr.Bottom < -tileH || cr.Top > ClientSize.Height + tileH) continue;
                cr.Inflate(shadowPadPx + 2, shadowPadPx + 2);
                r = r.IsEmpty ? cr : Rectangle.Union(r, cr);
            }
            if (!trashRect.IsEmpty) r = r.IsEmpty ? trashRect : Rectangle.Union(r, trashRect);
            if (cfg.showControlButton && !ctrlRect.IsEmpty) r = r.IsEmpty ? ctrlRect : Rectangle.Union(r, ctrlRect);
            r.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            return r;
        }

        private void Render(Rectangle region)
        {
            if (!IsHandleCreated || !Visible) return;
            if (!EnsureSurface()) return;
            EnsureAcrylic(false);

            long t0 = Stopwatch.GetTimestamp();

            try
            {
                List<TileItem> snapshot;
                lock (dataLock) { snapshot = view; }

                using (Graphics g = Graphics.FromImage(surface))
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // 整帧成本的大头就是这几十张卡的 alpha 混合：HighSpeed 关掉 GDI+ 的
                    // gamma 校正混合，像素混合快一截（壁纸上的观感差别看不出来）。
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // 文字渲染提示必须在这里设：卡片名称、时钟、菜单都在这块 Graphics 上画 ——
                    // 默认的 SystemDefault 在透明位图上会发毛（卡片自己有设，菜单之前漏了）。
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    if (region.IsEmpty)
                    {
                        g.Clear(Color.Transparent);
                    }
                    else
                    {
                        // 局部重绘：只清这一块（SourceCopy 才能真正擦成透明），
                        // 其余像素留在 surface 上不动 —— 省下的就是那 20ms。
                        g.SetClip(region, CombineMode.Replace);
                        CompositingMode oldMode = g.CompositingMode;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        using (SolidBrush clear = new SolidBrush(Color.Transparent))
                            g.FillRectangle(clear, region);
                        g.CompositingMode = oldMode;
                    }
                    // 屏幕右边缘垫一层 alpha = 1 的隐形命中区。
                    // 分层窗口是按像素 alpha 命中：网格有内边距，最右边那条本来是全透明的，
                    // 鼠标消息会直接穿到桌面 —— 边缘手势的"起手"就收不到按下事件。
                    // 1/255 肉眼看不见，但不再是 0。
                    using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                        g.FillRectangle(hit, new Rectangle(ClientSize.Width - EdgeZone, 0, EdgeZone, ClientSize.Height));

                    // 墙"隐藏"时：卡片和按钮一律不画，整块保持透明 ——
                    // 鼠标会穿透到桌面，效果和真的隐藏窗口一样。
                    // 但**不能把窗口 Visible = false**：托盘右键菜单就画在这块画布上，
                    // 窗口一隐藏，菜单既显示不出来也收不到鼠标。
                    if (!wallHidden)
                    {
                    DrawPreviewBackdrop(g);

                    // 拖拽中：自动排列时按预览顺序画（其余卡片自动退让）；
                    // 自由排列时所有卡片留在自己的格子里，只画一个落点虚框 + 跟手的那张。
                    if (dragging && !cfg.autoArrange)
                    {
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            if (i == dragIndex) continue;        // 被拖的那张跟着光标画
                            TileItem fi = snapshot[i];
                            Rectangle cr = fi.animCol >= 0f ? CellRectF(fi.animCol, fi.animRow) : CardRect(i);
                            if (cr.Bottom < -tileH || cr.Top > ClientSize.Height + tileH) continue;
                            if (!Touches(region, TileRectOf(i))) continue;
                            Bitmap bmp = fi.cacheNormal;
                            if (bmp == null) continue;
                            DrawTile(g, bmp, cr, fi.launchAnim);
                        }

                        int tc, tr;
                        DropCell(out tc, out tr);
                        if (!DragOverTrash())
                        {
                            Rectangle sr = CellRect(tc, tr);
                            using (GraphicsPath sp = RoundedRect(
                                new RectangleF(sr.X, sr.Y, sr.Width, sr.Height), radiusPx))
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

                        DrawDraggedCard(g, snapshot);
                    }
                    else if (dragging && dragPreview != null)
                    {
                        for (int slot = 0; slot < dragPreview.Length; slot++)
                        {
                            int itemIdx = dragPreview[slot];
                            if (itemIdx == dragIndex) continue;
                            // 退让动画：卡片画在它"当前"的小数槽位上，而不是目标槽位
                            float aslot = snapshot[itemIdx].animSlot;
                            Rectangle cr = aslot >= 0f ? CardRectF(aslot) : CardRect(slot);
                            if (cr.Bottom < -tileH || cr.Top > ClientSize.Height + tileH) continue;
                            if (!Touches(region, TileRectOf(slot))) continue;
                            if (itemIdx < 0 || itemIdx >= snapshot.Count) continue;
                            Bitmap bmp = snapshot[itemIdx].cacheNormal;
                            if (bmp == null) continue;
                            DrawTile(g, bmp, cr, snapshot[itemIdx].launchAnim);
                        }

                        // 空出来的位置：一个淡淡的占位框
                        int emptySlot = Array.IndexOf(dragPreview, dragIndex);
                        if (emptySlot >= 0 && !DragOverTrash())
                        {
                            Rectangle sr = CardRect(emptySlot);
                            using (GraphicsPath sp = RoundedRect(
                                new RectangleF(sr.X, sr.Y, sr.Width, sr.Height), radiusPx))
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

                        DrawDraggedCard(g, snapshot);
                    }
                    else
                    {
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            TileItem fi = snapshot[i];
                            Rectangle cr = fi.animCol >= 0f ? CellRectF(fi.animCol, fi.animRow) : CardRect(i);
                            if (cr.Bottom < -tileH || cr.Top > ClientSize.Height + tileH) continue;
                            if (!Touches(region, TileRectOf(i))) continue;
                            Bitmap bmp = fi.cacheNormal;
                            if (bmp == null) continue;
                            DrawTile(g, bmp, cr, fi.launchAnim);
                        }
                    }

                    // 选中框：只在右键菜单打开时画，用来指示菜单作用于哪张卡。
                    // 绑定到选中状态是故意的 —— 平时不该存在任何"选中"状态，
                    // 鼠标不在卡片上时就不该有框。
                    {
                        Rectangle sr = CardRect(selIndex);
                        if (sr.Bottom >= -cardH && sr.Top <= ClientSize.Height + cardH && selIndex != hoverIndex)
                        {
                            using (GraphicsPath sp = RoundedRect(
                                new RectangleF(sr.X, sr.Y, sr.Width, sr.Height), radiusPx))
                            using (Pen pen = new Pen(Color.FromArgb(235, accent), Math.Max(1.6f, 1.9f * scale)))
                                g.DrawPath(pen, sp);
                        }
                    }

                    // 悬停卡片：用预渲染的 hover 版本做淡入，避免每帧重绘
                    if (hoverIndex >= 0 && hoverIndex < snapshot.Count)
                    {
                        TileItem hit = snapshot[hoverIndex];
                        float hov = hit.hover;
                        if (!Touches(region, TileRectOf(hoverIndex))) hov = 0f;
                        if (hov > 0.01f)
                        {
                            if (hit.cacheHover == null) BuildHoverCard(hit);
                            if (hit.cacheHover != null)
                            {
                                Rectangle cr = CardRect(hoverIndex);
                                // 过渡动画：不只是淡入，同时把悬停位图**从原始尺寸放大到悬停尺寸**。
                                //   之前只在两个不同大小的位图之间做透明叠加，图标看着像重影而不是"长大"。
                                //   悬停位图本身按 HoverGrow 画大了一圈，这里从 1/(1+HoverGrow) 缩放着长上去：
                                //   t=0 时两者尺寸完全一致（不会出现双影），t=1 时正好落到悬停尺寸。
                                //   smoothstep 让起止都不突兀。
                                float e = hov * hov * (3f - 2f * hov);
                                const float HoverGrow = 0.05f;
                                float s = 1f / (1f + HoverGrow) + (1f - 1f / (1f + HoverGrow)) * e;
                                float ccx = cr.X + cr.Width / 2f;
                                float ccy = cr.Y + cr.Height / 2f;
                                float dw = tileW * s, dh = tileH * s;
                                using (ImageAttributes ia = new ImageAttributes())
                                {
                                    ia.SetColorMatrix(AlphaMatrix(e));
                                    InterpolationMode oldIp = g.InterpolationMode;
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(hit.cacheHover,
                                        Rectangle.Round(new RectangleF(ccx - dw / 2f, ccy - dh / 2f, dw, dh)),
                                        0, 0, tileW, tileH, GraphicsUnit.Pixel, ia);
                                    g.InterpolationMode = oldIp;
                                }

                                // 名称单独淡入：曲线比卡片长大**晚一点**（e 过了 30% 才开始），
                                // 于是"卡片先撑开、名字随后浮出来"，两个动作分开才看得出淡入。
                                // 遮罩、文字阴影都随这个 alpha 一起缩放（见 DrawLabel 的 ScaleA）。
                                if (!cfg.showLabels && cfg.labelsOnHover)
                                {
                                    float la = (e - 0.35f) / 0.65f;
                                    if (la < 0f) la = 0f; else if (la > 1f) la = 1f;
                                    la = la * la * (3f - 2f * la);
                                    if (la > 0.004f)
                                    {
                                        // ★ 用**最终尺寸**排版，不用动画中缩小的矩形。
                                        //   否则动画前半段卡片还小、文字宽度不够会折成两行，
                                        //   动画结束时宽度够了又立刻重排成一行（用户实测：
                                        //   "标题在动画结束后可能文字显示的区域变宽了，会立即重排"）。
                                        //   文字几何固定、只淡入，就不会有重排。
                                        float cwF = cr.Width * (1f + HoverGrow);
                                        float chF = cr.Height * (1f + HoverGrow);
                                        RectangleF crFinal = new RectangleF(
                                            ccx - cwF / 2f, ccy - chF / 2f, cwF, chF);
                                        Region savedClip = g.Clip;
                                        using (GraphicsPath lp = RoundedRect(crFinal, radiusPx * (1f + HoverGrow)))
                                        {
                                            g.SetClip(lp, CombineMode.Intersect);
                                            DrawLabel(g, crFinal, hit, true, la);
                                            g.Clip = savedClip;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    DrawRipples(g, snapshot);   // 必须在 hover 浮层之后，否则会被盖住
                    DrawControlButton(g);
                    }
                    // 频谱放在绘制流程的最外层：原来它被包在一个条件块里，
                    // 某些状态下整段绘制会被跳过，频谱就"自己消失"了（用户实测）。
                    DrawSpectrum(g);
                }
            }
            catch (Exception ex) { Config.Log("渲染异常: " + ex.Message); }

            double drawMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            long pt0 = Stopwatch.GetTimestamp();
            Present(region);
            double presentMs = (Stopwatch.GetTimestamp() - pt0) * 1000.0 / Stopwatch.Frequency;


            // 长期帧数统计：每 10 秒报一次「画了多少帧、平均多久」。
            // 排查「空闲却在烧 CPU」这类问题全靠它 —— 只看日志就知道是谁在触发重绘。
            npRenderFrames++;
            if (region.IsEmpty) { npFullFrames++; npFullMs += drawMs + presentMs; }
            else npPartialFrames++;
            npRenderDrawMs += drawMs;
            npRenderPresentMs += presentMs;
            npRenderMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            StatsTick();

            // 帧率统计（只在动画期间统计，60 帧汇总一次）
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (AnimationActive())
            {
                frameCount++; frameMsTotal += ms; fpsWindowOpen = true;
                if (frameCount >= 60)
                {
                    Config.Log("渲染性能: 平均 " + (frameMsTotal / frameCount).ToString("0.0") + " ms/帧 (" +
                               (1000.0 / (frameMsTotal / frameCount)).ToString("0") + " fps)");
                    frameCount = 0; frameMsTotal = 0;
                }
            }
            else if (fpsWindowOpen)
            {
                if (frameCount > 3)
                    Config.Log("渲染性能: 平均 " + (frameMsTotal / frameCount).ToString("0.0") + " ms/帧 (" +
                               (1000.0 / (frameMsTotal / frameCount)).ToString("0") + " fps)");
                frameCount = 0; frameMsTotal = 0; fpsWindowOpen = false;
            }
        }

        /// <summary>
        /// 每 60 秒把一帧统计写进日志（**且只在这段时间真的渲染过帧时**）。
        ///
        /// 原来是 10 秒一次、空闲也照记 —— 一天就是近万行，日志文件会被这类周期性诊断
        /// 撑爆（用户实测反馈）。现在空闲时（一帧都没渲染）直接跳过，周期也放宽到 60 秒；
        /// 真出问题时有渲染才可能出现日志，诊断能力没丢。
        /// Render 和"只换透明度"的淡入淡出都会调它 —— 以前淡入淡出的帧完全不进统计
        /// （它不走 Render），所以"淡出只有三四帧"这种问题在日志里根本看不见。
        /// </summary>
        private void StatsTick()
        {
            if (npRenderWindowStart == 0) { npRenderWindowStart = unchecked(Environment.TickCount); return; }
            if (unchecked(Environment.TickCount - npRenderWindowStart) < 60000) return;
            if (npRenderFrames > 0)
                Config.Log("帧统计: 60 秒内 " + npRenderFrames + " 帧（整窗 " + npFullFrames + " / 局部 " + npPartialFrames +
                           " / 淡出 " + npFadeFrames + "），平均 " +
                           (npRenderMs / Math.Max(1, npRenderFrames)).ToString("0.0") + " ms/帧" +
                           "（绘制 " + (npRenderDrawMs / Math.Max(1, npRenderFrames)).ToString("0.0") +
                           " + 上传 " + (npRenderPresentMs / Math.Max(1, npRenderFrames)).ToString("0.0") + "）" +
                           "，整窗 " + (npFullMs / Math.Max(1, npFullFrames)).ToString("0.0") + " ms/帧");
            npRenderFrames = 0; npRenderMs = 0; npRenderDrawMs = 0; npRenderPresentMs = 0;
            npFullFrames = 0; npPartialFrames = 0; npFadeFrames = 0; npFullMs = 0;
            npRenderWindowStart = unchecked(Environment.TickCount);
        }

        private bool AnimationActive()
        {
            if (Math.Abs(scrollTarget - scroll) > 0.5f) return true;
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i].hover > 0.01f && snapshot[i].hover < 0.99f) return true;
                if (snapshot[i].launchAnim > 0f) return true;
            }
            return false;
        }

        private void EnsurePreviewBackdrop()
        {
            if (!windowed || previewBackdrop != null) return;
            try
            {
                string wp = Native.GetWallpaperPath();
                if (string.IsNullOrEmpty(wp) || !File.Exists(wp)) return;
                Size screen = Screen.PrimaryScreen.Bounds.Size;
                using (Image raw = Image.FromFile(wp))
                using (Bitmap src = new Bitmap(raw))
                    previewBackdrop = ImageFx.CoverFit(src, screen.Width, screen.Height);
            }
            catch (Exception ex) { Config.Log("预览壁纸底生成失败: " + ex.Message); }
        }

        private void DrawPreviewBackdrop(Graphics g)
        {
            if (!windowed) return;
            EnsurePreviewBackdrop();
            if (previewBackdrop == null) return;
            g.DrawImage(previewBackdrop,
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                Left, Top, ClientSize.Width, ClientSize.Height, GraphicsUnit.Pixel);
        }

        // ================================================================
        //  右下角常驻控制按钮
        // ================================================================

        /// <summary>
        /// 屏幕底部居中的音频频谱条。
        ///
        /// 电平来自 AudioMeter（系统默认播放设备混音后的真实峰值），形状用钟形包络放大，
        /// 所以看着像频谱，反应也是真的 —— 但它是**电平**，不是逐频段的频谱。
        /// 画在墙上，所以只在桌面可见（被其它窗口盖住时不显示）。
        /// </summary>
        private Rectangle spectrumRect = Rectangle.Empty;

        /// <summary>
        /// 墙的中心点是否被别的进程的窗口盖住。
        /// 用来在窗口最大化时停掉"为频谱保持高帧率"这件事 —— 看不见就没必要画。
        /// 只做一次采样，开销是一次 WindowFromPoint，可忽略。
        /// </summary>
        private bool WallCovered()
        {
            try
            {
                // 三点采样（底部左/中/右）：全部被别的进程盖住才算遮挡。
                // 单点采样会被游戏自带的置顶小浮层（成就提示、FPS 计数）骗到，
                // 明明看得见却停画。
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
                    if (pid == me) return false;      // 自己的浮层不算遮挡
                }
                return true;
            }
            catch { return false; }
        }

        private void DrawSpectrum(Graphics g)
        {
            if (!cfg.showSpectrum) { spectrumRect = Rectangle.Empty; return; }
            int n = Math.Max(8, Math.Min(128, cfg.spectrumBars));
            float[] lv = AudioSpectrum.Bands(n);
            if (lv == null || lv.Length != n) { spectrumRect = Rectangle.Empty; return; }

            int wantW = Math.Max(64, (int)Math.Round(ClientSize.Width *
                        Math.Max(0.1, Math.Min(1.0, cfg.spectrumWidth))));
            // ★ 全程用浮点算柱距，总跨度**恒等于 wantW**。
            //   原来先取整算柱宽、再取整算间隙，余数被截断；柱宽每变 1 像素，
            //   余数就跳 n 像素，间隙取整后可能一次跳两格 —— 总跨度就会突跳几十像素
            //   （用户实测："从 0.6 拉到 0.55 的一瞬间占屏比会变"）。
            double barRatio = Math.Max(0.15, Math.Min(1.0, cfg.spectrumBarWidth));
            double step = wantW / (double)n;               // 每根柱子占的横向步距
            int barW = Math.Max(2, (int)Math.Round(step * barRatio));
            // 滑条拉满 = 柱间无缝：宽度向上取整，保证相邻柱子相接、不留缝
            if (barRatio >= 0.999) barW = Math.Max(2, (int)Math.Ceiling(step));
            int total = wantW;                             // 跨度固定，不再随柱宽变
            AudioSpectrum.FallSec = (float)Math.Max(0.02, cfg.spectrumFall);
            int maxH = Math.Max(20, (int)Math.Round(cfg.spectrumHeight * scale));
            int baseline = ClientSize.Height;              // 贴着屏幕底部，不留空
            int x0 = (ClientSize.Width - total) / 2;
            spectrumRect = new Rectangle(x0 - 4, baseline - maxH - 4, total + 8, maxH + 8);

            for (int i = 0; i < n; i++)
            {
                float h = lv[i] * maxH;
                if (h < 4f) h = 4f;   // 最小高度：静止时也留一条看得见的底线，不会"整个消失"
                RectangleF r = new RectangleF((float)(x0 + i * step), baseline - h, barW, h);
                int op = (int)((55 + 75 * lv[i]) * Math.Max(0.1, Math.Min(2.0, cfg.spectrumOpacity)));
                int a = op > 255 ? 255 : op;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 238, 242, 252)))
                    g.FillRectangle(b, r);                 // 不切圆角：贴着屏幕底边，圆角反而破相
            }
        }

        private void DrawControlButton(Graphics g)
        {
            if (dragging && !trashRect.IsEmpty)
                DrawRoundButton(g, trashRect, true, GlyphTrash);
            else if (cfg.showControlButton && !trashRect.IsEmpty)
                DrawRoundButton(g, trashRect, trashHover, GlyphTrash);

            if (cfg.showControlButton && !ctrlRect.IsEmpty)
                DrawRoundButton(g, ctrlRect, ctrlHover, GlyphGear);
        }

        private const string GlyphTrash = "\uE74D";   // Win11 系统图标：删除
        private const string GlyphGear = "\uE713";    // Win11 系统图标：设置
        private static Font glyphFont;
        private static float glyphFontPx;

        /// <summary>Windows 11 的系统图标字体（Segoe Fluent Icons）。</summary>
        private Font GlyphFont(float px)
        {
            if (glyphFont != null && Math.Abs(glyphFontPx - px) < 0.5f) return glyphFont;
            if (glyphFont != null) { glyphFont.Dispose(); glyphFont = null; }
            string fam = "Segoe Fluent Icons";
            try { using (FontFamily ff = new FontFamily(fam)) { } }
            catch { fam = "Segoe MDL2 Assets"; }   // Win10 的等价字体
            glyphFont = new Font(fam, px, FontStyle.Regular, GraphicsUnit.Pixel);
            glyphFontPx = px;
            return glyphFont;
        }

        /// <summary>
        /// Windows 11 的「subtle button」：平时几乎全透明、悬停才浮出一层很淡的底，
        /// 圆角只有 4~8px（不像以前那样圆成胶囊），描边是 1px 极淡的白。
        /// 图标直接用 Segoe Fluent Icons 的字形 —— 那就是 Win11 系统图标本体。
        /// </summary>
        private void DrawRoundButton(Graphics g, Rectangle rect, bool hover, string glyph)
        {
            RectangleF r = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
            float rad = 7f * scale;
            // 启动后 12 秒内全亮，之后收敛成一个低调的常驻按钮
            bool intro = unchecked(Environment.TickCount - startTick) < 12000;
            float fade = hover ? 1f : (intro ? 1f : 0.62f);

            using (GraphicsPath p = RoundedRect(r, rad))
            {
                // 底：Win11 的控件默认是"玻璃上的淡色块"，不是实心圆
                int bg = (int)((hover ? 26 : 11) * fade);
                if (bg > 0)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, bg), 255, 255, 255)))
                        g.FillPath(b, p);

                // 描边：Win11 控件那圈 1px 的白，悬停时更亮一点
                using (Pen pen = new Pen(Color.FromArgb((int)((hover ? 52 : 24) * fade), 255, 255, 255),
                    Math.Max(1f, scale)))
                    g.DrawPath(pen, p);

                // 字形（Segoe Fluent Icons）
                Font f = GlyphFont(rect.Height * 0.44f);
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    int ga = (int)((hover ? 255 : 232) * fade);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, ga), 255, 255, 255)))
                        g.DrawString(glyph, f, b, r, sf);
                }
            }
        }

        // ================================================================
        //  右键菜单
        //
        //  为什么不用 ContextMenuStrip：本窗口永远不激活（WM_MOUSEACTIVATE 返回
        //  MA_NOACTIVATE，为了点击不把自己顶到最前面），而 WinForms 的弹出菜单
        //  依赖激活才能正常工作，结果是菜单显示得出来但点不动。
        //  自己画就没这问题 —— 命中判断和卡片走同一套，一定点得动，顺便还能做成
        //  Win11 那种圆角磨砂面板。
        // ================================================================



        private int MenuItemH { get { return (int)Math.Round(40 * scale); } }   // Win11 现代菜单约 40px @100%
        private int MenuSepH { get { return (int)Math.Round(11 * scale); } }
        private int MenuPad { get { return (int)Math.Round(6 * scale); } }
        private int MenuIconW { get { return (int)Math.Round(34 * scale); } }

        /// <summary>
        /// 弹出菜单。
        ///
        /// 和本机 TranslucentTB 的做法一致：系统渲染、硬件合成、天生 TOPMOST。
        /// 自绘分层窗口那套（绘制、材质、鼠标捕获、坐标换算、层级维护）全部退役 ——
        /// 它们既是性能瓶颈，也是一连串 bug 的来源。
        /// 保留旧的自绘实现以备回溯，但下面这段不再走它。
        /// </summary>
        private void OpenMenu(Point at, List<MenuEntry> items)
        {
            if (items == null || items.Count == 0) return;
            try
            {
                Point screenPt = PointToScreen(at);
                // WPF 版菜单：亚克力 + 圆角 + 图标列（对齐 TranslucentTB 的 WinUI 观感）。
                // 不用系统 TrackPopupMenu 是因为它拿不到亚克力模糊与自绘配色。
                int idx = WpfMenu.Show(items, screenPt, scale);
                if (idx >= 0 && idx < items.Count)
                {
                    MenuEntry e = items[idx];
                    if (e != null && e.enabled && !e.separator && e.action != null) e.action();
                }
            }
            catch (Exception ex) { Config.Log("弹出菜单失败: " + ex.Message); }
            MarkDirtyAll();
            Wake();
        }




        /// <summary>
        /// 把两个浮层插到墙**正下方**（菜单打开期间用）。
        ///
        /// 这里不能用 HWND_NOTOPMOST：那只是把窗口放到"非顶层窗口带的最上面"，
        /// 是否真在墙下面取决于墙自己在哪一带 —— 实测墙的 TOPMOST 不一定生效，
        /// 于是浮层照样骑在菜单上。直接用 `SetWindowPos(浮层, 墙)` 把它插到墙后面，
        /// 不管两者各在哪一带都成立。
        /// </summary>
        private void PushOverlaysBelowWall()
        {
            uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW;
            try
            {
                if (npWindow != null && !npWindow.IsDisposed && npWindow.IsHandleCreated)
                    Native.SetWindowPos(npWindow.Handle, Handle, 0, 0, 0, 0, flags);
            }
            catch { }
            try
            {
                if (deskInfo != null && !deskInfo.IsDisposed && deskInfo.IsHandleCreated)
                    Native.SetWindowPos(deskInfo.Handle, Handle, 0, 0, 0, 0, flags);
            }
            catch { }
            try
            {
                Config.Log("菜单: 浮层已压到墙下 (墙 TOPMOST=" +
                           ((Native.GetWindowLong(Handle, Native.GWL_EXSTYLE) & 0x8) != 0) + ")");
            }
            catch { }
        }

        /// <summary>
        /// 找系统 shell 弹出层（任务栏"隐藏的图标"溢出面板等）中面积最大的那个矩形。
        ///
        /// 只认类名里带 Overflow / XamlExplorerHost 的窗口 —— 不能把
        /// `Windows.UI.Core.CoreWindow` 也算进来：输入法候选窗、触摸键盘都是这个类，
        /// 而且是整屏大小，那样菜单会被无谓地推走。
        /// </summary>
        private Rectangle ShellFlyoutRect()
        {
            Rectangle best = Rectangle.Empty;
            int bestArea = 0;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!Native.IsWindowVisible(h)) return true;
                    StringBuilder sb = new StringBuilder(160);
                    Native.GetClassName(h, sb, 160);
                    string cls = sb.ToString();
                    if (cls.IndexOf("Overflow", StringComparison.OrdinalIgnoreCase) < 0 &&
                        cls.IndexOf("XamlExplorerHost", StringComparison.OrdinalIgnoreCase) < 0)
                        return true;
                    RECT r;                 // 命名空间级的结构体，不在 Native 里
                    if (!Native.GetWindowRect(h, out r)) return true;
                    int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
                    if (cw < 120 || ch < 120) return true;      // 收起状态是 0x0，忽略
                    if (cw * ch > bestArea)
                    {
                        bestArea = cw * ch;
                        best = new Rectangle(r.Left - Bounds.Left, r.Top - Bounds.Top, cw, ch);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return best;
        }

        /// <summary>菜单和 shell 面板重叠时，把菜单挪到不重叠的位置（下 → 右 → 左）。</summary>
        private void AvoidShellFlyout(ref Rectangle menu, int w, int h)
        {
            Rectangle fly = ShellFlyoutRect();
            if (fly.IsEmpty || !menu.IntersectsWith(fly)) return;

            int margin = (int)Math.Round(8 * scale);
            int maxY = ClientSize.Height - h - margin;
            int maxX = ClientSize.Width - w - margin;

            if (fly.Bottom + margin <= maxY)
            {
                menu.Y = fly.Bottom + margin;                       // 挪到面板下方
                Config.Log("菜单位置: 避开系统面板（下移）");
            }
            else if (fly.Right + margin <= maxX)
            {
                menu.X = fly.Right + margin;                        // 挪到面板右侧
                Config.Log("菜单位置: 避开系统面板（右移）");
            }
            else if (fly.Left - margin - w >= 0)
            {
                menu.X = fly.Left - margin - w;                     // 挪到面板左侧
                Config.Log("菜单位置: 避开系统面板（左移）");
            }
            else
            {
                Config.Log("菜单位置: 系统面板占满可见区域，无法避开");
            }
        }

        /// <summary>
        ///
        /// 墙从头到尾都待在桌面层没动过。以前那一堆"降 TOPMOST、复位浮层、整条链重排"
        /// 的补丁全部随着架构改变消失了。
        /// </summary>




        // ---- 菜单投影缓存（尺寸不变就复用）----
        // 只在开菜单时算一次，hover 只重画文字和图标。


        private Bitmap menuShadow;
        private int menuShadowW, menuShadowH;
        private int MenuShadowPad { get { return (int)Math.Round(24 * scale); } }

        private Bitmap MenuShadow(int w, int h)
        {
            if (menuShadow != null && menuShadowW == w && menuShadowH == h) return menuShadow;
            if (menuShadow != null) { menuShadow.Dispose(); menuShadow = null; }
            try
            {
                int pad = MenuShadowPad;
                Bitmap b = new Bitmap(w + pad * 2, h + pad * 2, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath p = RoundedRect(new RectangleF(pad, pad, w, h), Math.Max(4f, 8 * scale)))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                        g.FillPath(sb, p);
                }
                int blur = (int)Math.Round(9 * scale);
                if (blur < 3) blur = 3;
                ImageFx.BoxBlur(b, blur);
                menuShadow = b; menuShadowW = w; menuShadowH = h;
            }
            catch { menuShadow = null; }
            return menuShadow;
        }

        private void ShowControlMenu(Point at)
        {
            List<MenuEntry> items = new List<MenuEntry>();
            items.Add(MenuEntry.Item("设置…", delegate() { OpenSettings(); }));
            items.Add(MenuEntry.Item("刷新磁贴", delegate() { RefreshItems(); }));
            items.Add(MenuEntry.Check("自动排列卡片", cfg.autoArrange, delegate() { ToggleAutoArrange(); }));
            if (!cfg.autoArrange)
                items.Add(MenuEntry.Item("重置卡片位置（回到网格）", delegate() { ResetCardPositions(); }));
            items.Add(MenuEntry.Item("重新载入壁纸", delegate()
            {
                acrylicDirty = true;
                InvalidateCards();
                MarkDirtyAll();
            }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Check("隐藏 Windows 原生桌面图标", SystemIntegration.DesktopIconsHidden,
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
            // 写详细日志：平时关着（日志基本不增长），要复现 bug 时打开，就会写全量诊断
            items.Add(MenuEntry.Check("写详细日志（排查问题用）", cfg.verboseLog, delegate()
            {
                cfg.verboseLog = !cfg.verboseLog;
                Config.Verbose = cfg.verboseLog;
                cfg.Save();
                Config.Log("=== 详细日志已" + (cfg.verboseLog ? "打开" : "关闭") + " ===");
            }));
            items.Add(MenuEntry.Sep());
            items.Add(MenuEntry.Item("退出 TileDesk", delegate()
            {
                if (tray != null) tray.Visible = false;
                Application.Exit();
            }));
            OpenMenu(at, items);
        }

        // ================================================================
        //  加载桌面项目
        // ================================================================

        public void StartLoad()
        {
            ThreadPool.QueueUserWorkItem(delegate(object o) { LoadAll(); });
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

        private void LoadAll()
        {
            List<TileItem> old;
            lock (dataLock) { old = allItems; }
            List<TileItem> list = new List<TileItem>();
            foreach (string p in CollectPaths())
            {
                try
                {
                    TileItem it = new TileItem();
                    it.path = p;
                    it.name = Path.GetFileNameWithoutExtension(p);
                    if (Directory.Exists(p)) it.name = Path.GetFileName(p);
                    it.kind = KindOf(p);
                    it.stamp = File.GetLastWriteTime(p);
                    if (it.kind == "url" || it.kind == "lnk")
                    {
                        it.steamId = SteamArt.DetectAppId(p);
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

            lock (dataLock) { allItems = list; }
            // 位图缓存跟着 TileItem 走，所以被换掉的老项目要自己把缓存交出来
            if (old != null)
            {
                foreach (TileItem o in old)
                {
                    if (o == null || list.Contains(o)) continue;
                    o.DropCache();
                }
            }
            ApplyFilter();
            MarkDirtyAll();

            foreach (TileItem it in list)
            {
                try
                {
                    it.icon = ShellIcons.Get(it.iconSource, 256);
                    it.tint = ColorUtils.Dominant(it.icon, Color.FromArgb(58, 68, 96));
                    string art = ResolveLocalArt(it);
                    if (art != null)
                    {
                        it.artPath = art;
                        it.art = LoadArtCopy(art);
                        if (it.art == null) it.artPath = null;
                    }
                }
                catch (Exception ex) { Config.Log("加载图标失败 " + it.path + " : " + ex.Message); }
                MarkDirtyAll();
            }
            InvalidateCards();

            // 临时诊断：把名字里的空白字符标出来（半角空格 / 全角空格 / 不断行空格）。
            // 折行器按半角空格和标点找断点，如果名字用的是全角空格就找不到断点，
            // 只能退化成按字拆 —— 表现就是标题被从词中间切开。
            if (Config.Verbose)
            {
                foreach (TileItem it in list)
                {
                    string dbg = (it.name == null ? "" : it.name)
                        .Replace("\u3000", "<全角空格>").Replace("\u00A0", "<不断行空格>").Replace(" ", "<半角空格>");
                    Config.Log("名称: " + dbg);
                }
                // 直接把折行结果打出来，和渲染用的是同一套参数
                try
                {
                    float wrapW = cardW * 1.05f * 0.88f;    // 与渲染时 nameRect 的宽度一致
                    using (Bitmap tb = new Bitmap(4, 4))
                    using (Graphics gg = Graphics.FromImage(tb))
                    using (StringFormat sf2 = MeasureFormat())
                    {
                        foreach (TileItem it in list)
                        {
                            string w = SmartWrap(gg, it.name == null ? "" : it.name, wrapW, LabelLinesMax, sf2);
                            Config.Log("折行: " + (it.name == null ? "" : it.name) + "  ==>  " + w.Replace("\n", " ｜ "));
                        }
                    }
                }
                catch (Exception ex) { Config.Log("折行诊断失败: " + ex.Message); }
            }

            // 临时诊断：无封面卡片靠 it.icon 出图，图标为空就会只剩一片模糊壁纸
            {
                int noArt = 0, noIcon = 0, both = 0;
                foreach (TileItem it in list)
                {
                    if (it.art == null) noArt++;
                    if (it.icon == null) noIcon++;
                    if (it.art == null && it.icon == null) both++;
                }
                Config.Log("卡片素材: 共 " + list.Count + " 张，无封面 " + noArt + "，无图标 " + noIcon + "，两者都无 " + both);
            }

            if (cfg.downloadSteamArt)
            {
                List<TileItem> need = new List<TileItem>();
                // 复用的项目封面早就有了，it.art != null 会跳过 —— 所以这里天然只处理缺封面的
                foreach (TileItem it in list) if (it.steamId != null && it.art == null) need.Add(it);

                // 限制并发：2x 封面是 1200x1800，十几个同时解码峰值内存会飙到 200MB+。
                // 信号量必须活到所有工作线程结束，不要用 using 包（提前 Dispose 会让
                // 工作线程的 Release() 抛未捕获异常，直接把进程干掉）。
                foreach (TileItem it in need)
                {
                    TileItem captured = it;
                    artSlots.WaitOne();
                    ThreadPool.QueueUserWorkItem(delegate(object o)
                    {
                        try
                        {
                            string file = SteamArt.CachedPortrait(captured.steamId);
                            if (file == null) file = SteamArt.DownloadPortrait(captured.steamId);
                            if (file != null)
                            {
                                Bitmap b = LoadArtCopy(file);
                                // 注意：不能在这里直接写 captured.art —— 卡片可能已经画出来了，
                                // UI 线程正在用这些位图渲染，GDI+ 对象被两个线程同时碰会抛
                                // 「对象当前正在其他地方使用」。交给 UI 线程统一套用。
                                if (b != null) QueueArt(captured, file, b, null);
                            }
                            else
                            {
                                // 没有竖版封面：拿这个游戏的 header 图做模糊色底，
                                // 卡片就是它自己的配色 + 图标，而不是一片灰。
                                string hdr = SteamArt.DownloadHeader(captured.steamId);
                                Bitmap wash = null;
                                if (hdr != null) wash = LoadHeaderWash(hdr);
                                QueueArt(captured, null, null, wash);
                                Config.Log("无竖版封面，使用 header 色底: " + captured.name +
                                           " (appid " + captured.steamId + ") " + (wash != null ? "OK" : "也没有"));
                            }
                        }
                        catch (Exception ex) { Config.Log("补齐封面失败 " + captured.name + " : " + ex.Message); }
                        finally { artSlots.Release(); }
                    });
                }
            }
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

        /// <summary>自定义封面的稳定标识：Steam 游戏用 appid，其余用文件名。</summary>
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

        /// <summary>把横版 header 压成一个极小的模糊色块，当作卡片的配色底。</summary>
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
        /// 封面按卡片实际尺寸缩到够用的分辨率再存。
        /// 2x 封面是 1200x1800，26 张原图直接留在内存里要 200MB+。
        /// </summary>
                private Bitmap LoadArtCopy(string path)
        {
            lock (Gdi.Lock) { return LoadArtCopyRaw(path); }
        }
private Bitmap LoadArtCopyRaw(string path)
        {
            Bitmap full = LoadBitmapCopy(path);
            if (full == null) return null;
            try
            {
                int wantW = Math.Max(96, (int)(cardW * 1.3));
                if (full.Width <= wantW) return full;
                int wantH = Math.Max(96, (int)Math.Round(full.Height * (double)wantW / full.Width));
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

            // 自定义顺序：按配置里的文件名顺序排，没登记过的排到末尾
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
                // 顺序表里已经不存在（桌面图标被删）的条目清掉
                List<string> alive = new List<string>();
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TileItem it in src) names.Add(Path.GetFileName(it.path));
                foreach (string o in cfg.order) if (names.Contains(o)) alive.Add(o);
                if (alive.Count != cfg.order.Count) { cfg.order = alive; ScheduleSave(); }
            }

            lock (dataLock) { view = src; }
            Relayout();
            InvalidateCards();
        }

        // ================================================================
        //  计时器
        // ================================================================

        private void OnTick(object sender, EventArgs e)
        {
            bool need = false;

            int now = Environment.TickCount;
            float dt = (now - lastTickMs) / 1000f;
            lastTickMs = now;
            if (dt <= 0f) dt = 0.016f;
            if (dt > 0.12f) dt = 0.12f;

            float diff = scrollTarget - scroll;
            if (Math.Abs(diff) > 0.4f) { scroll += diff * Math.Min(1f, 18f * dt); need = true; }
            else if (scroll != scrollTarget)
            {
                scroll = scrollTarget;
                need = true;
                InvalidateCards();     // 滚动停下后重建，让亚克力和壁纸重新对齐
            }

            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            // 悬停动画：按时间收敛。鼠标快速划过时直接瞬切（见 snapHover），
            // 慢速移动时才是平滑过渡。
            for (int i = 0; i < snapshot.Count; i++)
            {
                float target = (i == hoverIndex) ? 1f : 0f;
                TileItem it = snapshot[i];
                if (it.hover == target) continue;
                if (snapHover) { it.hover = target; need = true; MarkDirty(TileRectOf(i)); continue; }
                // 悬停淡入淡出是**按时间**收敛的（rate 是 1/秒），和帧率无关：
                // 之前 70/40 是按 8ms 一帧调的，而 WM_TIMER 实际只有 15.6ms 一帧
                // （rate*dt > 1 被夹到 1）—— 于是淡入淡出直接变成"瞬切"，根本没有动画。
                // 30/20 在 15.6ms 一帧下分别是 ~125ms / ~200ms 的柔和过渡。
                float rate = target > it.hover ? 7f : 8f;   // 1/秒：约 430ms 淡入 / 380ms 淡出（原来 22/15 只有 135ms，肉眼看不见）
                it.hover += (target - it.hover) * Math.Min(1f, rate * dt);
                if (Math.Abs(it.hover - target) <= 0.012f) it.hover = target;
                need = true;
                MarkDirty(TileRectOf(i));   // 只重画这一张卡
            }
            if (snapHover) snapHover = false;

            // ---- 空闲淡出 ----
            // 用系统级空闲时间：键盘输入、鼠标点击/移动都会重置它。
            int idleMs = Native.SystemIdleMs();
            // 刚打开程序的那几秒不算"闲置"：系统级空闲是从**上一次系统输入**算起的，
            // 自启动/脚本拉起时上一次输入可能已经过去很久，于是程序一出现就立刻淡出。
            // 取"距程序启动的时间"和系统空闲里更小的那个，等于把启动本身当成一次活动。
            int sinceStart = unchecked(Environment.TickCount - startTick);
            if (sinceStart >= 0 && sinceStart < idleMs) idleMs = sinceStart;
            // 菜单开着时必须保持可见：否则"墙隐藏 + 空闲淡出"会让菜单画出来却完全透明
            // 频谱在动的时候也不能淡出：墙的空闲判定只看系统输入（鼠标/键盘），
            // 光放音乐不算活动 —— 于是放歌时人不动鼠标，墙会连频谱一起淡掉，
            // 看起来就是"歌曲播放时频谱直接全部消失"（用户实测）。
            // 频谱是否"有声音"必须在**每拍**都算一次，不能只在绘制时算。
            // 踩过的坑：Bands() 原来只在 DrawSpectrum 里调用，而墙淡出到 0 之后就完全
            // 停止绘制 —— 于是谁也不再更新频段值，Active 永远停在 false，墙再也醒不过来，
            // 频谱（以及整面墙）就永久消失了，直到用户动鼠标。
            if (cfg.showSpectrum) AudioSpectrum.Bands(Math.Max(8, Math.Min(128, cfg.spectrumBars)));
            bool specActive = cfg.showSpectrum && AudioSpectrum.Ready && AudioSpectrum.Active;
            fadeTarget = specActive ? 1 :
                         ((cfg.idleHideSeconds > 0.1 && idleMs >= cfg.idleHideSeconds * 1000.0) ? 0 : 1);
            if (Math.Abs(fadeLevel - fadeTarget) > 0.001f)
            {
                float step = dt / FadeSec;
                if (fadeTarget > fadeLevel) fadeLevel = Math.Min(1f, fadeLevel + step);
                else fadeLevel = Math.Max(0f, fadeLevel - step);
                needPresentOnly = true;   // 只是换透明度，不用重画
                Wake();
            }
            else if (fadeLevel != fadeTarget)
            {
                fadeLevel = fadeTarget;
                needPresentOnly = true;
            }

            // 点击水波纹
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem ri = snapshot[i];
                if (ri.ripple < 0f) continue;
                ri.ripple += dt / 0.45f;
                if (ri.ripple >= 1f) { ri.ripple = -1f; continue; }
                MarkDirty(TileRectOf(i));
                need = true;
            }

            // 拖拽退让动画：其他卡片朝自己的新槽位平滑滑过去（对标移动设备桌面）。
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
                    if (Math.Abs(d) < 0.01f) { di.animSlot = target; continue; }
                    // 收敛快一点，手感才跟得上；越接近终点越慢（缓出）
                    di.animSlot += d * Math.Min(1f, 15f * dt);
                    if (Math.Abs(target - di.animSlot) < 0.01f) di.animSlot = target;
                    Rectangle after = CardRectF(di.animSlot);

                    Rectangle dir = Rectangle.Union(before, after);
                    dir.Inflate(shadowPadPx + 2, shadowPadPx + 2);
                    MarkDirty(dir);
                    need = true;
                }
            }

            // ---- 落点驻留判定 ----
            // 拖拽经过一摞卡片时，不要让每张卡在挨到的瞬间就让位：光标要在同一个落点
            // 停留够久（DropDwellMs）才真的生效。这样划过一片卡片不会触发一串让位动画
            // （省掉大量计算），观感也稳 —— 手机桌面的拖拽就是这个逻辑。
            // 松手时会强制应用一次待定落点，所以不会出现"放下的位置和看到的不一致"。
            {
                int pc, pr;
                CellAt(dragPos, out pc, out pr);
                if (pc != pendDropCol || pr != pendDropRow)
                {
                    pendDropCol = pc; pendDropRow = pr;
                    dropDwellStart = unchecked(Environment.TickCount);
                }
                int pi = InsertIndexAt(dragPos);
                if (pi != pendDropIndex)
                {
                    pendDropIndex = pi;
                    dropDwellStart = unchecked(Environment.TickCount);
                }
            }

            // 拖动中：落点驻留够久就应用（上面那段只更新了"待定落点"）
            if (dragging) ApplyPendingDrop(false);

            // 自由排列的让位 / 落位动画（二维格子，和上面的 animSlot 那套并存）
            if (!cfg.autoArrange)
            {
                freeAnimBusy = false;
                StepFreeAnim(snapshot, dt);
                if (freeAnimBusy) need = true;
            }

            // 启动反馈动画：约 0.45 秒内从 1 衰减到 0
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem li = snapshot[i];
                if (li.launchAnim <= 0f) continue;
                li.launchAnim -= dt / 0.45f;
                if (li.launchAnim < 0f) li.launchAnim = 0f;
                MarkDirty(TileRectOf(i));
                need = true;
            }

            // ★ 滚动期间不重烤卡片缓存。
            //   卡片底是"按屏幕位置从壁纸里取样"烤出来的，滚动时每张卡的位置都在变，
            //   烤出来的下一秒就过期 —— 结果整个滚动过程都在重烤（用户实测："卡片放很大
            //   之后滚动非常卡"），而且每张卡的底跟着卡片移动、和背后静止的壁纸对不上，
            //   看上去就像"背景一直在变"。
            //   滚动停下时统一重建（见上面那段 InvalidateCards），滚动过程中沿用现有缓存，
            //   虽然和壁纸有轻微错位，但画面完全静止、不抖也不卡。
            if (scroll == scrollTarget && CachesIncomplete())
            {
                EnsureCaches(4);
                need = true;
            }

            if (dirty) { dirty = false; need = true; }

            if (demoHover >= 0) hoverIndex = demoHover;

            // 启动提示已按用户要求全部去掉：
            //  · 低完整性警告（"在工作区里跑"）—— 用户明确说不会在工作区里给别人用；
            //  · 首次运行引导 —— 同上，不需要。
            // 启动后直接进正常流程。

            tickCount++;
            ApplyPendingArt();
            // 「正在播放」控件现在是**独立的分层窗口**（见 NowPlayingWidget.cs）：
            // 它自己画、自己上传，磁贴墙完全不参与它的渲染。
            if (npWindow != null)
            {
                Point npLoc = PointToScreen(new Point(
                    (int)Math.Round(cfg.npMargin * scale),
                    ClientSize.Height - (int)Math.Round(cfg.npMargin * scale) - npWindow.Height));
                // 菜单开着时墙被临时提到 TOPMOST 画菜单，这时候控件**绝不能**再重申层级，
                // 否则它会骑到菜单上面（用户实测：菜单底部被时钟/播放器盖住）。
                // 两个浮层都不再自己动层级：`SetWindowPos(墙, 浮层)` 会把**墙**拖到浮层的高度，
                // 而墙是卡片墙 —— 一拖就盖住所有程序。层级的整体排布统一由
                // PlaceAboveDesktop()（每 128 tick 的 KeepAboveDesktop 或菜单开关时触发）负责。
                npWindow.Sync(Handle, npLoc, false, fadeLevel);
            }
            if (nowPlaying != null && (tickCount % 32) == 0) nowPlaying.DrainRetired();
            // 右下角常驻信息条：内容变化自己判断，没变不重绘；位置随布局走
            if (deskInfo != null && !deskInfo.IsDisposed)
            {
                Rectangle dr = DeskInfoRect();
                if (!dr.IsEmpty)
                    deskInfo.Sync(Handle, PointToScreen(new Point(dr.X, dr.Y)), false);   // 同上：不自己抢层级
            }
            // ---- 幽灵状态自愈 ----
            // 真实左键早松开了，但 pressCandidate / dragging 还挂着 —— 说明那次"抬起"
            // 消息丢了（切换窗口、被别的窗口抢走、或者按住时触发了边缘手势）。
            // 这种残留会让**之后任何一次抬起**都被当成"点中了那张卡片"，
            // 表现就是"鼠标没动，游戏自己开了"（用户实测报的偶发问题）。
            // 每帧对一下真实按键状态，一松开就立刻清干净、并把鼠标捕获放掉。
            if ((pressCandidate || dragging || edgeArmed) && !Native.KeyDown(Native.VK_LBUTTON))
            {
                if (pressCandidate || dragging || edgeArmed)
                {
                    pressCandidate = false; pressIndex = -1;
                    edgeArmed = false;
                    if (dragging)
                    {
                        // ★ 拖拽中的"自愈"必须**走 EndDrag 落位**，不能直接清状态 ——
                        //   直接清掉等于放弃这次拖动，卡片会弹回原处（用户实测的"回弹"）。
                        //   这段自愈本来是为"幽灵点击"加的（防止误触把程序打开），
                        //   对拖拽中的情形处理错了：它跑在按键兜底之前，兜底因此永远没机会执行。
                        if (Config.Verbose) Config.Log("拖拽自愈: 左键已松开 → 落位");
                        EndDrag(true);
                    }
                    try { Native.ReleaseCapture(); } catch { }
                }
            }
            // 频谱条：有声音时保持重绘（否则墙空闲会淡出，频谱就不动了）
            // 被别的窗口完全盖住时不要再为频谱保持 60fps：
            // 墙是桌面层窗口，它并不知道自己看不见，否则纯烧 CPU（用户实测问到这点）。
            spectrumOn = false;
            bool covered = WallCovered();
            bool specOn = cfg.showSpectrum && AudioSpectrum.Ready && AudioSpectrum.Active && !covered;
            spectrumOn = specOn;   // 给下面的 tick 间隔判定用
            // 频谱要跟音乐动，必须把系统计时器精度提到 1ms：
            // WM_TIMER 默认只有 ~15.6ms 粒度，墙实际只跑十几到几十 fps（这就是"采样率低"的真凶）
            Native.SetFastTimer(specOn);
            if (specOn)
            {
                if (!spectrumRect.IsEmpty) MarkDirty(spectrumRect);
                else MarkDirtyAll();
                Wake();
                // 诊断：每 2 秒报一次状态和几根柱子的高度（只在详细日志开启时写）
                if (tickCount % 128 == 0 && Config.Verbose)
                {
                    // ★ 这里**绝对不能**再调 AudioSpectrum.Bands(别的柱子数)。
                    //   柱子数量一变，bands 数组会被重新分配并清零 ——
                    //   于是每隔一个诊断周期（2 秒）频谱就被清空一次，
                    //   看起来就是"显示几秒后自己消失"（用户实测，就是这么来的）。
                    Config.Log("频谱状态: ready=" + AudioSpectrum.Ready + " 淡出=" + fadeLevel.ToString("0.00") +
                               " 可见=" + Visible + " 有声音=" + AudioSpectrum.Active +
                               " 柱数=" + Math.Max(8, Math.Min(128, cfg.spectrumBars)));
                }
            }

            // 诊断：拖拽中每秒报一次状态；以及检测"某一拍卡住多久"
            if (Config.Verbose)
            {
                int nowMs = unchecked(Environment.TickCount);
                if (diagTickMs != 0)
                {
                    int cost = unchecked(nowMs - diagTickMs);
                    if (cost > 300) Config.Log("卡顿: 两拍之间隔了 " + cost + " 毫秒（拖拽中=" + dragging + "）");
                }
                diagTickMs = nowMs;
            }

            // ★ 兜底：拖拽中若左键已经物理松开，立刻落位。
            //   上面那条只能覆盖"还在动鼠标"的情况；松手后手不动就没有任何鼠标消息，
            //   这条按物理按键状态补上。
            if (dragging && !Native.KeyDown(Native.VK_LBUTTON)) EndDrag(true);
            if (tickCount % 64 == 0) EnsurePinned();
            if (tickCount % 128 == 0) KeepAboveDesktop();
            // 菜单开着时定期重申 TOPMOST 的那段代码已删除 ——
            // 留着它反而会把整面卡片墙又顶到所有窗口之上（那正是之前的 bug）。



            if (saveDueMs != 0 && unchecked(Environment.TickCount - saveDueMs) >= 0)
            {
                saveDueMs = 0;
                cfg.Save();
            }
            if (watchDueMs != 0 && unchecked(Environment.TickCount - watchDueMs) >= 0)
            {
                watchDueMs = 0;
                Config.Log("桌面有变化，自动刷新");
                RefreshItems();
            }

            // 本 tick 是不是"只换透明度"。必须在下面把它取走之前记下来 ——
            // 末尾算帧率时还要用（之前就是被取走了才导致淡出仍然按 120ms 跑）。
            bool fadingThisTick = needPresentOnly;

            if (need)
            {
                // 取走脏区：Empty = 整窗，否则只重画那一块。
                // 取走之后再发生的 MarkDirty 归下一帧。
                Rectangle region = dirtyFull ? Rectangle.Empty : dirtyRect;
                dirtyRect = Rectangle.Empty;
                dirtyFull = false;  // 下一帧从"非整窗"开始，这样 MarkDirty 才生效
                dirty = false;
                Render(region);
            }
            else if (fadingThisTick)
            {
                needPresentOnly = false;
                // 画面没变，只是整体透明度变了 —— 所以只把"真有内容"的那一块重新
                // 交给 DWM。整块画布其余像素本来就是 alpha=0，透明度怎么变都看不见，
                // 没必要每帧重新上传 3840x2064。
                long fp0 = Stopwatch.GetTimestamp();
                Present(FadePresentRegion());
                double fadeMs = (Stopwatch.GetTimestamp() - fp0) * 1000.0 / Stopwatch.Frequency;
                // 淡出帧也要进统计：以前它既不做脏区重绘、也不进"渲染性能"，等于完全隐形。
                npRenderFrames++; npFadeFrames++;
                npRenderMs += fadeMs;
                npRenderPresentMs += fadeMs;
                StatsTick();
            }

            // 空闲时降低计时器频率；一旦要动就立刻提到最高帧率。
            // 注意：这对动画的"跟手"感觉至关重要 —— 空闲 120ms 的轮询会让鼠标
            // 移到新卡片后最多等 120ms 动画才开始，高 DPI 快速滑动时就是明显的滞后。
            // 以前这里是"轮询鼠标按键、点到菜单外面就关闭"的补丁 —— 因为菜单画在墙上，
            // 并且自己 SetCapture，外面点的任何一下都会作为消息送到它那里，
            // 由它负责"关闭 + 把这一击转发给下面的窗口"。补丁删掉：
            // 留着反而会误伤 —— 用户按住鼠标时会被判成"点了外面"，菜单莫名其妙就关了。

            // 空闲时降低计时器频率，一旦有东西在动就提到最高帧率。
            //
            // fadingThisTick 必须算进"在动"：空闲淡入淡出只换整体透明度、不重画
            // 任何像素，以前它不算在动，于是 0.45 秒的淡出仍然按 120ms 的空闲轮询走
            // —— 整个淡出只有三四帧，看起来就是一顿一顿的。
            //
            // 这里一律用 8：WM_TIMER 的粒度是系统计时器周期（15.6ms），请求 16ms 反而
            // 会因为跨不过那个边界而退化成 ~31ms（32fps），请求 8ms 才能稳定拿到
            // 每个系统 tick 一帧（≈64fps）。淡出帧很便宜（只上传有内容的那一块）。
            // spectrumOn 必须算进来：频谱每帧都在变，但卡片是静止的，
            // 不算的话 tick 会被压回 120ms —— 频谱就只剩 ~8fps（用户实测："像帧率低"）。
            bool animating = need || fadingThisTick || CachesIncomplete() || spectrumOn;
            int want = animating ? 8 : 120;
            if (tick.Interval != want) tick.Interval = want;
        }

        // ================================================================
        //  鼠标：直接处理窗口消息
        //
        //  .NET Framework 的 DpiHelper 不认 system-DPI-aware 清单，会把消息坐标
        //  按缩放倍数再乘一遍（192dpi 下 5,7 变成 10,14），MouseEventArgs 不可用。
        // ================================================================

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_CAPTURECHANGED = 0x0215;

        private bool trackingLeave = false;

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

            // 拖拽中：卡片跟着光标走，顺便算落点
            if (dragging)
            {
                // ★ 兜底：松手消息（WM_LBUTTONUP）可能没送到本窗口 ——
                //   拖拽期间如果有别的窗口抢走鼠标捕获，那条消息就落到别的窗口去了，
                //   墙永远不知道已经松手，卡片就"回弹"（用户实测：日志里只有"拖拽开始"
                //   没有"拖拽松手"）。这里用移动消息里带的按键状态兜底。
                if (!leftDown)
                {
                    EndDrag(true);
                    return;
                }
                dragPos = pt;
                if (cfg.autoArrange)
                {
                    int di = InsertIndexAt(pt);
                    // ★ 这里**不能**因为插入位置变化就重置驻留计时：
                    //   光标在卡片左右半边之间移动会让 di 在 ±1 之间反复跳，
                    //   计时被反复清零 → 目标虚框迟迟不出现（用户实测"拖到位置上有明显延迟"）。
                    //   只有**格子**变化才重置计时。
                    pendDropIndex = di;
                    ApplyPendingDrop(false);
                }
                else
                {
                    int c, r;
                    CellAt(pt, out c, out r);
                    // 只记下"待定落点"，不立刻让位 —— 驻留判定交给 ApplyPendingDrop：
                    // 光标在同一格停够 DropDwellMs 才真的生效（快速划过不触发）。
                    if (c != pendDropCol || r != pendDropRow)
                    {
                        pendDropCol = c; pendDropRow = r;
                        dropDwellStart = unchecked(Environment.TickCount);
                    }
                    ApplyPendingDrop(false);
                }
                MarkDirtyAll();
                Wake();
                return;
            }

            int i = IndexAt(pt);
            if (mouseLogs < 4)
            {
                mouseLogs++;
                Config.Log("鼠标移动 client=(" + pt.X + "," + pt.Y + ") -> 索引 " + i);
            }

            bool overCtrl = cfg.showControlButton && !ctrlRect.IsEmpty && ctrlRect.Contains(pt);
            bool overTrash = cfg.showControlButton && !trashRect.IsEmpty && trashRect.Contains(pt);
            if (overCtrl != ctrlHover) { ctrlHover = overCtrl; MarkDirty(ctrlRect); }
            if (overTrash != trashHover) { trashHover = overTrash; MarkDirty(trashRect); }

            if (overCtrl || overTrash)
            {
            if (hoverIndex != -1) { MarkDirty(TileRectOf(hoverIndex)); hoverIndex = -1; }
                Cursor = Cursors.Hand;
                Wake();
                return;
            }

            // 按住左键拖过阈值 → 进入拖拽重排
            // ---- 右边缘左滑 -> 唤起通知中心 ----
            // 这是 Win8/Win10 自带的边缘手势，Win11 砍掉了，所以在这里补回来。
            // 必须在拖拽判定**之前**：否则会先进入卡片拖拽态。
            if (edgeArmed && leftDown)
            {
                if (pressAt.X - pt.X >= SwipeDist)          // 往左拖够了距离就触发
                {
                    edgeArmed = false;                      // 一次手势只触发一次
                    Native.OpenNotificationCenter();
                    Config.Log("边缘手势: 右边缘左滑 -> 唤起通知中心" + (Native.IsWindows11 ? "（Win+N）" : "（Win+A）"));
                }
                return;                                     // 起手在边缘，不做卡片拖拽
            }

            if (pressCandidate && pressIndex >= 0 && leftDown)
            {
                int ddx = Math.Abs(pt.X - pressAt.X);
                int ddy = Math.Abs(pt.Y - pressAt.Y);
                if (ddx > TapSlop || ddy > TapSlop)   // 触摸容差：小抖动不算拖拽
                {
                    StartDrag(pressIndex, pt);
                    return;
                }
            }

            // 高 DPI 鼠标快速划过时，两次事件之间可能跨过好几张卡。
            // 这种情况直接瞬切，不要拖一串半透明的尾巴出来。
            if (!lastMouse.IsEmpty)
            {
                int dx = Math.Abs(pt.X - lastMouse.X);
                int dy = Math.Abs(pt.Y - lastMouse.Y);
                if (dx > cardW || dy > cardH) snapHover = true;
            }
            lastMouse = pt;

            if (i != hoverIndex)
            {
                if (hoverIndex >= 0) MarkDirty(TileRectOf(hoverIndex));
                if (i >= 0) MarkDirty(TileRectOf(i));
                hoverIndex = i;
                Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
                Wake();
            }
        }

        private void ClearHover()
        {
            trackingLeave = false;
            if (hoverIndex != -1)
            {
                MarkDirty(TileRectOf(hoverIndex));
                hoverIndex = -1;
                Cursor = Cursors.Default;
                Wake();
            }
        }

        // ---------------- 拖拽：自动排列 = 重排顺序；自由排列 = 挪位置 ----------------

        private void StartDrag(int index, Point pt)
        {
            // 退让动画的起点：所有卡片从自己"现在"的槽位开始
            BeginDragAnim(index);
            dragging = true;
            dragIndex = index;
            dragPos = pt;
            if (cfg.autoArrange)
            {
                dropIndex = InsertIndexAt(pt);
                pendDropIndex = dropIndex;      // 待定落点同步初始化，避免沿用上一轮的旧值
                RebuildDragPreview();
            }
            else
            {
                // 自由排列：不做"插入"预览，但同样有让位动画 ——
                // 落点上已经有的那张卡会平滑让到被拖卡原来的格子去。
                dropIndex = -1;
                dragPreview = null;
                CellAt(pt, out dropCol, out dropRow);
                pendDropCol = dropCol; pendDropRow = dropRow;   // 同上：消除首帧旧值
                BeginFreeDragAnim(index);
            }
            selIndex = -1;
            hoverIndex = -1;
            Cursor = Cursors.SizeAll;
            try { Native.SetCapture(Handle); } catch { }   // 保证在窗口外松手也收得到 mouse-up
            if (Config.Verbose)
            {
                int vc; lock (dataLock) { vc = view.Count; }
                Config.Log("拖拽开始: index=" + index + " 视图数=" + vc + " 自动排列=" + cfg.autoArrange +
                           " 卡片=" + (index >= 0 && index < vc ? view[index].name : "?"));
            }
            MarkDirtyAll();
            Wake();
        }

        /// <summary>
        /// 计算拖拽中的预览顺序：把被拖的卡片抽出来插到落点，
        /// 其余卡片依次让位 —— 就是移动设备桌面那种「自动退让」。
        /// </summary>
        /// <summary>拖拽开始：所有卡片把当前位置记成 animSlot，之后朝目标槽位平滑滑过去。</summary>
        private void BeginDragAnim(int from)
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].animSlot = i;
                snapshot[i].hover = 0f;
            }
            dragAnimFrom = from;
        }

        /// <summary>拖拽结束 / 取消：把动画状态清掉（卡片回到自己的真实槽位）。</summary>
        private void ResetDragAnim()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++) snapshot[i].animSlot = -1f;
        }

        private int dragAnimFrom = -1;

        // ---- 自由排列的让位/落位动画（二维格子版）----
        private int freeSwapIndex = -1;
        private const int DropDwellMs = 40;   // 40ms ≈ 2~3 帧：既不会被"划过一摞卡片"触发一串让位，也看不出延迟   // 落点要驻留多久才让位（手机桌面的手感）
        private int dropDwellStart = 0;        // 当前落点首次出现的时刻
        private int pendDropCol = -2, pendDropRow = -2, pendDropIndex = -2;  // 待定落点                  // 落点上那张"要让位"的卡
        private int dragFromCol = -1, dragFromRow = -1;  // 被拖卡原来的格子（让位目标）

        /// <summary>自由排列拖拽开始：所有卡片从自己"现在"的格子起动画。</summary>
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

        /// <summary>
        /// 把"待定落点"应用成实际落点（带驻留判定）。
        ///
        /// 光标在同一个落点停留够 DropDwellMs 才生效；快速划过一摞卡片时一路都不触发
        /// （省掉一串让位动画，观感也稳 —— 手机桌面的拖拽就是这个逻辑）。
        /// 松手前会强制调一次（force = true），保证放下的位置和看到的一致。
        /// </summary>
        private void ApplyPendingDrop(bool force)
        {
            if (!dragging) return;
            bool dwellOk = force || unchecked(Environment.TickCount - dropDwellStart) >= DropDwellMs;

            // ★ 两件事分开：
            //   ① **落点位置（也就是目标虚框）立刻跟手** —— 它只是高亮，不该等驻留确认。
            //      以前这里整体被驻留卡住：光标连续拖动时每跨一格就重置计时，
            //      虚框因此永远落后光标好几格（用户实测："拖到位置上有明显延迟才显示虚框"）。
            //   ② **让位动画**（其他卡片重排预览 / 让位者认领）才等驻留 ——
            //      它才是"快速划过一摞卡片不触发一串动画"要防抖的东西。
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

        /// <summary>
        /// 落点上已经有的那张卡改成"让到被拖卡原来的格子"（没有就让回自己格子）。
        ///
        /// <summary>
        /// 落点上已经有的那张卡改成"让到被拖卡原来的格子"（没有就让回自己格子）。
        ///
        /// 用 `cellOwner` 表反查落点格上是谁 —— 这是最早一直能工作的写法。
        /// 后来试过改"按格坐标扫描"和"按绘制矩形命中"，两次都引入了新问题
        /// （找不到人 / 让位者来回跳），所以回到这里，并配上落点驻留判定来减少抖动。
        /// </summary>
        private void UpdateFreeSwapTarget()
        {
            int owner;
            freeSwapIndex = (dropCol >= 0 && cellOwner.TryGetValue(CellKey(dropCol, dropRow), out owner) &&
                             owner != dragIndex) ? owner : -1;
        }

        /// <summary>松手/取消：被拖的那张从光标位置滑回（或滑到）它的格子，而不是瞬间跳。</summary>
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

        /// <summary>自由排列的动画每一帧推进一步（缓出），并标脏受影响的那几张卡。</summary>
        private void StepFreeAnim(List<TileItem> snapshot, float dt)
        {
            for (int i = 0; i < snapshot.Count && i < cellCol.Length; i++)
            {
                TileItem it = snapshot[i];
                // 格坐标无效的卡片不参与动画：算出 -1 会跑到屏幕外，看起来就是"卡片消失"。
                if (cellCol[i] < 0 || cellRow[i] < 0) continue;
                // 没有在动画中的卡片：若这一帧它的目标格已经和它自己的格不同
                // （典型情形是刚被指定成"让位者"），就从它当前所在的格补一段动画。
                // 不补的话它会被下面的 continue 直接跳过，表现成"跳过动画瞬间归位" ——
                // 正是用户实测报的"拖拽经过时有些卡片跳过动画直接回到原地"。
                if (it.animCol < 0f)
                {
                    bool needMove = dragging && i == freeSwapIndex &&
                                    (Math.Abs(dragFromCol - cellCol[i]) > 0.01f ||
                                     Math.Abs(dragFromRow - cellRow[i]) > 0.01f);
                    if (!needMove) continue;
                    it.animCol = cellCol[i];
                    it.animRow = cellRow[i];
                }
                if (dragging && i == dragIndex) continue;      // 被拖的那张跟着光标画
                float tc = cellCol[i], tr = cellRow[i];
                if (dragging && i == freeSwapIndex) { tc = dragFromCol; tr = dragFromRow; }

                float dc = tc - it.animCol, dr = tr - it.animRow;
                if (Math.Abs(dc) < 0.01f && Math.Abs(dr) < 0.01f)
                {
                    it.animCol = tc; it.animRow = tr;
                    if (!dragging) { it.animCol = -1f; it.animRow = -1f; }   // 收敛完交回静态位置
                    continue;
                }
                Rectangle before = CellRectF(it.animCol, it.animRow);
                it.animCol += dc * Math.Min(1f, 15f * dt);
                it.animRow += dr * Math.Min(1f, 15f * dt);
                Rectangle after = CellRectF(it.animCol, it.animRow);

                Rectangle dir = Rectangle.Union(before, after);
                dir.Inflate(shadowPadPx + 2, shadowPadPx + 2);
                MarkDirty(dir);
                freeAnimBusy = true;
            }
        }
        private bool freeAnimBusy = false;

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

        /// <summary>把光标位置换算成「插入到第几个位置」。</summary>
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
            if (relX - col * cellX > cardW / 2) idx++;   // 落在卡片右半边就插到它后面
            return Math.Max(0, Math.Min(count, idx));
        }

        private bool DragOverTrash()
        {
            return dragging && !trashRect.IsEmpty && trashRect.Contains(dragPos);
        }

        private void EndDrag(bool commit)
        {
            int from = dragIndex;
            if (Config.Verbose)
            {
                int vc; lock (dataLock) { vc = view.Count; }
                Config.Log("拖拽松手: commit=" + commit + " from=" + from + " 视图数=" + vc +
                           " 落点=(" + dropCol + "," + dropRow + ") 待定=(" + pendDropCol + "," + pendDropRow + ")" +
                           " 待定插入=" + pendDropIndex + " to=" + dropIndex +
                           " 光标=" + dragPos.X + "," + dragPos.Y + " 垃圾桶=" + DragOverTrash());
            }
            // ★ 松手时以光标当前位置重算落点：驻留判定只决定预览动画要不要让位，
            //   不该影响最终结果。之前只把"待定落点"强制应用一次，快速拖动时那个值
            //   可能还是旧的，于是按旧落点归位 —— 表现就是"拖到目标马上松手会失效"。
            if (dragging && !DragOverTrash())
            {
                CellAt(dragPos, out pendDropCol, out pendDropRow);
                pendDropIndex = InsertIndexAt(dragPos);
            }
            // 松手前把待定落点强制应用 —— 否则"驻留还没到"的那一次拖动会按旧落点归位，
            // 和用户看到的位置对不上。
            ApplyPendingDrop(true);
            int to = dropIndex;
            bool onTrash = DragOverTrash();

            // 自由排列：无论落位还是取消，都让被拖的那张从光标处滑回去，
            // 而不是瞬间跳（这就是"松手那一下"的动画）。
            if (!cfg.autoArrange && from >= 0 && !onTrash) SettleDraggedFromCursor();

            // ================================================================
            //  顺序很重要：必须**先把新顺序落到 view 上**，再清拖拽状态。
            //
            //  以前的写法是先清 dragging / dragPreview / animSlot，最后才 ApplyFilter()。
            //  那中间渲染计时器（8ms）随时可能插一帧 —— 那时 dragging 已经是 false、
            //  走的是"按 view 下标画"的普通分支，可 view 还是旧顺序，
            //  于是卡片先闪回原位，等顺序生效后再跳到新位置。
            //  cfg.Save() 是同步文件 IO，还把这段窗口拉得更长，所以挪到最后。
            //
            //  （2026-10-04 补：还有第二个"闪回原位"的来源 —— 卡片位图缓存以前是按
            //   槽位存的，重排之后数组里还是旧顺序的图，松手那一帧整面墙就用旧顺序
            //   重画了。现在缓存挂在 TileItem 上，重排不会再串图。）
            // ================================================================
            List<TileItem> reordered = null;
            TileItem movedItem = null;
            int finalSlot = -1;
            bool freeMoved = false;
            int freeCol = 0, freeRow = 0;

            if (!commit || from < 0 || onTrash)
            {
                if (Config.Verbose) Config.Log("拖拽未落位: commit=" + commit + " from=" + from + " 垃圾桶=" + onTrash);
            }
            if (commit && from >= 0 && !onTrash)
            {
                List<TileItem> snap;
                lock (dataLock) { snap = new List<TileItem>(view); }
                if (from < snap.Count)
                {
                    if (!cfg.autoArrange)
                    {
                        // 自由排列：把卡片放到落点格子上；格子上已经有别的卡就两张交换
                        // （和桌面上把图标拖到别人身上时的直觉一致）。
                        DropCell(out freeCol, out freeRow);
                        TileItem moved = snap[from];
                        if (Config.Verbose) Config.Log("自由落位: 目标格=(" + freeCol + "," + freeRow + ") 卡片=" + moved.name);
                        // 交换对象走 cellOwner（和 UpdateFreeSwapTarget 同一个来源，保持一致）
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
                        AssignCells();          // 位置立刻生效：下一帧画的就是新位置
                        SavePositions();
                        Relayout();
                        InvalidateCardsPositionSensitive();
                    }
                    else if (to >= 0 && !(to == from || to == from + 1))
                    {
                        // 自动排列：原地放下不算重排，别改排序方式
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
                ApplyFilter();     // 顺序立刻生效：下一帧画的就是新位置，不会闪回
            }

            // 顺序已经一致了，现在才清拖拽状态
            dragging = false; dragIndex = -1; dropIndex = -1; dragPreview = null;
            dropCol = -1; dropRow = -1;
            ResetDragAnim();
            pressCandidate = false; pressIndex = -1;
            Cursor = Cursors.Default;
            try { Native.ReleaseCapture(); } catch { }
            MarkDirtyAll();

            if (!commit) return;
            if (onTrash && from >= 0) { DeleteToRecycleBin(from); return; }

            if (freeMoved)
            {
                cfg.Save();
                Config.Log("卡片位置: " + movedItem.name + " -> 第 " + (freeRow + 1) + " 行第 " + (freeCol + 1) + " 列");
            }
            else if (reordered != null)
            {
                cfg.Save();        // 文件 IO 放到视觉状态一致之后，免得又拉长那一帧的窗口
                Config.Log("拖拽重排: " + movedItem.name + " -> 第 " + (finalSlot + 1) + " 位");
            }
        }

        /// <summary>
        /// 位置变了：封面卡片的位图内容与屏幕位置无关（不用重建），只有"亚克力"
        /// 卡片要从新位置取壁纸色，所以只把没封面的那几张标记成待重建 —— 而且是在
        /// 下一帧之后慢慢补，绝不阻塞拖拽。
        /// </summary>
        private void InvalidateCardsPositionSensitive()
        {
            List<TileItem> snapshot;
            lock (dataLock) { snapshot = view; }
            for (int i = 0; i < snapshot.Count; i++)
            {
                TileItem it = snapshot[i];
                if (it != null && it.art == null && it.cacheNormal != null) it.cacheEpoch = cacheEpoch - 1;
            }
        }

        /// <summary>把桌面快捷方式丢进回收站，然后重新扫描（卡片自动补位）。</summary>
        private void DeleteToRecycleBin(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            // 不弹确认：进回收站随时能还原，没必要多问一句
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

        /// <summary>
        /// 重命名这张卡片对应的桌面文件/快捷方式。
        ///
        /// 只改名字、不动扩展名（快捷方式必须保留 .lnk 否则双击就跑不起来），
        /// 并且把 `positions` 里以文件名为键的那条位置记录一起改名 ——
        /// 不改的话卡片位置会丢，下次启动它就跑到默认格去了。
        /// </summary>
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
            if (input == null) return;                       // 取消
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

            // 位置记录是按文件名做键的，名字变了要一起搬过去
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

        /// <summary>
        /// 永久删除（不进回收站，删了找不回来）。默认要点确认，菜单里也是红字。
        /// </summary>
        private void DeletePermanently(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }
            if (it == null || string.IsNullOrEmpty(it.path)) return;

            if (MessageBox.Show(
                    "永久删除？",
                    "TileDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
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

        private int IndexAt(Point pt)
        {
            int relX = pt.X - gridLeft;
            int relY = pt.Y - gridTop - gridTopPad + (int)Math.Round(scroll);
            if (relX < 0 || relY < 0) return -1;

            // 注意：列距是 cardW+gap，行距是 cardH+gap —— 卡片不是正方形，混用会让
            // 越往下偏得越多（鼠标在第二排会选到第三排）。
            int cellX = cardW + gapPx;
            int cellY = cardH + gapPx;
            int col = relX / cellX;
            int row = relY / cellY;
            if (relX - col * cellX > cardW) return -1;   // 落在卡片之间的缝里
            if (relY - row * cellY > cardH) return -1;
            // 自由排列时格子是稀疏的（可能是空格），所以查表而不是算下标
            int idx;
            if (cellOwner.TryGetValue(CellKey(col, row), out idx))
                return (idx >= 0 && idx < cellCol.Length) ? idx : -1;
            return -1;
        }

        /// <summary>Ctrl+滚轮 缩放卡片（按 2:3 保持比例），防抖后写进配置。</summary>
        private void ZoomCards(int dir)
        {
            // 步长要细：以前是 cardWidth/10（10%），滚一格跳一大截。现在按 2.5%、最小 2px，
            // 滚轮可以一点点调（每格只动 2~3 个逻辑像素）。
            int step = Math.Max(2, (int)Math.Round(cfg.cardWidth * 0.025));
            int w = cfg.cardWidth + dir * step;
            w = Math.Max(70, Math.Min(360, w));
            if (w == cfg.cardWidth) return;
            cfg.cardWidth = w;
            cfg.cardHeight = (int)Math.Round(w * 1.5);
            RecomputeMetrics();
            InvalidateCards();
            ScheduleSave();
            Wake();
            MarkDirtyAll();
            Config.Log("缩放卡片: " + cfg.cardWidth + "x" + cfg.cardHeight);
        }

        private void ScheduleSave()
        {
            saveDueMs = unchecked(Environment.TickCount + 1200);
        }

        /// <summary>
        /// 垃圾桶按钮 → 弹出菜单。
        /// 之所以不是"点一下直接开回收站"：这台机器上 shell 开窗口这条路被挡住了
        /// （SHOpenFolderAndSelectItems 返回 0x80070005，explorer.exe 0xc0000142），
        /// 所以把"不需要开窗口"的操作用户（清空回收站）放在同等重要的位置，
        /// 并且先把回收站里有几项摆出来，至少按钮是有信息量的。
        /// </summary>
        private void ShowTrashMenu(Point at)
        {
            long items = 0, bytes = 0;
            bool ok = Native.QueryRecycleBin(out items, out bytes);

            List<MenuEntry> list = new List<MenuEntry>();
            MenuEntry head = MenuEntry.Item(
                ok ? ("回收站：" + items + " 项 · " + FormatSize(bytes)) : "回收站：不可用", null);
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
            // 千万不要自己 Process.Start("explorer.exe", "shell:RecycleBinFolder")。
            // 那会用 CreateProcess 新起一个 explorer.exe，它在不少环境下会初始化失败：
            // 弹「explorer.exe - 应用程序无法正常启动(0xc0000142)」（DLL 初始化失败）。
            // 正确做法是把 shell: moniker 交给 shell，由正在运行的 shell 打开回收站，
            // 全程不新起进程（ShellExecuteEx 成功时也不会返回进程）。
            // 注意别用 "::{645FF040-...}" 这种 GUID 路径：ShellExecuteEx 会把它当成
            // 普通文件路径，而且还返回「成功」，随后 shell 自己弹一个
            // 「Windows 无法访问指定设备、路径或文件」—— 又错又难排查。
            // 关键在 UseShellExecute = true —— 让 shell 去启动 explorer.exe。
            //
            // 默认的 UseShellExecute = false 是「我们自己 CreateProcess 拉一个 explorer.exe」，
            // 在受限环境下会 0xc0000142（STATUS_DLL_INIT_FAILED），弹
            // 「explorer.exe - 应用程序无法正常启动」。
            //
            // 实测对比（同一台机器）：
            //   explorer.exe + UseShellExecute=true   -> ✅ 可见窗口开出来
            //   Shell.Application COM InvokeVerb       -> ❌ 返回成功但窗口不显示
            //   SHGetKnownFolderIDList + ShellExecuteEx-> ❌ 拒绝访问
            // 所以 shell 启动优先，COM/PIDL 只做兜底。
            // 不再优先用 explorer.exe：受限环境里它会 0xc0000142 弹系统错误框。
            // Native.OpenRecycleBin 内部走 SHOpenFolderAndSelectItems(PIDL)，两种环境都能用。
            int err = Native.OpenRecycleBin(Handle);
            if (err == 0) return;

            Config.Log("打开回收站失败: err=0x" + err.ToString("X8"));
            MessageBox.Show(
                "打不开回收站（系统返回 0x" + err.ToString("X8") + "）。\n\n" +
                "当前运行环境：" + Native.DescribeEnvironment() + "\n\n" +
                "如果上面的完整性级别低于 0x2000，说明 TileDesk 是从沙箱、终端或 IDE 一类\n" +
                "受限环境里启动的 —— 这种环境下系统不允许打开资源管理器窗口。\n\n" +
                "解决办法：从桌面直接双击 TileDesk.exe 运行，回收站和「打开文件所在位置」就都正常了。",
                "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================================================================
        //  托盘菜单
        //
        //  菜单是独立的 WPF 窗口（WpfMenu），不用 WinForms 的 ContextMenuStrip。
        //  本程序主窗口「永远不激活」（WM_MOUSEACTIVATE 返回 MA_NOACTIVATE），
        //  ContextMenuStrip 拿不到前台身份就点不动 —— 实测弹菜单时 SetForegroundWindow
        //  也抢不到前台（前台仍是任务栏的溢出面板）。
        //  右键菜单画在我们自己的分层窗口里，命中判断走自己那套，和卡片菜单同一条路。
        // ================================================================

        /// <summary>
        /// 托盘右键菜单。托盘图标在任务栏上（我们窗口之外），
        /// 所以把菜单夹到窗口内最靠近光标的那个位置，看起来仍是从托盘图标下面掉出来的。
        /// </summary>
        private void ShowTrayMenu()
        {
            Point sp = Cursor.Position;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int margin = (int)Math.Round(8 * scale);

            // 菜单只能画在窗口（= 工作区）里面。任务栏在左/右/下时，托盘在工作区**外面**，
            // 直接 PointToClient 会得到越界坐标 —— 之前只是笼统地夹一下，
            // 结果菜单落哪儿全看夹取顺序，和托盘对不上。
            // 这里改成先判断托盘贴的是哪条边，再决定菜单从哪边展开。
            int w = (int)Math.Round(330 * scale * 1.25);   // 估宽
            Point local = PointToClient(sp);

            if (sp.X <= wa.Left) local.X = margin;                                // 托盘在工作区左边
            else if (sp.X >= wa.Right - 1) local.X = ClientSize.Width - w - margin; // 右边

            if (sp.Y <= wa.Top) local.Y = margin;                                 // 上边
            else if (sp.Y >= wa.Bottom - 1) local.Y = Math.Max(margin, ClientSize.Height - margin * 3); // 下边

            // 剩下的交给 OpenMenu 统一夹进客户区
            OpenMenu(local, BuildTrayEntries());
        }

        /// <summary>托盘菜单内容（每次弹出时现建，勾选状态自然是最新的）。</summary>
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
                InvalidateCardsPositionSensitive();
                if (!cfg.autoArrange) Relayout();
                MarkDirtyAll();
                Wake();
            }));
            list.Add(MenuEntry.Check("自动排列卡片", cfg.autoArrange, delegate() { ToggleAutoArrange(); }));
            if (!cfg.autoArrange)
                list.Add(MenuEntry.Item("重置卡片位置（回到网格）", delegate() { ResetCardPositions(); }));
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Item("设置…", delegate() { OpenSettings(); }));
            list.Add(MenuEntry.Item("打开数据目录", delegate() { Native.ShowInExplorer(Config.DataDir); }));
            list.Add(MenuEntry.Item("查看日志", delegate()
            {
                try { Process.Start("notepad.exe", "\"" + Config.LogPath + "\""); } catch { }
            }));
            list.Add(MenuEntry.Sep());
            list.Add(MenuEntry.Check("隐藏 Windows 原生桌面图标", SystemIntegration.DesktopIconsHidden,
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
            list.Add(MenuEntry.Check("窗口停在壁纸层（需系统允许）", desktopMode, delegate() { SwitchMode("Desktop"); }));
            list.Add(MenuEntry.Sep());
            // 版本行：不可点，放在退出上面 —— 别人截图报 bug 时一眼能看出用的是哪版
            MenuEntry ver = MenuEntry.Item("TileDesk v" + AppVersion.Text + "（当前版本）", null);
            ver.enabled = false;
            list.Add(ver);
            list.Add(MenuEntry.Item("退出 TileDesk", delegate()
            {
                if (tray != null) tray.Visible = false;
                Application.Exit();
            }));
            return list;
        }

        private void Launch(int index)
        {
            TileItem it;
            lock (dataLock)
            {
                if (index < 0 || index >= view.Count) return;
                it = view[index];
            }

            // 启动方式：**交给正在运行的 shell 去打开**，也就是"双击桌面图标"那条路。
            //
            // 为什么不直接用 ShellExecuteEx（虽然它也能启动成功）：磁贴墙是个
            // "永不激活、紧贴桌面"的最底层窗口，**从来不是前台窗口** —— 由它直接拉起来的
            // 进程拿不到"把窗口提到前台"的权限。对"进程还活着、只是窗口被关掉了"的程序
            // （DeepSeek Harness 就是这种：单实例 + 通知已有实例 show()），第二次启动
            // 只是给已有实例发通知，那个实例 show() 时会被前台锁挡住：
            // 表现就是"点卡片没反应"，而双击桌面图标却能唤醒（因为那是 shell 拉起来的）。
            //
            // 注意 UseShellExecute 必须是 true —— 那才是"让 shell 去开"；
            // 设成 false 会变成我们自己 CreateProcess 拉一个 explorer.exe，
            // 在受限环境里会 0xc0000142（见 OpenRecycleBin 里记的实测）。
            try
            {
                if (File.Exists(it.path) || Directory.Exists(it.path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + it.path + "\"")
                    {
                        UseShellExecute = true
                    });
                    it.launchAnim = 1f;   // 成功反馈动画
                    MarkDirtyAll();
                    Wake();
                    Config.Log("启动（经 shell）: " + it.name);
                    return;
                }
            }
            catch (Exception ex)
            {
                Config.Log("经 shell 启动失败 " + it.name + " : " + ex.Message);
            }

            // 兜底：映射到别处的快捷方式 / 特殊路径（shell: 之类）走原来的 ShellExecuteEx，
            // 这样还能给出具体的错误码提示。
            int err;
            try { err = Native.ShellExecuteOpen(Handle, it.path); }
            catch (Exception ex)
            {
                err = -1;
                Config.Log("启动异常 " + it.path + " : " + ex.Message);
            }

            if (err == 0)
            {
                it.launchAnim = 1f;   // 成功反馈动画
                MarkDirtyAll();
                Wake();
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
                // 走 shell 的 API，别自己起 explorer.exe（受限环境会 0xc0000142）
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

        /// <summary>
        /// 直接按 Steam appid 取封面并固化成这张卡的自定义封面。
        /// 用途：有些快捷方式的 appid 认不出来（或认错了），可以手动指定。
        /// </summary>
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
            string file = null;
            try
            {
                file = SteamArt.CachedPortrait(id);
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
                InvalidateCards();
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
                    InvalidateCards();
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
        //  桌面目录监视：图标增删自动同步
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
            if (n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(n, "desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
            // 这里在别的线程上，只设个到期时间，让 UI 线程去刷新（防抖 1.2 秒）
            watchDueMs = unchecked(Environment.TickCount + 1200);
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

        /// <summary>
        /// 进程被降权时的提示，并直接帮忙复制到一个正常目录。
        /// </summary>
        private void ShowLowIntegrityWarning()
        {
            string exe = Application.ExecutablePath;
            string dst = Path.Combine(
                Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                Environment.UserName);
            dst = Path.Combine(dst, "TileDesk");

            DialogResult r = MessageBox.Show(
                "TileDesk 现在运行在「被降权」的状态下（完整性级别 " +
                Native.IntegrityRid().ToString("X4") + "，正常应为 2000）。\n\n" +
                "原因是程序所在目录带低完整性标签（常见于被沙箱 / 工作区管理的目录），\n" +
                "从这个目录启动的进程会被系统降权。\n\n" +
                "受影响：打不开回收站、不能开机自启、封面缓存只能放在程序旁边。\n\n" +
                "当前路径：\n" + exe + "\n\n" +
                "帮你复制到：" + dst + "\n\n现在复制吗？",
                "TileDesk", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            try
            {
                Directory.CreateDirectory(dst);
                string target = Path.Combine(dst, "TileDesk.exe");
                File.Copy(exe, target, true);
                Config.Log("已复制到正常目录: " + target);
                MessageBox.Show("已复制到：\n" + target +
                                "\n\n请关掉当前这个窗口，改为运行上面那个 TileDesk.exe。",
                    "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Config.Log("复制到正常目录失败: " + ex.Message);
                MessageBox.Show("复制失败：" + ex.Message + "\n\n请手动把这个文件复制到普通目录再运行：\n" + exe,
                    "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private SettingsForm settingsForm;   // 非模态设置窗口（单例）

        /// <summary>
        /// 打开设置界面。用**非模态**（Show），不是 ShowDialog。
        ///
        /// 原因：WinForms 的模态对话框会把**本线程的其它顶层窗口全部
        /// `EnableWindow(false)`** —— 「正在播放」控件是同一线程的独立分层窗口，
        /// 于是被一起禁掉：实测设置界面开着时控件 `enabled=False`，
        /// 鼠标点击全被丢掉（`SendMessage` 还能进，真实点击进不来），
        /// 表现就是"三个按钮按不动"。非模态没有这个问题，还顺带允许边调设置边控制播放。
        ///
        /// 也**不能**给设置窗口设 `Owner = 墙`：墙为了维持播放器层级会挪动自己
        /// （见 RaiseNowPlayingAboveWall），被拥有窗口会跟着挪，设置界面就沉底了。
        /// </summary>
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

        /// <summary>设置界面改动后立刻生效。</summary>
        /// <summary>
        /// 「正在播放」数据层。UI 侧直接读 <c>NowPlaying.State</c>（一个快照），
        /// 或者订阅 <c>NowPlaying.Changed</c>。
        /// <c>NowPlaying.Visibility</c> 是已经做好的 0..1 平滑值：
        /// QQ 音乐一开就慢慢升到 1，进程退出就慢慢降到 0，可以直接当不透明度用。
        /// </summary>
        public NowPlayingMonitor NowPlaying { get { return nowPlaying; } }

        public void ApplySettings()
        {
            acrylicDirty = true;
            SetupWatchers();
            RecomputeMetrics();
            ApplyFilter();
            cfg.Save();
            MarkDirtyAll();
            Wake();
        }

        // ---- 后台线程加载的图片，统一交给 UI 线程套用（避免跨线程碰 GDI+ 对象）----

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
                if (u.art != null) { u.item.artPath = u.path; u.item.art = u.art; }
                if (u.header != null) u.item.header = u.header;
            }
            InvalidateCards();
            MarkDirtyAll();
        }

        // ================================================================
        //  窗口模式
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
        }

        private void FitToWorkingArea()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (Bounds != wa) Bounds = wa;
        }

        /// <summary>
        /// 把整条链排成：**浮层 → 磁贴墙 → Progman**（也就是墙紧贴桌面之上、浮层浮在墙上面）。
        ///
        /// ★ 这里有个 Win32 的硬性约束（踩过两次坑）：
        ///   `SetWindowPos(A, B)` 永远是把 **A** 移动到 **B 下面**，A 会跟着 B 的位置跑。
        ///   所以要"A 在 B 上面"，只能移动 B —— 没法在不移动任何东西的前提下插队。
        ///   推论：想"墙紧贴桌面 + 浮层在墙上面"，就必须**先让浮层也沉到底部**，
        ///   再把墙插到浮层下面。否则浮层还停在屏幕最上层那一带，
        ///   把墙往它下面一插，整面墙就被拖到普通窗口带的高处，盖住所有程序
        ///   —— 这正是用户看到的"卡片置顶好几秒"。
        ///
        /// 步骤（每一步都用 SWP_NOREDRAW，不会闪）：
        ///   1) 墙、播放器、信息条各自沉到最底 —— 只移动它们自己，不牵动别的窗口；
        ///   2) 把墙插到每个浮层下面 —— 浮层就到墙上面了（此时大家都在底部附近，位移极小）；
        ///   3) 把 Progman 插到墙下面 —— 墙紧贴桌面之上。
        ///
        /// 找不到 Progman 时**什么都不做**：绝不能只沉底就完事（那样桌面图标层和所有窗口
        /// 都会盖住磁贴墙，KeepAboveDesktop 也会因为 progman==0 直接 return，墙再也回不来）。
        /// </summary>
        private void PlaceAboveDesktop()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return;

            uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW;
            IntPtr bottom = new IntPtr(1);   // HWND_BOTTOM

            IntPtr np = IntPtr.Zero, di = IntPtr.Zero;
            try
            {
                if (npWindow != null && !npWindow.IsDisposed && npWindow.IsHandleCreated) np = npWindow.Handle;
            }
            catch { }
            try
            {
                if (deskInfo != null && !deskInfo.IsDisposed && deskInfo.IsHandleCreated) di = deskInfo.Handle;
            }
            catch { }

            // 1) 三方先各自沉底
            Native.SetWindowPos(Handle, bottom, 0, 0, 0, 0, flags);
            if (np != IntPtr.Zero) Native.SetWindowPos(np, bottom, 0, 0, 0, 0, flags);
            if (di != IntPtr.Zero) Native.SetWindowPos(di, bottom, 0, 0, 0, 0, flags);

            // 2) 墙插到浮层下面 -> 浮层就在墙上面
            if (np != IntPtr.Zero) Native.SetWindowPos(Handle, np, 0, 0, 0, 0, flags);
            if (di != IntPtr.Zero) Native.SetWindowPos(Handle, di, 0, 0, 0, 0, flags);

            // 3) Progman 插到墙下面 -> 墙紧贴桌面之上
            Native.SetWindowPos(progman, Handle, 0, 0, 0, 0, flags);
        }

        /// <summary>
        /// 把「正在播放」控件重新抬到磁贴墙上面。
        ///
        /// 注意方向：`SetWindowPos(hWnd, hWndInsertAfter)` 是把 **hWnd 放到
        /// hWndInsertAfter 下面**。控件要在墙上面，所以把**墙**插到控件下面。
        ///
        /// 为什么不用 `npWindow.Owner = this`（更"优雅"但不可用）：被拥有窗口在收到
        /// 鼠标点击时会先尝试激活拥有者，而墙是 WS_EX_NOACTIVATE 的最底层窗口，
        /// 激活必然失败 → 点击被丢弃，控件的三个按钮直接点不动（实测确认）。
        /// 代价是移动的是墙：所以设置界面**不能是墙的被拥有窗口**（见 OpenSettings）。
        /// </summary>
        private void RaiseNowPlayingAboveWall()
        {
            if (npWindow == null || !npWindow.IsHandleCreated) return;
            try
            {
                Native.SetWindowPos(Handle, npWindow.Handle, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW);
            }
            catch { }
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

        protected override void WndProc(ref Message m)
        {
            // 壁纸/桌面外观变了：立刻重烤亚克力底图。
            // 原来只在启动和手动「重新载入壁纸」时生成，改了壁纸卡片底还是旧颜色，
            // 用户感受到的就是"刷新延迟高"（用户的对比对象是系统亚克力：那边是
            // 合成器每帧实时模糊，没有延迟概念）。
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
                        Wake();
                    }
                }
                catch { }
            }
            if (m.Msg == WM_THEMECHANGED || m.Msg == WM_DWMCOMPOSITIONCHANGED || m.Msg == WM_DISPLAYCHANGE)
            {
                acrylicDirty = true;
                try { EnsureAcrylic(false); MarkDirtyAll(); Wake(); } catch { }
            }

            // 隐藏状态下不接受任何点击 —— 防误触。
            // （SourceConstantAlpha=0 时鼠标本来就穿透了，这是第二道保险。）
            if (fadeLevel < FadeDead &&
                (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONUP || m.Msg == WM_LBUTTONDBLCLK ||
                 m.Msg == WM_RBUTTONUP || m.Msg == WM_MOUSEWHEEL))
            {
                return;
            }
            if (m.Msg == WM_MOUSEMOVE)
            {
                bool leftDown = (m.WParam.ToInt32() & 0x0001) != 0;
                HandleMouseMove(MouseClient(), leftDown);
                return;
            }
            if (m.Msg == WM_MOUSELEAVE) { ClearHover(); return; }

            // ★ WM_CAPTURECHANGED（0x0215）：鼠标捕获被别的窗口抢走。
            //   拖拽靠捕获把鼠标消息锁在墙上，捕获一丢，那条 WM_LBUTTONUP 就送到别人那里去了 ——
            //   墙永远不知道已经松手，卡片回弹；而"按键轮询兜底"也抓不到，
            //   因为用户快速连拖时，松开到再次按下只隔几毫秒，等下一拍轮询时左键又已经按下。
            //   这个通知是系统主动推的，不依赖轮询，所以在这里处理最可靠。
            if (m.Msg == WM_CAPTURECHANGED && dragging)
            {
                if (Native.KeyDown(Native.VK_LBUTTON))
                {
                    // 还按着：只是捕获被抢，抢回来继续拖
                    try { Native.SetCapture(Handle); } catch { }
                }
                else
                {
                    Config.Log("拖拽兜底: 捕获被抢且左键已松开 → 落位");
                    EndDrag(true);
                    return;
                }
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
                    OpenRecycleBin();   // 左键直接开回收站 —— 要找回文件就靠它
                    return;
                }
                  // 从屏幕右边缘起手 = 边缘手势候选，不当作点卡片
                  if (cfg.edgeSwipeNotify && pt.X >= ClientSize.Width - EdgeZone)
                  {
                      edgeArmed = true; pressAt = pt; pressCandidate = false; pressIndex = -1;
                      Native.SetCapture(Handle);
                      return;
                  }

                int i = IndexAt(pt);
                // 左键不记「选中态」。选中框只在右键菜单打开时才有意义（表示菜单作用于哪张卡），
                // 以前这里把 selIndex 记下来又没人清，结果左键点过一张卡之后那个蓝框就永远留着了。
                if (i >= 0) { pressCandidate = true; pressAt = pt; pressIndex = i; pressTick = unchecked(Environment.TickCount); }
                return;
            }
            if (m.Msg == WM_LBUTTONUP)
            {
                if (dragging) { EndDrag(true); return; }
                if (edgeArmed) { edgeArmed = false; try { Native.ReleaseCapture(); } catch { } return; }

                // 触摸屏上"点一下"就是打开 —— 不再要求双击。
                // 按下到松开的位移只要在 TapSlop 内就算点中，手指抖一下也不会失败。
                if (pressCandidate && pressIndex >= 0)
                {
                    Point up = MouseClient();
                    int ddx = Math.Abs(up.X - pressAt.X);
                    int ddy = Math.Abs(up.Y - pressAt.Y);
                    // 时间也要合理：正常点击几百毫秒内就抬起了。按下和抬起之间隔了一秒以上，
                    // 说明这个"抬起"和那次"按下"根本不是一回事（中间丢了消息/切过窗口），
                    // 不能拿来触发打开 —— 否则会出现"鼠标没动、游戏自己开了"。
                    bool quick = unchecked(Environment.TickCount - pressTick) <= 900;
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
                // 双击必须优先于拖拽。高 DPI 鼠标在两次点击之间很容易抖过拖拽阈值，
                // 一旦进入拖拽态，这里以前是 `if (dragging) return;` —— 双击直接被吞掉，
                // 表现就是「点了没反应」，而且时有时无。
                if (dragging)
                {
                    dragging = false; dragIndex = -1; dropIndex = -1; dragPreview = null;
            ResetDragAnim();
                    try { Native.ReleaseCapture(); } catch { }
                    Cursor = Cursors.Default;
                }
                pressCandidate = false; pressIndex = -1;
                // 单击已经负责打开了，双击这里只清拖拽态，不再重复启动。
                return;
            }
            if (m.Msg == WM_RBUTTONUP)
            {
                Point pt = MouseClient();
                    if (cfg.showControlButton && !ctrlRect.IsEmpty && ctrlRect.Contains(pt))
                {
                    ShowControlMenu(pt);
                    return;
                }
                if (cfg.showControlButton && !trashRect.IsEmpty && trashRect.Contains(pt))
                {
                    ShowTrashMenu(pt);
                    return;
                }
                int i = IndexAt(pt);
                if (i >= 0) { selIndex = i; MarkDirtyAll(); ShowItemMenu(i, pt); }
                return;
            }
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                if (Native.CtrlPressed)
                {
                    ZoomCards(delta > 0 ? 1 : -1);
                    return;
                }
                scrollTarget = ClampF(scrollTarget - delta * 0.55f * scale, 0, maxScroll);
                MarkDirtyAll();
                Wake();
                return;
            }

            if (m.Msg == Native.WM_WINDOWPOSCHANGING && stickBottom && !windowed)
            {
                Native.WINDOWPOS wp = (Native.WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(Native.WINDOWPOS));
                if ((wp.flags & Native.SWP_NOZORDER) == 0 && wp.hwndInsertAfter == IntPtr.Zero)
                {
                    IntPtr progman = Native.FindWindow("Progman", null);
                    // 找不到 Progman 就保持现有 z 序（SWP_NOZORDER）—— 和
                    // PlaceAboveDesktop 一样，绝不 SetWindowPos(HWND_BOTTOM) 沉底。
                    if (progman != IntPtr.Zero) wp.hwndInsertAfter = progman;
                    else wp.flags |= Native.SWP_NOZORDER;
                    Marshal.StructureToPtr(wp, m.LParam, false);
                }
            }
            else if (m.Msg == Native.WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == Native.SC_MINIMIZE)
            {
                return;   // Win+D / 显示桌面 不要把磁贴墙一起最小化
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
                InvalidateCards();
                MarkDirtyAll();
            }
            base.WndProc(ref m);
        }

        // ================================================================
        //  托盘
        // ================================================================

        private void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = BuildTrayIcon();
            tray.Text = "TileDesk v" + AppVersion.Text + " — 桌面磁贴";
            // 不交给 NotifyIcon 自动弹（那条路拿不到前台身份，菜单点不动）
            // 右键菜单由 ShowTrayMenu() 弹出
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
                else
                {
                      SetWallHidden(!wallHidden);
                    MarkDirtyAll();
                }
            };
        }

        private void ShowFirstRunDialog()
        {
            string extra = SystemIntegration.HasInstalledCopy
                ? "\n\n（检测到已有一份安装副本，可以选择覆盖安装）"
                : "";

            DialogResult r = MessageBox.Show(
                "TileDesk 已经跑起来了。\n\n" +
                "· 磁贴墙就铺在桌面上，把其它窗口最小化就能看到\n" +
                "· 【鼠标移到磁贴墙右下角】有个齿轮按钮 —— 设置、隐藏原生图标、\n" +
                "   安装、卸载、退出，全在这个按钮的菜单里\n" +
                "· 任务栏通知区域的托盘图标（蓝色双卡片）也有同一套菜单；\n" +
                "   Windows 11 可能把它收在任务栏「^」溢出区里\n\n" +
                "要现在把它安装到本机吗？（复制到用户目录 + 开机自启，" +
                "以后不需要再管这个 exe，卸载时可以一键还原系统）" + extra,
                "TileDesk 首次运行", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (r == DialogResult.Yes) InstallToMachine();
        }

        /// <summary>把当前 exe 复制到用户目录并设置开机自启 —— 这样就不用依赖任何脚本了。</summary>
        private void InstallToMachine()
        {
            string self = SystemIntegration.ExePath;
            string target = SystemIntegration.InstalledExe;

            if (SystemIntegration.RunningFromInstallDir)
            {
                SystemIntegration.SetAutoStart(true);
                MessageBox.Show("已经是从安装目录运行的了：\n" + target +
                    "\n\n已开启开机自启。",
                    "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(SystemIntegration.InstallDir);
                string installed = SystemIntegration.InstallSelf();

                if (MessageBox.Show(
                        "已安装到：\n" + installed +
                        "\n\n已开启开机自启，以后开机自动运行，不需要再管这个 exe 了。" +
                        "\n\n现在退出并改用安装后的版本吗？",
                        "TileDesk 安装完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true }); } catch { }
                    tray.Visible = false;
                    Application.Exit();
                }
            }
            catch (Exception ex)
            {
                Config.Log("安装失败: " + ex.Message);
                MessageBox.Show("安装失败：\n" + ex.Message, "TileDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>关闭自启、恢复原生图标、删除安装目录。</summary>
        private void UninstallFromMachine()
        {
            string installDir = SystemIntegration.InstallDir;
            bool selfInInstall = SystemIntegration.RunningFromInstallDir;

            string msg = "将执行：\n" +
                         "  · 关闭开机自启\n" +
                         "  · 恢复 Windows 原生桌面图标\n";
            if (Directory.Exists(installDir)) msg += "  · 删除安装目录 " + installDir + "\n";
            msg += "\n配置和封面缓存会保留（" + Config.DataDir + "）。\n\n确定要卸载吗？";

            if (MessageBox.Show(msg, "TileDesk 卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            SystemIntegration.UninstallSelf(false);
            tray.Visible = false;
            Application.Exit();
        }

        // 勾选状态由 BuildTrayEntries() / ShowControlMenu() 每次弹出时现算。

        private void SwitchMode(string mode)
        {
            ApplyWindowMode(mode);
            cfg.Save();
        }

        /// <summary>
        /// 托盘图标。注意：这里保留 HICON 的生命周期到进程结束再释放 ——
        /// Icon.FromHandle 返回的对象不拥有句柄，立刻 DestroyIcon 会让图标在某些
        /// 情况下失效，导致 Shell_NotifyIcon 加不上图标（托盘里什么都看不到）。
        /// </summary>
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
                    using (GraphicsPath p = RoundedRect(new RectangleF(1, 1, 13, 19), 3))
                        g.FillPath(br, p);
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 156, 205, 255)))
                    using (GraphicsPath p = RoundedRect(new RectangleF(17, 12, 13, 19), 3))
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

        // ================================================================

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tick != null) { tick.Stop(); tick.Dispose(); tick = null; }
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                ReleaseSurface();
                if (trayIconHandle != IntPtr.Zero) { Native.DestroyIcon(trayIconHandle); trayIconHandle = IntPtr.Zero; }
                if (wallpaperBlur != null) { wallpaperBlur.Dispose(); wallpaperBlur = null; }
                if (previewBackdrop != null) { previewBackdrop.Dispose(); previewBackdrop = null; }
                if (shadowBmp != null) { shadowBmp.Dispose(); shadowBmp = null; }
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