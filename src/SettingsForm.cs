using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 设置界面。全部用自绘控件（见 UiKit.cs），改哪儿立刻生效。
    ///
    /// 布局刻意**不用 Dock**：之前底部栏用 Dock=Bottom、内容用 Dock=Fill，
    /// 结果内容被压在底部栏下面（最后一项被挡住），而底部按钮又是在
    /// bottom.ClientSize 还是初始值时算的坐标，被算到负坐标飞出了窗口。
    /// 现在所有位置都按窗口尺寸显式算，确定性的。
    /// </summary>
    public class SettingsForm : Form, IMessageFilter
    {
        private readonly Config cfg;
        private readonly TileForm owner;
        private readonly float s;
        private readonly Color accent;
        private bool loading = true;
        private System.Windows.Forms.Timer applyTimer;      // 合并频繁的设置变动（见 ApplySoon）

        private readonly int LabelX, LabelW, CtrlX, SliderW, ValueX, ValueW;
        private readonly int RowSlider, RowToggle, RowSection, HeaderH, FooterH;

        /// <summary>开了双缓冲的面板 —— 减少滚动时自身的重绘闪烁。</summary>
        private class BufferedPanel : Panel
        {
            public BufferedPanel() { DoubleBuffered = true; }
        }

        private Panel body;
        private int y;

        public SettingsForm(Config config, TileForm ownerForm)
        {
            cfg = config;
            owner = ownerForm;

            int dpi = Native.GetWindowDpi(IntPtr.Zero);
            if (dpi <= 0) dpi = 96;
            s = dpi / 96f;
            if (s < 0.5f) s = 1f;
            accent = HexToColor(cfg.accentColor);

            LabelX = P(24); LabelW = P(150);
            CtrlX = P(182); SliderW = P(260);
            ValueX = P(452); ValueW = P(140);
            RowSlider = P(44); RowToggle = P(40); RowSection = P(36);
            HeaderH = P(70); FooterH = P(72);

            Text = "TileDesk 设置";
            // no border - the footer already has a close button, a title bar is redundant
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(P(620), P(780));
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            Font = new Font("Microsoft YaHei UI", 9f);

            // ---------------- 页眉 ----------------
            Label title = new Label();
            title.Text = "TileDesk 设置";
            title.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            title.ForeColor = Ui.Text;
            title.AutoSize = true;
            title.Location = new Point(LabelX, P(18));
            title.MouseDown += delegate(object o, MouseEventArgs me) { if (me.Button == MouseButtons.Left) DragWindow(); };
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "改完立刻生效，不用手动保存";
            sub.ForeColor = Ui.SubText;
            sub.AutoSize = true;
            sub.Location = new Point(LabelX, P(44));
            sub.MouseDown += delegate(object o, MouseEventArgs me) { if (me.Button == MouseButtons.Left) DragWindow(); };
            Controls.Add(sub);

            Panel hline = new Panel();
            hline.BackColor = Ui.Line;
            hline.Bounds = new Rectangle(0, HeaderH - P(1), ClientSize.Width, P(1));
            Controls.Add(hline);

            // ---------------- 页脚 ----------------
            Panel footer = new Panel();
            footer.BackColor = Ui.Bg;
            footer.Bounds = new Rectangle(0, ClientSize.Height - FooterH, ClientSize.Width, FooterH);
            Controls.Add(footer);

            Panel fline = new Panel();
            fline.BackColor = Ui.Line;
            fline.Bounds = new Rectangle(0, 0, footer.Width, P(1));
            footer.Controls.Add(fline);

            FlatButton reset = new FlatButton(s, "恢复默认");
            reset.Size = new Size(P(110), P(34));
            reset.Location = new Point(LabelX, P(19));
            reset.Click += delegate(object o, EventArgs e)
            {
                if (MessageBox.Show("恢复所有设置为默认值？", "TileDesk",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                CopyInto(new Config(), cfg);
                owner.ApplySettings();
                loading = true;
                Close();
                // 重开也走非模态那条路（模态会把同线程的播放器控件一起禁掉）
                owner.BeginInvoke((MethodInvoker)delegate { owner.OpenSettings(); });
            };
            footer.Controls.Add(reset);

            FlatButton close = new FlatButton(s, "关闭");
            close.Primary = true;
            close.Accent = accent;
            close.Size = new Size(P(110), P(34));
            close.Location = new Point(footer.Width - LabelX - P(110), P(19));
            close.Click += delegate(object o, EventArgs e) { FlushApply(); Close(); };
            footer.Controls.Add(close);

            FlatButton json = new FlatButton(s, "打开 JSON");
            json.Size = new Size(P(110), P(34));
            json.Location = new Point(close.Left - P(10) - P(110), P(19));
            json.Click += delegate(object o, EventArgs e)
            {
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Config.ConfigPath + "\""); }
                catch { }
            };
            footer.Controls.Add(json);

            // ---------------- 内容区 ----------------
            body = new BufferedPanel();
            body.BackColor = Ui.Bg;
            body.AutoScroll = true;
            body.Bounds = new Rectangle(0, HeaderH, ClientSize.Width, ClientSize.Height - HeaderH - FooterH);
            // 原生滚动条是系统画的浅色，跟这块深色面板完全不搭，而且没法主题化。
            // 办法：把 body 加宽一个滚动条宽度，让原生滚动条落到窗体外面被裁掉，
            // 然后自己在 OnPaint 里画一条细的（WinForms 的子控件会被父级裁剪）。
            body.Width = ClientSize.Width + SystemInformation.VerticalScrollBarWidth;
            body.Scroll += delegate { UpdateScrollbar(); SbTouch(); body.Invalidate(); };
            sbTimer = new Timer();
            sbTimer.Interval = 33;
            sbTimer.Tick += SbTick;
            // 必须一开始就启动：悬停判定是在计时器里做的，
            // 而计时器原来只由"滚动"启动 —— 先有鸡还是先有蛋，永远转不起来。
            // 完全隐形时 SbTick 会自己把它停掉，所以不会空转。
            sbTimer.Start();

            // 原生滚动条被裁到窗体外面了，滚轮必须自己接管 —— 否则用户根本没法滚动。
            // 用消息过滤器而不是 MouseWheel 事件：滚轮消息默认发给**有焦点的控件**，
            // 我们的滑条一被拖动就可能拿到焦点，事件方式会漏。
            // 注意：SetFormClosed/Dipose 里要 Remove，否则每开一次设置就漏一个过滤器。
            Application.AddMessageFilter(this);

            // 拖动必须绑在 body 上：滚动条画在 body 上，鼠标事件被子控件吃掉了，
            // 绑在窗体上根本收不到（这就是之前"拖不动"的原因）。
            // 按下后 body.Capture = true，这样拖到滑条上面也还能继续收到移动。
            body.MouseDown += delegate(object oo, MouseEventArgs me)
            {
                UpdateScrollbar();
                if (!sbThumb.IsEmpty && me.X >= sbTrackX - P(8) && me.X <= sbTrackX + P(12) &&
                    me.Y >= sbThumb.Top - P(8) && me.Y <= sbThumb.Bottom + P(8))
                {
                    sbDrag = true;
                    sbDragGrab = me.Y - sbThumb.Top;
                    body.Capture = true;
                    sbHoldUntil = unchecked(Environment.TickCount + 600000);   // 拖动期间常亮
                    body.Invalidate();
                }
            };
            body.MouseMove += delegate(object oo, MouseEventArgs me)
            {
                if (!sbDrag || sbThumb.IsEmpty) return;
                int hi = ScrollMax();
                int range = sbTrackH - sbThumb.Height;
                if (range <= 0 || hi <= 0) return;
                SetScroll((int)((me.Y - sbDragGrab - sbTrackTop) / (double)range * hi));
                body.Invalidate();
            };
            body.MouseUp += delegate(object oo, MouseEventArgs me)
            {
                if (!sbDrag) return;
                sbDrag = false;
                body.Capture = false;
                SbTouch();
                body.Invalidate();
            };
            // 必须画在 body 自己的 Paint 上：子控件画在父控件之上，
            // 画在窗体 OnPaint 里会被 body 整个盖住。
            body.Paint += delegate(object oo, PaintEventArgs pe) { DrawScrollbar(pe.Graphics); };
            Controls.Add(body);

            y = P(14);

            AddSection("卡片布局");
            AddSlider("卡片宽度", 70, 320, cfg.cardWidth, 1,
                delegate(double v) { cfg.cardWidth = (int)v; cfg.cardHeight = (int)Math.Round(v * 1.5); }, "px");
            AddSlider("卡片间距", 0, 80, cfg.gap, 1, delegate(double v) { cfg.gap = (int)v; }, "px");
            AddSlider("空闲隐藏", 0, 60, cfg.idleHideSeconds, 1, delegate(double v) { cfg.idleHideSeconds = v; }, "秒");
            AddSlider("屏幕边距", 0, 160, cfg.padding, 1, delegate(double v) { cfg.padding = (int)v; }, "px");
            AddSlider("圆角半径", 0, 48, cfg.cornerRadius, 1, delegate(double v) { cfg.cornerRadius = (int)v; }, "px");
            AddSlider("卡片不透明度", 0.25, 1.0, cfg.cardOpacity, 0.01, delegate(double v) { cfg.cardOpacity = v; }, "");
            AddToggle("卡片上常态显示名称", cfg.showLabels, delegate(bool v) { cfg.showLabels = v; });
            AddToggle("鼠标悬停时浮出名称", cfg.labelsOnHover, delegate(bool v) { cfg.labelsOnHover = v; });
            // 标签一律不带括号补充说明：左边的文字一长就压到右边的开关上了。
            // 需要解释的都放进最下面的提示块。
            AddToggle("自动排列卡片", cfg.autoArrange,
                delegate(bool v) { cfg.autoArrange = v; });

            AddSection("外观");
            AddSlider("亚克力压暗", 0.05, 1.0, cfg.acrylicBrightness, 0.01,
                delegate(double v) { cfg.acrylicBrightness = v; }, "");
            AddSlider("磨砂白度", 0, 0.5, cfg.acrylicTint, 0.01,
                delegate(double v) { cfg.acrylicTint = v; }, "");
            AddSlider("模糊半径", 0, 160, cfg.acrylicBlur, 2,
                delegate(double v) { cfg.acrylicBlur = (int)v; }, "px");
            AddSection("音频频谱");
            AddToggle("显示音频频谱", cfg.showSpectrum,
                delegate(bool v) { cfg.showSpectrum = v; });
            AddSlider("柱子数量", 16, 128, cfg.spectrumBars, 1,
                delegate(double v) { cfg.spectrumBars = (int)v; }, "");
            AddSlider("高度", 40, 400, cfg.spectrumHeight, 5,
                delegate(double v) { cfg.spectrumHeight = (int)v; }, "px");
            AddSlider("宽度占屏比", 0.20, 1.00, cfg.spectrumWidth, 0.02,
                delegate(double v) { cfg.spectrumWidth = v; }, "");
            AddSlider("不透明度", 0.10, 2.00, cfg.spectrumOpacity, 0.05,
                delegate(double v) { cfg.spectrumOpacity = v; }, "");
            AddSlider("柱宽比例", 0.15, 1.00, cfg.spectrumBarWidth, 0.05,
                delegate(double v) { cfg.spectrumBarWidth = v; }, "");
            AddSlider("下落时间", 0.04, 0.40, cfg.spectrumFall, 0.01,
                delegate(double v) { cfg.spectrumFall = v; }, "s");

            AddToggle("卡片深色模式", cfg.darkCards,
                delegate(bool v) { cfg.darkCards = v; });
            AddSlider("投影浓度", 0, 1, cfg.shadowOpacity, 0.02,
                delegate(double v) { cfg.shadowOpacity = v; }, "");
            AddSlider("投影大小", 0, 60, cfg.shadowSize, 1,
                delegate(double v) { cfg.shadowSize = (int)v; }, "px");
            AddColor("强调色", cfg.accentColor, delegate(string v) { cfg.accentColor = v; });
            AddToggle("显示右下角按钮", cfg.showControlButton, delegate(bool v) { cfg.showControlButton = v; });
            AddToggle("显示时钟与天气", cfg.showDeskInfo, delegate(bool v) { cfg.showDeskInfo = v; });
            AddText("天气城市", cfg.weatherCity, delegate(string v) { cfg.weatherCity = v; });
            AddToggle("天气图标动画", cfg.weatherAnim, delegate(bool v) { cfg.weatherAnim = v; });
            AddToggle("写详细日志（排查问题用）", cfg.verboseLog, delegate(bool v)
            {
                cfg.verboseLog = v;
                Config.Verbose = v;      // 立刻生效，不用重启
            });

            AddSection("内容与排序");
            AddSegmented("排序方式", new string[] { "名称", "日期", "类型", "自定义" }, SortIndex(cfg.sortMode),
                delegate(int i) { cfg.sortMode = SortValue(i); });
            AddToggle("倒序排列", cfg.sortDescending, delegate(bool v) { cfg.sortDescending = v; });
            AddToggle("自动下载 Steam 封面", cfg.downloadSteamArt, delegate(bool v) { cfg.downloadSteamArt = v; });
            AddToggle("自动同步桌面图标", cfg.watchDesktop, delegate(bool v) { cfg.watchDesktop = v; });

            AddHint("提示：Ctrl + 滚轮 缩放卡片；拖动卡片可以重排；\n" +
                    "把卡片拖到右下角垃圾桶 = 删掉桌面图标，进回收站；\n" +
                    "右键卡片 →「用 Steam appid 设置封面」可以手动指定封面来源。\n" +
                    "自动排列卡片关掉后，可以把卡片拖到任意位置；\n" +
                    "空闲隐藏设成 0 = 不隐藏；外观几项只影响没有封面的卡片。\n" +
                    "时钟与天气常驻在右下角，不跟着空闲隐藏；天气城市留空 = 自动定位，数据来自 wttr.in。");

            loading = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);

            // borderless window: drag it by the header
            MouseDown += delegate(object o, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left) DragWindow();
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 构造函数里 AddMessageFilter 过一次，这里必须成对移除：
            // 设置界面现在是非模态、可以反复开关，不移除就会越积越多。
            Application.RemoveMessageFilter(this);
            base.OnFormClosed(e);
        }

        /// <summary>
        /// 窗口失活时把鼠标捕获交回去。窗口一旦不是活动窗口，某个控件抓着 Capture
        /// 不放就会把后续点击全部吞掉（滚轮不受影响，所以看起来"能滚动、点不动"）。
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            try { if (Native.GetCapture() != IntPtr.Zero) Native.ReleaseCapture(); } catch { }
            base.OnDeactivate(e);
        }

        private void DragWindow()
        {
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        // ================================================================
        //  滚动条：照 Chromium 的 Fluent 做法
        //
        //  · 覆盖式，不占布局宽度
        //  · 没有轨道底槽（Fluent 就是纯滑块）
        //  · 平时完全隐形，滚动/悬停时淡入，停下约 0.9 秒淡出
        //  · 悬停或拖动时加粗（5 -> 8 逻辑像素）
        //
        //  为什么画在 body.Paint 而不是窗体 OnPaint：子控件画在父控件之上，
        //  画在窗体上会被 body 整个盖住。
        // ================================================================

        private Rectangle sbThumb = Rectangle.Empty;
        private int sbTrackX, sbTrackTop, sbTrackH;
        private bool sbDrag;
        private int sbDragGrab;
        private int sbHoldUntil;          // 这段时间内保持可见（滚动/拖动后）
        private float sbShow;             // 0..1 淡入淡出进度
        private bool sbHover;             // 鼠标是否贴近滚动条
        private Timer sbTimer;

        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_LBUTTONDOWN = 0x0201;

        public bool PreFilterMessage(ref Message m)
        {
            if (body == null || !Visible) return false;

            // 残留捕获兜底：某个子控件（滑块）抓住了鼠标但已经不在鼠标底下时，
            // 点击会被它一直吞掉 —— 表现为"整个设置界面点不动，只能滚动"。
            // 注意：WinForms 的 Control.Capture 是 bool，拿不到"谁在抓"，
            // 必须走 Win32 GetCapture 才查得出捕获者。
            if (m.Msg == WM_LBUTTONDOWN)
            {
                IntPtr h = Native.GetCapture();
                Control cap = h == IntPtr.Zero ? null : Control.FromHandle(h);
                if (cap != null && cap != body) Native.ReleaseCapture();
            }

            if (m.Msg != WM_MOUSEWHEEL) return false;
            int delta = (short)((long)m.WParam >> 16);   // 高 16 位是滚轮增量（±120）
            ScrollBy(-delta);
            return true;                                  // 吃掉，避免又滚一层
        }

        private void ScrollBy(int amount)
        {
            int hi = ScrollMax();
            if (hi <= 0) return;
            int cur = ScrollPos();
            int v = cur + amount;
            if (v < 0) v = 0;
            if (v > hi) v = hi;
            // 到顶/到底之后别再重绘 —— 无级滚轮在边界狂刷就是"抽风"的来源
            if (v == cur) return;
            SetScroll(v);
            SbTouch();
            body.Invalidate();
        }

        private bool SbWanted { get { return sbDrag || sbHover || unchecked(Environment.TickCount - sbHoldUntil) < 0; } }

        private void SbTouch()
        {
            sbHoldUntil = unchecked(Environment.TickCount + 900);   // 停下 0.9 秒后开始淡出
            if (sbTimer != null && !sbTimer.Enabled) sbTimer.Start();
        }

        private void SbTick(object o, EventArgs e)
        {
            // 窗口已经关掉/正在释放时绝不能碰它：
            // PointToClient 会去 CreateHandle，在已释放的 Form 上直接抛
            // ObjectDisposedException，而且这个 Timer 每 33ms 抛一次、
            // 每次都中断一次 UI 消息循环 —— 表现出来就是"整个程序卡死/墙不显示"。
            if (IsDisposed || Disposing || !IsHandleCreated) { sbTimer.Stop(); return; }
            // 悬停判定放在这里：子控件会吃掉 MouseMove，靠事件判断会漏
            Point cf = PointToClient(Cursor.Position);
            bool h = cf.X >= ClientSize.Width - P(18) && cf.Y > HeaderH &&
                     cf.Y < ClientSize.Height - FooterH;
            if (h != sbHover) { sbHover = h; if (h) sbHoldUntil = unchecked(Environment.TickCount + 900); }

            float target = SbWanted ? 1f : 0f;
            if (Math.Abs(sbShow - target) > 0.01f)
            {
                float step = 0.16f;
                sbShow += (target > sbShow) ? step : -step;
                if (sbShow < 0f) sbShow = 0f;
                if (sbShow > 1f) sbShow = 1f;
                body.Invalidate();
            }
            else if (sbShow != target)
            {
                sbShow = target;
                body.Invalidate();
                if (sbShow <= 0f) sbTimer.Stop();   // 完全隐形就停掉计时器
            }
        }

        private int ScrollMax()
        {
            // Win32 滚动条可滚范围是 [0, Max - LargeChange + 1]
            int m = body.VerticalScroll.Maximum - body.VerticalScroll.LargeChange + 1;
            return m > 0 ? m : 0;
        }

        /// <summary>
        /// 当前滚动位置。
        /// **不能用 VerticalScroll.Value** —— 对 AutoScroll 面板它读出来不一定是真实位置、
        /// 写进去也不一定生效。权威值是 AutoScrollPosition：读出来是负的，写进去用正的。
        /// </summary>
        private int ScrollPos() { return -body.AutoScrollPosition.Y; }

        private void SetScroll(int v)
        {
            int hi = ScrollMax();
            if (v < 0) v = 0;
            if (v > hi) v = hi;
            body.AutoScrollPosition = new Point(0, v);
        }

        private void UpdateScrollbar()
        {
            if (body == null) return;
            int w = P(sbHover || sbDrag ? 8 : 5);
            sbTrackX = ClientSize.Width - P(6) - w;
            // 注意：这里必须用 body 自己的坐标！滚动条是画在 body 面板上的，
            // 而 body 的左上角本来就在窗体的 (0, HeaderH) 处。
            // 之前写成 HeaderH + P(6) 是窗体坐标，等于整体又下移了一个 HeaderH ——
            // 顶部比"卡片布局"低 140px、底部伸进底栏被"关闭"按钮盖住。
            sbTrackTop = P(6);
            sbTrackH = ClientSize.Height - HeaderH - FooterH - P(12);
            int hi = ScrollMax();
            if (hi <= 0 || sbTrackH <= 0) { sbThumb = Rectangle.Empty; return; }
            int large = body.VerticalScroll.LargeChange;
            int total = hi + large;
            int th = Math.Max(P(34), (int)(sbTrackH * (large / (double)total)));
            int range = sbTrackH - th;
            int pos = sbTrackTop + (int)(range * (ScrollPos() / (double)hi));
            sbThumb = new Rectangle(sbTrackX, pos, w, th);
        }

        private void DrawScrollbar(Graphics g)
        {
            if (sbShow <= 0.01f) return;
            UpdateScrollbar();
            if (sbThumb.IsEmpty) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int a = (int)((sbDrag ? 150 : 110) * sbShow);
            float rad = sbThumb.Width / 2f;
            using (GraphicsPath hp = Ui.Round(new RectangleF(sbThumb.X, sbThumb.Y, sbThumb.Width, sbThumb.Height), rad))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                g.FillPath(b, hp);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            UpdateScrollbar();
            if (!sbThumb.IsEmpty && e.X >= sbTrackX - P(6) && e.X <= sbTrackX + P(10) &&
                e.Y >= sbThumb.Top - P(4) && e.Y <= sbThumb.Bottom + P(4))
            {
                sbDrag = true;
                sbDragGrab = e.Y - sbThumb.Top;
                body.Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!sbDrag || sbThumb.IsEmpty) return;
            int max = body.VerticalScroll.Maximum - body.VerticalScroll.Minimum;
            int large = body.VerticalScroll.LargeChange;
            int range = sbTrackH - sbThumb.Height;
            if (range <= 0) return;
            int val = (int)((e.Y - sbDragGrab - sbTrackTop) / (double)range * (max - large));
            body.VerticalScroll.Value = Math.Max(body.VerticalScroll.Minimum,
                                                 Math.Min(max - large, body.VerticalScroll.Minimum + val));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (sbDrag) { sbDrag = false; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Ui.Line, 1f))
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        private int P(int v) { return (int)Math.Round(v * s); }

        private static int SortIndex(string mode)
        {
            if (string.Equals(mode, "Date", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(mode, "Kind", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase)) return 3;
            return 0;
        }

        private static string SortValue(int i)
        {
            if (i == 1) return "Date";
            if (i == 2) return "Kind";
            if (i == 3) return "Custom";
            return "Name";
        }

        private static void CopyInto(Config from, Config to)
        {
            to.cardWidth = from.cardWidth;
            to.cardHeight = from.cardHeight;
            to.gap = from.gap;
            to.padding = from.padding;
            to.cornerRadius = from.cornerRadius;
            to.cardOpacity = from.cardOpacity;
            to.showLabels = from.showLabels;
            to.labelsOnHover = from.labelsOnHover;
            to.acrylicBrightness = from.acrylicBrightness;
            to.acrylicTint = from.acrylicTint;
            to.acrylicBlur = from.acrylicBlur;
            to.shadowOpacity = from.shadowOpacity;
            to.shadowSize = from.shadowSize;
            to.accentColor = from.accentColor;
            to.showControlButton = from.showControlButton;
            to.sortMode = from.sortMode;
            to.sortDescending = from.sortDescending;
            to.downloadSteamArt = from.downloadSteamArt;
            to.watchDesktop = from.watchDesktop;
            to.autoArrange = from.autoArrange;
        }

        private void Apply()
        {
            if (loading) return;
            ApplySoon();
        }

        /// <summary>
        /// 延迟合并应用设置。
        ///
        /// 拖滑块时 ValueChanged 一秒钟能触发几十次，每次都直接 ApplySettings() 的话，
        /// 里面会 RecomputeMetrics + ApplyFilter（重建**所有卡片缓存**）+ cfg.Save（写磁盘），
        /// 于是画面疯狂闪烁、磁盘狂写（用户实测："调滑块的时候会疯狂闪烁"）。
        /// 这里合并成"停手 180 毫秒后才应用一次"，拖动过程只改内存里的配置值。
        /// </summary>
        private void ApplySoon()
        {
            if (loading) return;
            if (applyTimer == null)
            {
                applyTimer = new System.Windows.Forms.Timer();
                applyTimer.Interval = 180;
                applyTimer.Tick += delegate(object o, EventArgs e)
                {
                    applyTimer.Stop();
                    try { owner.ApplySettings(); } catch { }
                };
            }
            applyTimer.Stop();
            applyTimer.Start();
        }

        /// <summary>立刻把待应用的变化落下去（关闭设置窗口前调用，避免丢了最后一次改动）。</summary>
        private void FlushApply()
        {
            if (applyTimer != null && applyTimer.Enabled)
            {
                applyTimer.Stop();
                try { owner.ApplySettings(); } catch { }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            FlushApply();      // Esc 或右上角关闭时也要把最后一次改动落下
            base.OnFormClosing(e);
        }

        private Label MakeLabel(string text, int x, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.AutoEllipsis = true;   // 万一以后标签又写长了，宁可截断也不要压到右边开关上
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Location = new Point(x, y);
            l.Size = new Size(w, h);
            l.ForeColor = Ui.Text;
            l.BackColor = Ui.Bg;
            body.Controls.Add(l);
            return l;
        }

        private void AddSection(string text)
        {
            y += P(6);
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            l.ForeColor = accent;
            l.BackColor = Ui.Bg;
            l.AutoSize = true;
            l.Location = new Point(LabelX, y + P(8));
            body.Controls.Add(l);
            y += RowSection;
        }

        private void AddHint(string text)
        {
            y += P(10);
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.ForeColor = Ui.SubText;
            l.BackColor = Ui.Bg;
            l.Location = new Point(LabelX, y);
            // 高度按行数算：写几行就占几行，别让文字被自己的框裁掉
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            int h = lines * P(20) + P(4);
            l.Size = new Size(ClientSize.Width - LabelX * 2 - P(16), h);
            body.Controls.Add(l);
            y += h + P(4);
        }

        private void AddSlider(string text, double min, double max, double value,
                               double step, Action<double> set, string suffix)
        {
            MakeLabel(text, LabelX, LabelW, RowSlider);

            FlatSlider sl = new FlatSlider(s);
            sl.Accent = accent;
            sl.Bounds = new Rectangle(CtrlX, y + (RowSlider - sl.Height) / 2, SliderW, sl.Height);
            double init = (value - min) / (max - min);
            sl.SetSilent((int)Math.Max(0, Math.Min(1000, Math.Round(init * 1000))));
            body.Controls.Add(sl);

            Label val = MakeLabel("", ValueX, ValueW, RowSlider);
            val.ForeColor = accent;

            Action update = delegate()
            {
                double v = min + (max - min) * (sl.Value / 1000.0);
                if (step >= 1) v = Math.Round(v); else v = Math.Round(v / step) * step;
                val.Text = (step >= 1 ? ((int)v).ToString(CultureInfo.InvariantCulture)
                                      : v.ToString("0.##", CultureInfo.InvariantCulture))
                           + (suffix.Length > 0 ? " " + suffix : "");
            };
            update();

            sl.ValueChanged += delegate(object o, EventArgs e)
            {
                update();
                if (loading) return;
                double v = min + (max - min) * (sl.Value / 1000.0);
                if (step >= 1) v = Math.Round(v); else v = Math.Round(v / step) * step;
                set(v);
                Apply();
            };

            y += RowSlider;
        }

        private void AddToggle(string text, bool value, Action<bool> set)
        {
            MakeLabel(text, LabelX, LabelW + P(130), RowToggle);

            FlatToggle tg = new FlatToggle(s);
            tg.Accent = accent;
            tg.SetSilent(value);
            tg.Location = new Point(CtrlX, y + (RowToggle - tg.Height) / 2);
            tg.CheckedChanged += delegate(object o, EventArgs e)
            {
                if (loading) return;
                set(tg.Checked);
                Apply();
            };
            body.Controls.Add(tg);
            tg.BringToFront();   // 标签是不透明的且比控制列宽，必须让开关盖在它上面
            y += RowToggle;
        }

        /// <summary>单行文本输入（深色自绘风格）。天气城市用它。</summary>
        private void AddText(string text, string value, Action<string> set)
        {
            MakeLabel(text, LabelX, LabelW, RowToggle);

            TextBox tb = new TextBox();
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.BackColor = Color.FromArgb(28, 30, 36);
            tb.ForeColor = Ui.Text;
            tb.Font = new Font("Microsoft YaHei UI", 10f * s);
            tb.Text = value ?? "";
            tb.Location = new Point(CtrlX, y + (RowToggle - tb.Height) / 2);
            tb.Size = new Size(SliderW + ValueW - P(60), tb.Height);
            tb.TextChanged += delegate(object o, EventArgs e)
            {
                if (loading) return;
                set(tb.Text);
                Apply();
            };
            body.Controls.Add(tb);
            tb.BringToFront();
            y += RowToggle;
        }

        private void AddSegmented(string text, string[] items, int index, Action<int> set)
        {
            MakeLabel(text, LabelX, LabelW, RowSlider);

            FlatSegmented sg = new FlatSegmented(s, items);
            sg.Accent = accent;
            sg.SetSilent(index);
            sg.Bounds = new Rectangle(CtrlX, y + (RowSlider - sg.Height) / 2, SliderW + P(80), sg.Height);
            sg.SelectedIndexChanged += delegate(object o, EventArgs e)
            {
                if (loading) return;
                set(sg.SelectedIndex);
                Apply();
            };
            body.Controls.Add(sg);
            sg.BringToFront();
            y += RowSlider;
        }

        private void AddColor(string text, string value, Action<string> set)
        {
            MakeLabel(text, LabelX, LabelW, RowSlider);

            Panel swatch = new Panel();
            swatch.BackColor = HexToColor(value);
            swatch.Location = new Point(CtrlX, y + (RowSlider - P(26)) / 2);
            swatch.Size = new Size(P(54), P(26));
            body.Controls.Add(swatch);

            FlatButton pick = new FlatButton(s, "选择颜色…");
            pick.Accent = accent;
            pick.Size = new Size(P(130), P(30));
            pick.Location = new Point(CtrlX + P(66), y + (RowSlider - P(30)) / 2);
            pick.Click += delegate(object o, EventArgs e)
            {
                using (ColorDialog d = new ColorDialog())
                {
                    d.Color = swatch.BackColor;
                    d.FullOpen = true;
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    swatch.BackColor = d.Color;
                    string hex = string.Format("#{0:X2}{1:X2}{2:X2}", d.Color.R, d.Color.G, d.Color.B);
                    set(hex);
                    Apply();
                }
            };
            body.Controls.Add(pick);
            pick.BringToFront();
            y += RowSlider;
        }

        private static Color HexToColor(string hex)
        {
            try
            {
                string t = (hex ?? "").TrimStart('#');
                if (t.Length == 6)
                    return Color.FromArgb(
                        Convert.ToInt32(t.Substring(0, 2), 16),
                        Convert.ToInt32(t.Substring(2, 2), 16),
                        Convert.ToInt32(t.Substring(4, 2), 16));
            }
            catch { }
            return Color.FromArgb(76, 154, 255);
        }
    }
}
