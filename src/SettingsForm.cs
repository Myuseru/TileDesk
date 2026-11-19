using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 设置界面（Fluent 风格，全部自绘）。
    ///
    /// 结构：固定页眉 + 可滚动内容区 + 固定页脚。
    /// 内容按「分区 → 卡片 → 行」组织，一行一个设置项：左侧标题 + 说明，右侧控件。
    /// 所有位置显式计算（不用 Dock）—— 之前 Dock 和自绘滚动条混用，
    /// 底部内容被页脚盖住、按钮被算到负坐标飞出窗口。
    /// </summary>
    public class SettingsForm : Form, IMessageFilter
    {
        private readonly Config cfg;
        private readonly TileForm owner;
        private readonly float s;
        private readonly Color accent;
        private bool loading = true;
        private System.Windows.Forms.Timer applyTimer;

        // 布局常量（逻辑像素）
        private const int MarginX = 26;
        private const int CardPad = 18;
        private const int RowH = 62;
        private const int RowHTight = 50;
        private const int CtrlW = 252;

        private readonly int HeaderH, FooterH;

        private Panel body;
        private int y;
        private CardPanel card;
        private int cardRowY;
        private int cardRows;

        public SettingsForm(Config config, TileForm ownerForm)
        {
            cfg = config;
            owner = ownerForm;

            int dpi = Native.GetWindowDpi(IntPtr.Zero);
            if (dpi <= 0) dpi = 96;
            s = dpi / 96f;
            if (s < 0.5f) s = 1f;
            accent = HexToColor(cfg.accentColor);

            HeaderH = P(88);
            FooterH = P(80);

            Text = "TileDesk 设置";
            FormBorderStyle = FormBorderStyle.None;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;

            // 窗口高度不能超过工作区：小屏笔记本上 880 逻辑像素会顶出屏幕，
            // 底部的"完成"按钮就点不到了。
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int wantW = P(760), wantH = P(880);
            int maxW = Math.Max(P(520), wa.Width - P(40));
            int maxH = Math.Max(P(420), wa.Height - P(40));
            ClientSize = new Size(Math.Min(wantW, maxW), Math.Min(wantH, maxH));
            BackColor = Ui.WindowBg;
            ForeColor = Ui.Text;
            Font = new Font("Microsoft YaHei UI", 9f);
            DoubleBuffered = true;

            BuildHeader();
            BuildFooter();
            BuildBody();

            loading = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
            Native.SetImmersiveDarkMode(Handle, true);

            MouseDown += delegate(object o, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left) DragWindow();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 背景：竖向渐变 + 左上角一团极淡的强调色辉光（"有光源"的现代感）
            using (LinearGradientBrush lg = new LinearGradientBrush(
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                Color.FromArgb(32, 33, 38), Color.FromArgb(19, 20, 23), LinearGradientMode.Vertical))
                g.FillRectangle(lg, ClientRectangle);

            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(-P(220), -P(260), P(760), P(560));
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Color.FromArgb(30, accent);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, accent) };
                    g.FillPath(pg, gp);
                }
            }

            // 1px 外框：无边框窗口没有它会在浅色壁纸上"糊"掉
            using (Pen pen = new Pen(Color.FromArgb(58, 255, 255, 255), Math.Max(1f, s)))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            base.OnPaint(e);
        }

        // ================================================================
        //  页眉 / 页脚
        // ================================================================

        private void BuildHeader()
        {
            Label title = new Label();
            title.Text = "设置";
            title.Font = new Font("Microsoft YaHei UI", 15.5f, FontStyle.Bold);
            title.ForeColor = Ui.Text;
            title.AutoSize = true;
            title.BackColor = Color.Transparent;
            title.Location = new Point(P(MarginX), P(26));
            title.MouseDown += delegate(object o, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left) DragWindow();
            };
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = "改动即时生效，不需要手动保存";
            sub.Font = new Font("Microsoft YaHei UI", 9f);
            sub.ForeColor = Ui.SubText;
            sub.AutoSize = true;
            sub.BackColor = Color.Transparent;
            sub.Location = new Point(P(MarginX), P(58));
            Controls.Add(sub);

            FlatIconButton close = new FlatIconButton(s);
            close.Glyph = "\uE8BB";
            close.Danger = true;
            close.Location = new Point(ClientSize.Width - P(MarginX) - close.Width, P(24));
            close.Click += delegate { FlushApply(); Close(); };
            Controls.Add(close);

            Panel line = new Panel();
            line.BackColor = Color.FromArgb(30, 255, 255, 255);
            line.Bounds = new Rectangle(P(MarginX), HeaderH - P(1), ClientSize.Width - P(MarginX) * 2, P(1));
            Controls.Add(line);
        }

        private void BuildFooter()
        {
            Panel line = new Panel();
            line.BackColor = Color.FromArgb(30, 255, 255, 255);
            line.Bounds = new Rectangle(P(MarginX), ClientSize.Height - FooterH, ClientSize.Width - P(MarginX) * 2, P(1));
            Controls.Add(line);

            FlatButton reset = new FlatButton(s, "恢复默认");
            reset.Size = new Size(P(104), P(36));
            reset.Location = new Point(P(MarginX), ClientSize.Height - FooterH + P(22));
            reset.Click += delegate
            {
                if (MessageBox.Show("恢复所有设置为默认值？", "TileDesk",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                CopyDefaults(cfg);
                owner.ApplySettings();
                loading = true;
                Close();
                owner.BeginInvoke((MethodInvoker)delegate { owner.OpenSettings(); });
            };
            Controls.Add(reset);

            FlatButton json = new FlatButton(s, "配置文件");
            json.Size = new Size(P(104), P(36));
            json.Location = new Point(ClientSize.Width - P(MarginX) - P(104) - P(10) - P(104), ClientSize.Height - FooterH + P(22));
            json.Click += delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + Config.ConfigPath + "\""); }
                catch { }
            };
            Controls.Add(json);

            FlatButton done = new FlatButton(s, "完成");
            done.Primary = true;
            done.Accent = accent;
            done.Size = new Size(P(104), P(36));
            done.Location = new Point(ClientSize.Width - P(MarginX) - P(104), ClientSize.Height - FooterH + P(22));
            done.Click += delegate { FlushApply(); Close(); };
            Controls.Add(done);
        }

        // ================================================================
        //  内容
        // ================================================================

        private void BuildBody()
        {
            body = new Panel();
            body.BackColor = Ui.WindowBg;
            body.AutoScroll = true;
            body.Bounds = new Rectangle(0, HeaderH, ClientSize.Width, ClientSize.Height - HeaderH - FooterH);
            // 原生滚动条是系统画的浅色，和这块深色面板不搭；把 body 加宽一个滚动条宽度，
            // 让它落到窗体外面被裁掉，再自己画一条细的。
            body.Width = ClientSize.Width + SystemInformation.VerticalScrollBarWidth;
            body.Scroll += delegate { UpdateScrollbar(); SbTouch(); body.Invalidate(); };
            body.Paint += delegate(object oo, PaintEventArgs pe) { DrawScrollbar(pe.Graphics); };
            body.MouseDown += delegate(object oo, MouseEventArgs me) { BodyMouseDown(me); };
            body.MouseMove += delegate(object oo, MouseEventArgs me) { BodyMouseMove(me); };
            body.MouseUp += delegate(object oo, MouseEventArgs me) { BodyMouseUp(me); };

            sbTimer = new System.Windows.Forms.Timer();
            sbTimer.Interval = 33;
            sbTimer.Tick += SbTick;
            sbTimer.Start();

            Controls.Add(body);

            Application.AddMessageFilter(this);

            y = P(6);

            // ---------------- 布局 ----------------
            BeginCard("布局与尺寸");
            AddSlider("卡片宽度", "封面按 2:3 缩放，高度自动跟随", 70, 320, cfg.cardWidth, 1,
                delegate(double v) { cfg.cardWidth = (int)v; cfg.cardHeight = (int)Math.Round(v * 1.5); }, "px");
            AddSlider("卡片间距", "调小可以让一面墙放更多卡片", 0, 80, cfg.gap, 1,
                delegate(double v) { cfg.gap = (int)v; }, "px");
            AddSlider("屏幕边距", "卡片区到屏幕边缘的距离", 0, 160, cfg.padding, 1,
                delegate(double v) { cfg.padding = (int)v; }, "px");
            AddSlider("圆角半径", "0 = 直角，16 以上接近胶囊", 0, 48, cfg.cornerRadius, 1,
                delegate(double v) { cfg.cornerRadius = (int)v; }, "px");
            AddToggle("自动排列卡片", "关掉后可以把卡片拖到任意格子", cfg.autoArrange,
                delegate(bool v) { cfg.autoArrange = v; });
            AddSegmented("排序方式", new string[] { "名称", "日期", "类型", "自定义" }, SortIndex(cfg.sortMode),
                delegate(int i) { cfg.sortMode = SortValue(i); });
            AddToggle("倒序排列", "从后往前排", cfg.sortDescending, delegate(bool v) { cfg.sortDescending = v; });
            EndCard();

            // ---------------- 外观 ----------------
            BeginCard("材质");
            AddSlider("玻璃浓度", "着色层占比。Win11 原生 80%；调到 0 就是纯模糊壁纸，不带任何固定色调",
                0, 0.95, cfg.acrylicTintOpacity, 0.01,
                delegate(double v) { cfg.acrylicTintOpacity = v; }, "");
            AddSlider("玻璃明度", "0.13 ≈ Win11 深色 #202020，0.95 ≈ 浅色 #F3F3F3", 0.02, 0.98,
                cfg.acrylicBrightness, 0.01, delegate(double v) { cfg.acrylicBrightness = v; }, "");
            AddSlider("模糊半径", "Win11 原生是 30px；调大更雾，调小会露出壁纸细节", 0, 120,
                cfg.acrylicBlur, 2, delegate(double v) { cfg.acrylicBlur = (int)v; }, "px");
            AddSlider("背景饱和度", "透过玻璃看到的背景彩度，原生是 1.5", 0.5, 2.5,
                cfg.acrylicSaturation, 0.05, delegate(double v) { cfg.acrylicSaturation = v; }, "×");
            AddSlider("磨砂颗粒", "玻璃表面的颗粒强度。40 ≈ Win7 Aero 的磨砂手感，0 = 完全光滑",
                0, 120, cfg.acrylicNoise, 1,
                delegate(double v) { cfg.acrylicNoise = v; }, "");
            AddSlider("颗粒粗细", "颗粒的放大倍数：1 = 细腻（Win7 手感），4 = 粗砂", 1, 4,
                cfg.acrylicNoiseScale, 0.5, delegate(double v) { cfg.acrylicNoiseScale = v; }, "×");
            AddSlider("玻璃不透明度", "只影响玻璃材质本身，封面不受影响", 0.1, 1.0, cfg.cardOpacity, 0.01,
                delegate(double v) { cfg.cardOpacity = v; }, "");
            AddSlider("封面不透明度", "只影响有封面的卡片，和玻璃互不影响", 0.1, 1.0, cfg.coverOpacity, 0.01,
                delegate(double v) { cfg.coverOpacity = v; }, "");
            AddSlider("投影浓度", "卡片下方的柔和阴影强度", 0, 1, cfg.shadowOpacity, 0.02,
                delegate(double v) { cfg.shadowOpacity = v; }, "");
            AddColor("投影颜色", "深色壁纸配黑影；浅色/纯色壁纸可换带色相的软影", cfg.shadowColor,
                delegate(string v) { cfg.shadowColor = v; });
            AddSlider("投影大小", "阴影扩散半径", 0, 60, cfg.shadowSize, 1,
                delegate(double v) { cfg.shadowSize = (int)v; }, "px");
            AddColor("强调色", "悬停描边、选中框与控件主色", cfg.accentColor,
                delegate(string v) { cfg.accentColor = v; });
            AddToggle("常显卡片名称", "关掉后只在鼠标悬停时浮出", cfg.showLabels,
                delegate(bool v) { cfg.showLabels = v; });
            AddToggle("悬停时浮出名称", "关闭则悬停时只有放大和描边", cfg.labelsOnHover,
                delegate(bool v) { cfg.labelsOnHover = v; });
            EndCard();

            // ---------------- 桌面组件 ----------------
            BeginCard("桌面组件");
            AddSlider("空闲隐藏", "桌面无操作多少秒后淡出，0 = 从不隐藏", 0, 60, cfg.idleHideSeconds, 1,
                delegate(double v) { cfg.idleHideSeconds = v; }, "秒");
            AddToggle("右下角按钮", "垃圾桶与齿轮（也可以只用托盘图标）", cfg.showControlButton,
                delegate(bool v) { cfg.showControlButton = v; });
            AddToggle("时钟与天气", "常驻右下角，不跟随空闲隐藏", cfg.showDeskInfo,
                delegate(bool v) { cfg.showDeskInfo = v; });
            AddToggle("时钟玻璃底板", "关 = 完全透明只有文字（默认），开 = 加一层深色玻璃", cfg.deskInfoGlass,
                delegate(bool v) { cfg.deskInfoGlass = v; });
            AddToggle("天气图标动画", "太阳旋转、雨滴下落、闪电呼吸", cfg.weatherAnim,
                delegate(bool v) { cfg.weatherAnim = v; });
            AddText("天气城市", "留空 = 自动定位（wttr.in 数据源）", cfg.weatherCity,
                delegate(string v) { cfg.weatherCity = v; });
            AddToggle("正在播放控件", "读取系统媒体会话（SMTC）", cfg.showNowPlaying,
                delegate(bool v) { cfg.showNowPlaying = v; });
            EndCard();

            // ---------------- 频谱 ----------------
            BeginCard("音频频谱");
            AddToggle("显示音频频谱", "WASAPI 回环采集 + FFT，跟随系统输出", cfg.showSpectrum,
                delegate(bool v) { cfg.showSpectrum = v; });
            AddSlider("柱子数量", "低频到高频按对数分布", 16, 128, cfg.spectrumBars, 1,
                delegate(double v) { cfg.spectrumBars = (int)v; }, "");
            AddSlider("高度", "柱子最高能到多少像素", 40, 400, cfg.spectrumHeight, 5,
                delegate(double v) { cfg.spectrumHeight = (int)v; }, "px");
            AddSlider("宽度占屏比", "整条频谱占屏幕宽度的比例", 0.20, 1.00, cfg.spectrumWidth, 0.02,
                delegate(double v) { cfg.spectrumWidth = v; }, "");
            AddSlider("不透明度", "越高越亮", 0.10, 2.00, cfg.spectrumOpacity, 0.05,
                delegate(double v) { cfg.spectrumOpacity = v; }, "");
            AddSlider("柱宽比例", "1.0 = 柱子之间没有缝隙", 0.15, 1.00, cfg.spectrumBarWidth, 0.05,
                delegate(double v) { cfg.spectrumBarWidth = v; }, "");
            AddSlider("下落速度", "数值越大，回落越慢越稳", 0.04, 0.40, cfg.spectrumFall, 0.01,
                delegate(double v) { cfg.spectrumFall = v; }, "s");
            EndCard();

            // ---------------- 行为 ----------------
            BeginCard("同步与系统");
            AddToggle("自动同步桌面图标", "新建 / 删除 / 改名后约 1 秒自动刷新", cfg.watchDesktop,
                delegate(bool v) { cfg.watchDesktop = v; });
            AddToggle("自动下载 Steam 封面", "本地没有时联网补下（缓存在数据目录）", cfg.downloadSteamArt,
                delegate(bool v) { cfg.downloadSteamArt = v; });
            AddToggle("右边缘左滑唤起通知", "Win8/Win10 的手势，Win11 已移除", cfg.edgeSwipeNotify,
                delegate(bool v) { cfg.edgeSwipeNotify = v; });
            AddToggle("开机自动启动", "写入当前用户的启动项", SystemIntegration.AutoStartEnabled,
                delegate(bool v) { SystemIntegration.SetAutoStart(v); cfg.autoStartWithWindows = v; });
            AddToggle("写详细日志", "排查问题时打开，平时保持关闭", cfg.verboseLog,
                delegate(bool v) { cfg.verboseLog = v; Config.Verbose = v; });
            EndCard();

            AddHint("Ctrl + 滚轮 缩放卡片 · 拖动卡片重排 · 拖到垃圾桶删除\n" +
                    "右键卡片可以设置自定义封面、重命名、永久删除\n" +
                    "右键空白处是桌面自己的菜单（空白处的鼠标是穿透的）");

            Label ver = new Label();
            ver.Text = "TileDesk v" + AppVersion.Text;
            ver.ForeColor = Ui.Faint;
            ver.Font = new Font("Microsoft YaHei UI", 8.5f);
            ver.AutoSize = true;
            ver.BackColor = Ui.WindowBg;
            ver.Location = new Point(P(MarginX + CardPad), y + P(10));
            body.Controls.Add(ver);
            y += P(40);

            body.AutoScrollMinSize = new Size(0, y);
        }

        private void BeginCard(string section)
        {
            y += P(16);

            Label head = new Label();
            head.Text = section;
            head.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            head.ForeColor = Ui.Mix(accent, Ui.Text, 0.35f);
            head.AutoSize = true;
            head.BackColor = Ui.WindowBg;
            head.Location = new Point(P(MarginX + 4), y);
            body.Controls.Add(head);
            y += P(28);

            card = new CardPanel(s);
            card.Location = new Point(P(MarginX), y);
            card.Width = ClientSize.Width - P(MarginX) * 2 - P(12);
            card.Height = P(8);
            card.BackColor = Ui.WindowBg;
            body.Controls.Add(card);

            cardRowY = P(8);
            cardRows = 0;
        }

        private void EndCard()
        {
            card.Height = cardRowY + P(8);
            y = card.Top + card.Height;
            card = null;
        }

        private void NextRow()
        {
            if (cardRows > 0)
            {
                Panel sep = new Panel();
                sep.BackColor = Color.FromArgb(26, 255, 255, 255);
                sep.Bounds = new Rectangle(P(CardPad), cardRowY, card.Width - P(CardPad) * 2, P(1));
                card.Controls.Add(sep);
            }
            cardRows++;
        }

        private void AddRowCore(string label, string desc, Control ctrl, int rowH)
        {
            NextRow();

            int textX = P(CardPad);
            int textW = card.Width - P(CardPad) * 2 - (ctrl != null ? ctrl.Width + P(20) : 0);
            if (textW < P(60)) textW = P(60);

            Label l = new Label();
            l.Text = label;
            l.Font = new Font("Microsoft YaHei UI", 9.5f);
            l.ForeColor = Ui.Text;
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.TextAlign = desc != null ? ContentAlignment.BottomLeft : ContentAlignment.MiddleLeft;
            l.BackColor = Ui.Card;
            if (desc != null)
            {
                l.Bounds = new Rectangle(textX, cardRowY + P(11), textW, P(20));
                Label d = new Label();
                d.Text = desc;
                d.Font = new Font("Microsoft YaHei UI", 8.2f);
                d.ForeColor = Ui.Faint;
                d.AutoSize = false;
                d.AutoEllipsis = true;
                d.TextAlign = ContentAlignment.TopLeft;
                d.BackColor = Ui.Card;
                d.Bounds = new Rectangle(textX, cardRowY + P(31), textW, P(18));
                card.Controls.Add(d);
            }
            else
            {
                l.Bounds = new Rectangle(textX, cardRowY, textW, rowH);
            }
            card.Controls.Add(l);

            if (ctrl != null)
            {
                ctrl.BackColor = Ui.Card;
                ctrl.Location = new Point(card.Width - P(CardPad) - ctrl.Width,
                                          cardRowY + (rowH - ctrl.Height) / 2);
                card.Controls.Add(ctrl);
                ctrl.BringToFront();
            }

            cardRowY += rowH;
        }

        private void AddSlider(string label, string desc, double min, double max, double value,
                               double step, Action<double> set, string suffix)
        {
            FlatSlider sl = new FlatSlider(s);
            sl.Accent = accent;
            sl.Width = P(CtrlW);
            sl.Height = P(32);
            // ★ 数值标签占控件右侧一条独立区域，轨道/旋钮不再延伸到它下面
            //   —— 之前值一高，旋钮会滑进标签背后被盖住（看起来像滑条被截断）。
            sl.ValueInset = P(74);
            double init = (value - min) / (max - min);
            sl.SetSilent((int)Math.Max(0, Math.Min(1000, Math.Round(init * 1000))));

            Label val = new Label();
            val.AutoSize = false;
            val.TextAlign = ContentAlignment.MiddleRight;
            val.Font = new Font("Microsoft YaHei UI", 9f);
            val.ForeColor = accent;
            val.BackColor = Ui.Card;
            val.Bounds = new Rectangle(sl.Width - P(74), 0, P(74), P(32));
            sl.Controls.Add(val);

            Action update = delegate()
            {
                double v = min + (max - min) * (sl.Value / 1000.0);
                if (step >= 1) v = Math.Round(v); else v = Math.Round(v / step) * step;
                val.Text = (step >= 1 ? ((int)v).ToString(CultureInfo.InvariantCulture)
                                      : v.ToString("0.##", CultureInfo.InvariantCulture))
                           + (suffix.Length > 0 ? " " + suffix : "");
            };
            update();

            sl.ValueChanged += delegate
            {
                update();
                if (loading) return;
                double v = min + (max - min) * (sl.Value / 1000.0);
                if (step >= 1) v = Math.Round(v); else v = Math.Round(v / step) * step;
                set(v);
                Apply();
            };

            AddRowCore(label, desc, sl, P(RowH));
        }

        private void AddToggle(string label, string desc, bool value, Action<bool> set)
        {
            FlatToggle tg = new FlatToggle(s);
            tg.Accent = accent;
            tg.SetSilent(value);
            tg.CheckedChanged += delegate
            {
                if (loading) return;
                set(tg.Checked);
                Apply();
            };
            AddRowCore(label, desc, tg, P(desc != null ? RowH : RowHTight));
        }

        private void AddSegmented(string label, string[] items, int index, Action<int> set)
        {
            FlatSegmented sg = new FlatSegmented(s, items);
            sg.Accent = accent;
            sg.Width = P(CtrlW);
            sg.SetSilent(index);
            sg.SelectedIndexChanged += delegate
            {
                if (loading) return;
                set(sg.SelectedIndex);
                Apply();
            };
            AddRowCore(label, null, sg, P(RowHTight));
        }

        private void AddText(string label, string desc, string value, Action<string> set)
        {
            FlatTextBox tb = new FlatTextBox(s);
            tb.Width = P(CtrlW);
            tb.Text = value ?? "";
            tb.TextValueChanged += delegate
            {
                if (loading) return;
                set(tb.Text);
                Apply();
            };
            AddRowCore(label, desc, tb, P(RowH));
        }

        private void AddColor(string label, string desc, string value, Action<string> set)
        {
            Panel host = new Panel();
            host.Width = P(CtrlW);
            host.Height = P(32);
            host.BackColor = Ui.Card;

            Panel swatch = new Panel();
            swatch.Bounds = new Rectangle(0, P(4), P(52), P(24));
            swatch.BackColor = HexToColor(value);
            swatch.Paint += delegate(object o, PaintEventArgs pe)
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Ui.Round(new RectangleF(0.5f, 0.5f, swatch.Width - 1, swatch.Height - 1), P(6)))
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                    pe.Graphics.DrawPath(pen, p);
            };
            host.Controls.Add(swatch);

            FlatButton pick = new FlatButton(s, "选择颜色…");
            pick.Accent = accent;
            pick.Bounds = new Rectangle(P(60), 0, P(120), P(32));
            pick.Click += delegate
            {
                using (ColorDialog d = new ColorDialog())
                {
                    d.Color = swatch.BackColor;
                    d.FullOpen = true;
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    swatch.BackColor = d.Color;
                    swatch.Invalidate();
                    string hex = string.Format("#{0:X2}{1:X2}{2:X2}", d.Color.R, d.Color.G, d.Color.B);
                    set(hex);
                    Apply();
                }
            };
            host.Controls.Add(pick);

            AddRowCore(label, desc, host, P(RowH));
        }

        private void AddHint(string text)
        {
            y += P(18);
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.ForeColor = Ui.Faint;
            l.BackColor = Ui.WindowBg;
            l.Font = new Font("Microsoft YaHei UI", 8.5f);
            l.Location = new Point(P(MarginX + CardPad), y);
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            l.Size = new Size(ClientSize.Width - P(MarginX) * 2 - P(20), lines * P(21) + P(4));
            body.Controls.Add(l);
            y += l.Height;
        }

        // ================================================================
        //  滚动条
        // ================================================================

        private Rectangle sbThumb = Rectangle.Empty;
        private int sbTrackX, sbTrackTop, sbTrackH;
        private bool sbDrag;
        private int sbDragGrab;
        private int sbHoldUntil;
        private float sbShow;
        private bool sbHover;
        private System.Windows.Forms.Timer sbTimer;

        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_LBUTTONDOWN = 0x0201;

        public bool PreFilterMessage(ref Message m)
        {
            if (body == null || !Visible) return false;

            if (m.Msg == WM_LBUTTONDOWN)
            {
                IntPtr h = Native.GetCapture();
                Control cap = h == IntPtr.Zero ? null : Control.FromHandle(h);
                if (cap != null && cap != body) Native.ReleaseCapture();
            }

            if (m.Msg != WM_MOUSEWHEEL) return false;
            int delta = (short)((long)m.WParam >> 16);
            ScrollBy(-delta);
            return true;
        }

        private void ScrollBy(int amount)
        {
            int hi = ScrollMax();
            if (hi <= 0) return;
            int cur = ScrollPos();
            int v = cur + amount;
            if (v < 0) v = 0;
            if (v > hi) v = hi;
            if (v == cur) return;
            SetScroll(v);
            SbTouch();
            body.Invalidate();
        }

        private bool SbWanted { get { return sbDrag || sbHover || unchecked(Clock.NowMs - sbHoldUntil) < 0; } }

        private void SbTouch()
        {
            sbHoldUntil = Clock.NowMs + 900;
            if (sbTimer != null && !sbTimer.Enabled) sbTimer.Start();
        }

        private void SbTick(object o, EventArgs e)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) { sbTimer.Stop(); return; }
            Point cf = PointToClient(Cursor.Position);
            bool h = cf.X >= ClientSize.Width - P(18) && cf.Y > HeaderH && cf.Y < ClientSize.Height - FooterH;
            if (h != sbHover) { sbHover = h; if (h) sbHoldUntil = Clock.NowMs + 900; }

            float target = SbWanted ? 1f : 0f;
            if (Math.Abs(sbShow - target) > 0.01f)
            {
                sbShow += (target > sbShow) ? 0.16f : -0.16f;
                if (sbShow < 0f) sbShow = 0f;
                if (sbShow > 1f) sbShow = 1f;
                body.Invalidate();
            }
            else if (sbShow != target)
            {
                sbShow = target;
                body.Invalidate();
                if (sbShow <= 0f) sbTimer.Stop();
            }
        }

        private int ScrollMax()
        {
            int m = body.VerticalScroll.Maximum - body.VerticalScroll.LargeChange + 1;
            return m > 0 ? m : 0;
        }

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
            int w = P(sbHover || sbDrag ? 7 : 4);
            sbTrackX = ClientSize.Width - P(7) - w;
            sbTrackTop = P(8);
            sbTrackH = ClientSize.Height - HeaderH - FooterH - P(16);
            int hi = ScrollMax();
            if (hi <= 0 || sbTrackH <= 0) { sbThumb = Rectangle.Empty; return; }
            int large = body.VerticalScroll.LargeChange;
            int total = hi + large;
            int th = Math.Max(P(36), (int)(sbTrackH * (large / (double)total)));
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
            int a = (int)((sbDrag ? 170 : 120) * sbShow);
            float rad = sbThumb.Width / 2f;
            using (GraphicsPath hp = Ui.Round(new RectangleF(sbThumb.X, sbThumb.Y, sbThumb.Width, sbThumb.Height), rad))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255)))
                g.FillPath(b, hp);
        }

        private void BodyMouseDown(MouseEventArgs me)
        {
            UpdateScrollbar();
            if (!sbThumb.IsEmpty && me.X >= sbTrackX - P(8) && me.X <= sbTrackX + P(12) &&
                me.Y >= sbThumb.Top - P(8) && me.Y <= sbThumb.Bottom + P(8))
            {
                sbDrag = true;
                sbDragGrab = me.Y - sbThumb.Top;
                body.Capture = true;
                sbHoldUntil = Clock.NowMs + 600000;
                body.Invalidate();
            }
        }

        private void BodyMouseMove(MouseEventArgs me)
        {
            if (!sbDrag || sbThumb.IsEmpty) return;
            int hi = ScrollMax();
            int range = sbTrackH - sbThumb.Height;
            if (range <= 0 || hi <= 0) return;
            SetScroll((int)((me.Y - sbDragGrab - sbTrackTop) / (double)range * hi));
            body.Invalidate();
        }

        private void BodyMouseUp(MouseEventArgs me)
        {
            if (!sbDrag) return;
            sbDrag = false;
            body.Capture = false;
            SbTouch();
            body.Invalidate();
        }

        // ================================================================
        //  应用 / 关闭
        // ================================================================

        private void Apply()
        {
            if (loading) return;
            ApplySoon();
        }

        /// <summary>
        /// 延迟合并应用：拖滑块时 ValueChanged 一秒能触发几十次，
        /// 每次都重建所有卡片缓存 + 写磁盘会疯狂闪烁。
        /// </summary>
        private void ApplySoon()
        {
            if (loading) return;
            if (applyTimer == null)
            {
                applyTimer = new System.Windows.Forms.Timer();
                applyTimer.Interval = 180;
                applyTimer.Tick += delegate
                {
                    applyTimer.Stop();
                    try { owner.ApplySettings(); } catch { }
                };
            }
            applyTimer.Stop();
            applyTimer.Start();
        }

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
            FlushApply();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnFormClosed(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            try { if (Native.GetCapture() != IntPtr.Zero) Native.ReleaseCapture(); } catch { }
            base.OnDeactivate(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        private void DragWindow()
        {
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
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

        private static void CopyDefaults(Config c)
        {
            Config d = new Config();
            c.cardWidth = d.cardWidth;
            c.cardHeight = d.cardHeight;
            c.gap = d.gap;
            c.padding = d.padding;
            c.cornerRadius = d.cornerRadius;
            c.cardOpacity = d.cardOpacity;
            c.coverOpacity = d.coverOpacity;
            c.showLabels = d.showLabels;
            c.labelsOnHover = d.labelsOnHover;
            c.acrylicTintOpacity = d.acrylicTintOpacity;
            c.acrylicSaturation = d.acrylicSaturation;
            c.acrylicBrightness = d.acrylicBrightness;
            c.acrylicTint = d.acrylicTint;
            c.acrylicBlur = d.acrylicBlur;
            c.darkCards = d.darkCards;
            c.deskInfoGlass = d.deskInfoGlass;
            c.shadowOpacity = d.shadowOpacity;
            c.shadowColor = d.shadowColor;
            c.shadowSize = d.shadowSize;
            c.accentColor = d.accentColor;
            c.showControlButton = d.showControlButton;
            c.showDeskInfo = d.showDeskInfo;
            c.weatherAnim = d.weatherAnim;
            c.weatherCity = d.weatherCity;
            c.showSpectrum = d.showSpectrum;
            c.spectrumBars = d.spectrumBars;
            c.spectrumHeight = d.spectrumHeight;
            c.spectrumWidth = d.spectrumWidth;
            c.spectrumOpacity = d.spectrumOpacity;
            c.spectrumBarWidth = d.spectrumBarWidth;
            c.spectrumFall = d.spectrumFall;
            c.idleHideSeconds = d.idleHideSeconds;
            c.sortMode = d.sortMode;
            c.sortDescending = d.sortDescending;
            c.downloadSteamArt = d.downloadSteamArt;
            c.watchDesktop = d.watchDesktop;
            c.autoArrange = d.autoArrange;
            c.edgeSwipeNotify = d.edgeSwipeNotify;
            c.showNowPlaying = d.showNowPlaying;
        }
    }
}
