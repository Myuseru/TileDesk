using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 右下角常驻信息条：时间 + 日期 + 天气。
    ///
    /// 和「正在播放」控件一样是独立分层窗口：这块内容要求**常驻**，
    /// 而磁贴墙空闲会整体淡出 —— 画在墙上就跟着没了。
    ///
    /// v3 改动：
    ///   · 加一层玻璃底板（圆角 + 极淡白 + 1px 描边），任何壁纸上都读得清；
    ///   · 时间用细字重、更大的字号，日期/星期做真正的两级层次；
    ///   · 动画只在自己可见、且没被别的窗口盖住时才跑（原来那个 100ms 定时器
    ///     不管看不看得见都在重绘整块画布）。
    /// </summary>
    internal sealed class DeskInfoWindow : Form
    {
        private readonly float scale;
        private readonly Config cfg;

        private readonly LayerSurface surface = new LayerSurface();
        private bool dirty = true;

        // 天气（后台线程拉，锁保护）
        private readonly object wlock = new object();
        private string wTemp = "", wText = "", wSign = "", wCity = "";
        private int fetchAtMs;
        private int fetching;
        private const int FetchIntervalMs = 20 * 60 * 1000;

        private string lastName = "";
        private double animT;
        private System.Windows.Forms.Timer iconTick;
        private Font fontTime, fontDate, fontWeek, fontTemp, fontCity, fontSign;

        private bool coveredNow;
        private int coveredCheckAt;

        public DeskInfoWindow(Config config, float dpiScale)
        {
            cfg = config;
            scale = dpiScale;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Width2(), Height2());

            // 15fps：够顺，又不至于为了一个 34px 的图标一直烧 CPU
            iconTick = new System.Windows.Forms.Timer();
            iconTick.Interval = 66;
            iconTick.Tick += delegate
            {
                if (IsDisposed || !cfg.weatherAnim || !Visible) return;
                if (wText.Length == 0) return;
                if (unchecked(Clock.NowMs - coveredCheckAt) > 500)
                {
                    coveredCheckAt = Clock.NowMs;
                    coveredNow = CoveredByOthers();
                }
                if (coveredNow) return;
                animT = Clock.Now;
                Draw();
            };
            iconTick.Start();
        }

        /// <summary>逻辑 340x76（物理尺寸再乘 scale）。</summary>
        public int Width2() { return (int)Math.Round(340 * scale); }
        public int Height2() { return (int)Math.Round(76 * scale); }

        protected override bool ShowWithoutActivation { get { return true; } }

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
        //  由磁贴墙驱动
        // ================================================================

        public void Sync(IntPtr above, Point location, bool assertZ)
        {
            if (IsDisposed) return;
            if (animT == 0) animT = Clock.Now;

            if (!geoRunning)
            {
                if (unchecked(Clock.NowMs - geoNextMs) >= 0) StartGeo();
            }
            else if (unchecked(Clock.NowMs - geoStartMs) > LocateTimeoutMs)
            {
                Config.Log("定位: 超时没结果，放弃本次（改用城市/IP 定位）");
                StopGeo();
            }

            if (!Visible) { Location = location; Show(); dirty = true; }
            else if (Location != location) Location = location;

            if (assertZ && above != IntPtr.Zero && IsHandleCreated)
            {
                Native.SetWindowPos(above, Handle, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW);
            }

            string sig = Signature();
            if (sig != lastName) { lastName = sig; dirty = true; }
            if (dirty) Draw();
            MaybeFetchWeather();
        }

        private string Signature()
        {
            DateTime now = DateTime.Now;
            string w;
            lock (wlock) w = wTemp + "|" + wText + "|" + wSign + "|" + wCity;
            return now.ToString("HH:mm") + "|" + now.ToString("M月d日") + "|" + WeekName(now) + "|" + w;
        }

        private static string WeekName(DateTime t)
        {
            string[] n = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return n[(int)t.DayOfWeek];
        }

        private bool CoveredByOthers()
        {
            try
            {
                if (!Visible) return true;
                uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                int[] xs = new int[] { ClientSize.Width / 3, ClientSize.Width / 2, ClientSize.Width * 2 / 3 };
                for (int i = 0; i < xs.Length; i++)
                {
                    Point p = PointToScreen(new Point(xs[i], ClientSize.Height / 2));
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

        // ================================================================
        //  绘制
        // ================================================================


        /// <summary>
        /// 画一行带柔和投影的文字。
        ///
        /// 时钟/日期是**完全透明**地浮在壁纸上的，没有底板兜底 ——
        /// 浅色壁纸上纯白字会糊成一片，所以给一层偏移 1px、55% 的黑色投影，
        /// 比加底板轻得多，也不破坏"全透明"。
        /// </summary>
        private static void DrawText(Graphics g, string s, Font f, Color c, float x, float y)
        {
            if (string.IsNullOrEmpty(s)) return;
            float off = Math.Max(1f, f.Size * 0.035f);
            using (SolidBrush sh = new SolidBrush(Color.FromArgb((int)(c.A * 0.55f), 0, 0, 0)))
                g.DrawString(s, f, sh, x, y + off);
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, x, y);
        }

        private void EnsureFonts()
        {
            if (fontTime != null) return;
            string fam = "Microsoft YaHei UI";
            fontTime = new Font(fam, Math.Max(20f, 38f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontDate = new Font(fam, Math.Max(10f, 14.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontWeek = new Font(fam, Math.Max(9f, 12f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontTemp = new Font(fam, Math.Max(11f, 17f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontCity = new Font(fam, Math.Max(8.5f, 11.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontSign = new Font("Segoe UI Symbol", Math.Max(18f, 32f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void Draw()
        {
            if (!surface.Ensure(ClientSize.Width, ClientSize.Height)) return;
            dirty = false;

            DateTime now = DateTime.Now;
            string temp, text, sign, city;
            lock (wlock) { temp = wTemp; text = wText; sign = wSign; city = wCity; }

            Graphics g = surface.G;
            surface.ClearAll();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            EnsureFonts();
            int w = ClientSize.Width, h = ClientSize.Height;

            // ---- 底板 ----
            // 默认**什么都不画**：这块要的是"信息浮在壁纸上"，不是一块玻璃砖。
            // 2.x 就是这样，3.0 为了可读性擅自加了深色底板，用户实测一眼就发现
            // "日期模块没有全透明"。可读性交给文字自己的柔和投影解决。
            if (cfg.deskInfoGlass)
            {
                RectangleF panel = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);
                float rad = Math.Min(18 * scale, h / 2f);
                using (GraphicsPath p = Ui.Round(panel, rad))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 16, 18, 24)))
                    g.FillPath(b, p);
                using (GraphicsPath p = Ui.Round(panel, rad))
                {
                    Region saved = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new RectangleF(0, 0, w, h), Color.FromArgb(26, 255, 255, 255),
                        Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                        g.FillRectangle(lg, 0, 0, w, h);
                    g.Clip = saved;
                }
                Ui.StrokeRounded(g, panel, rad, Color.FromArgb(48, 255, 255, 255), Math.Max(1f, scale));
            }

            float padL = 16 * scale;

            // ---- 时间 ----
            string clock = now.ToString("HH:mm");
            SizeF szT = g.MeasureString(clock, fontTime);
            float x = padL;
            DrawText(g, clock, fontTime, Color.FromArgb(246, 255, 255, 255), x, (h - szT.Height) / 2f - 1 * scale);
            x += szT.Width + 12 * scale;

            // ---- 分隔线 ----
            using (Pen p = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1f, scale)))
                g.DrawLine(p, x, h * 0.26f, x, h * 0.74f);
            x += 12 * scale;

            // ---- 日期 + 星期 ----
            string d1 = now.ToString("M月d日");
            string d2 = WeekName(now);
            SizeF sd1 = g.MeasureString(d1, fontDate);
            SizeF sd2 = g.MeasureString(d2, fontWeek);
            float blockH = sd1.Height + sd2.Height - 3 * scale;
            float y0 = (h - blockH) / 2f;
            DrawText(g, d1, fontDate, Color.FromArgb(236, 255, 255, 255), x, y0);
            DrawText(g, d2, fontWeek, Color.FromArgb(168, 186, 196, 208), x, y0 + sd1.Height - 3 * scale);
            float dateRight = x + Math.Max(sd1.Width, sd2.Width);

            // ---- 天气块（右对齐）----
            string line1 = temp.Length > 0 ? temp + "°" : "--";
            string wd = CnDesc(text);
            string line2 = (city.Length > 0 ? city : "定位中") + (wd.Length > 0 ? " " + wd : "");
            SizeF s1 = g.MeasureString(line1, fontTemp);
            SizeF s2 = g.MeasureString(line2, fontCity);
            float wBlock = Math.Max(s1.Width, s2.Width);
            float wRight = w - padL - wBlock;

            float iconSize = 32 * scale;
            float iconRight = wRight - 10 * scale;
            float iconX = iconRight - iconSize;
            float minIconX = dateRight + 8 * scale;
            if (iconX < minIconX) iconX = minIconX;
            float iconY = (h - iconSize) / 2f;

            if (text.Length > 0)
            {
                if (cfg.weatherAnim) DrawWeatherIcon(g, iconX, iconY, iconSize, KindOf(text), animT);
                else
                {
                    SizeF ss = g.MeasureString(sign, fontSign);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                        g.DrawString(sign, fontSign, b, iconX + (iconSize - ss.Width) / 2f, iconY - 2 * scale);
                }
            }

            float bh = s1.Height + s2.Height - 3 * scale;
            float byy = (h - bh) / 2f;
            DrawText(g, line1, fontTemp, Color.FromArgb(244, 255, 255, 255), wRight, byy);
            DrawText(g, line2, fontCity, Color.FromArgb(168, 186, 196, 208), wRight, byy + s1.Height - 3 * scale);

            surface.Present(Handle, Left, Top, Rectangle.Empty, 1f);

            if (!loggedFirst)
            {
                loggedFirst = true;
                Config.Log("信息条: 已绘制 " + ClientSize.Width + "x" + ClientSize.Height +
                           " 时间=" + now.ToString("HH:mm") +
                           " 天气=" + (temp.Length > 0 ? temp + "°" : "还没拿到"));
            }
        }

        private bool loggedFirst;

        // ================================================================
        //  天气：wttr.in（免 key），后台拉，失败保留上一次
        // ================================================================

        private void MaybeFetchWeather()
        {
            if (!cfg.showDeskInfo) return;
            int nowMs = Clock.NowMs;
            if (fetchAtMs == 0) fetchAtMs = nowMs + 200;
            if (unchecked(nowMs - fetchAtMs) < 0) return;
            if (Interlocked.CompareExchange(ref fetching, 1, 0) != 0) return;
            fetchAtMs = nowMs + FetchIntervalMs;
            ThreadPool.QueueUserWorkItem(delegate { FetchWeather(); });
        }

        // ================================================================
        //  定位：Windows 位置服务（System.Device.Location）
        // ================================================================

        private System.Device.Location.GeoCoordinateWatcher geoWatch;
        private double geoLat, geoLon;
        private bool geoOk, geoRunning;
        private int geoStartMs, geoNextMs;
        private string geoState = "";
        private string geoCity;
        private double geoCityLat, geoCityLon;

        private const int LocateIntervalMs = 2 * 60 * 60 * 1000;
        private const int LocateTimeoutMs = 20 * 1000;
        private const double CityMoveEps = 0.05;

        private void StopGeo()
        {
            if (!geoRunning) return;
            geoRunning = false;
            geoNextMs = Clock.NowMs + LocateIntervalMs;
            try
            {
                if (geoWatch != null)
                {
                    geoWatch.Stop();
                    geoWatch.Dispose();
                    geoWatch = null;
                }
                Config.Log("定位: 已停止并释放（下次 " + (LocateIntervalMs / 60000) + " 分钟后）");
            }
            catch { }
        }

        private void StartGeo()
        {
            if (geoRunning) return;
            geoRunning = true;
            geoStartMs = Clock.NowMs;
            geoState = "已启动";
            try
            {
                geoWatch = new System.Device.Location.GeoCoordinateWatcher(
                    System.Device.Location.GeoPositionAccuracy.Default);
                geoWatch.PositionChanged += delegate(object o,
                    System.Device.Location.GeoPositionChangedEventArgs<System.Device.Location.GeoCoordinate> a)
                {
                    try
                    {
                        System.Device.Location.GeoCoordinate c = a.Position.Location;
                        if (c == null || c.IsUnknown) return;
                        bool first = !geoOk;
                        // 只有"已经查过城市名"才需要因为移动而作废缓存；
                        // 否则第一次定位时连续几帧 PositionChanged 会连着打三条日志。
                        if (geoCity != null && (Math.Abs(c.Latitude - geoCityLat) > CityMoveEps ||
                                                Math.Abs(c.Longitude - geoCityLon) > CityMoveEps))
                        {
                            geoCity = null;
                            Config.Log("定位: 位置变化较大，重新查询城市名");
                        }
                        geoLat = c.Latitude; geoLon = c.Longitude; geoOk = true;
                        if (first)
                            Config.Log("定位: 经纬度 " + geoLat.ToString("0.###") + "," + geoLon.ToString("0.###"));
                        StopGeo();
                        fetchAtMs = 0;
                        dirty = true;
                    }
                    catch { }
                };
                geoWatch.StatusChanged += delegate(object o,
                    System.Device.Location.GeoPositionStatusChangedEventArgs a)
                {
                    if (a.Status == System.Device.Location.GeoPositionStatus.Disabled)
                    {
                        try
                        {
                            geoState = geoWatch.Permission == System.Device.Location.GeoPositionPermission.Denied
                                ? "Denied" : "Disabled";
                        }
                        catch { geoState = "Disabled"; }
                        Config.Log("定位: 系统位置服务不可用（" + geoState + "），改用城市/IP 定位");
                        StopGeo();
                    }
                };
                geoWatch.Start();
                Config.Log("定位: 启动 Windows 位置服务");
            }
            catch (Exception ex)
            {
                geoState = "异常";
                Config.Log("定位: 启动失败 " + ex.Message);
                geoRunning = false;
                geoNextMs = Clock.NowMs + LocateIntervalMs;
            }
        }

        private bool TryGetLatLon()
        {
            if (geoOk) return true;
            try
            {
                if (geoWatch != null && geoWatch.Position != null)
                {
                    System.Device.Location.GeoCoordinate c = geoWatch.Position.Location;
                    if (c != null && !c.IsUnknown)
                    {
                        geoLat = c.Latitude; geoLon = c.Longitude; geoOk = true;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void FetchWeather()
        {
            try
            {
                string city = (cfg.weatherCity ?? "").Trim();
                string url;
                bool latOk = TryGetLatLon();
                if (latOk)
                    url = "https://wttr.in/~" + geoLat.ToString("0.####", CultureInfo.InvariantCulture) + "," +
                          geoLon.ToString("0.####", CultureInfo.InvariantCulture) + "?format=j1";
                else
                    url = "https://wttr.in/" + (city.Length > 0 ? Uri.EscapeDataString(city) : "") + "?format=j1";
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string json;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "curl/8.0");
                    wc.Encoding = Encoding.UTF8;
                    json = wc.DownloadString(url);
                }
                if (json == null || json.Length == 0) throw new Exception("空响应");

                string t = Pick(json, "\"temp_C\"\\s*:\\s*\"([^\"]+)\"");
                string desc = Pick(json, "\"weatherDesc\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]+)\"");
                string area = Pick(json, "\"areaName\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]+)\"");

                string shown = city.Length > 0 ? city : null;
                if (shown == null && latOk)
                {
                    if (geoCity == null)
                    {
                        geoCity = ReverseCity(geoLat, geoLon);
                        if (geoCity != null) { geoCityLat = geoLat; geoCityLon = geoLon; }
                    }
                    shown = geoCity;
                }

                lock (wlock)
                {
                    if (t != null) wTemp = t;
                    if (shown != null) wCity = shown;
                    else if (area != null) wCity = area;
                    if (desc != null) { wText = desc; wSign = SignOf(desc); }
                }
                dirty = true;
            }
            catch (Exception ex)
            {
                Config.Log("天气获取失败: " + ex.Message + "（保留上一次显示）");
            }
            finally
            {
                Interlocked.Exchange(ref fetching, 0);
            }
        }

        private static string Pick(string s, string pattern)
        {
            Match m = Regex.Match(s, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string ReverseCity(double lat, double lon)
        {
            try
            {
                string url = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&accept-language=zh&lat=" +
                             lat.ToString("0.####", CultureInfo.InvariantCulture) + "&lon=" +
                             lon.ToString("0.####", CultureInfo.InvariantCulture);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "TileDesk/1.0 (desktop tile wall)";
                req.Timeout = 8000;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string j = sr.ReadToEnd();
                    string[] keys = { "city", "municipality", "state_district", "county", "town", "village" };
                    for (int i = 0; i < keys.Length; i++)
                    {
                        string v = Pick(j, "\"" + keys[i] + "\"\\s*:\\s*\"([^\"]+)\"");
                        if (v != null && v.Length > 0) return Shorten(v);
                    }
                }
            }
            catch (Exception ex)
            {
                Config.Log("反向地理编码失败: " + ex.Message);
            }
            return null;
        }

        private static string Shorten(string s)
        {
            if (s.Length > 2 && (s.EndsWith("市") || s.EndsWith("镇") || s.EndsWith("县")))
                return s.Substring(0, s.Length - 1);
            return s;
        }

        // ================================================================
        //  天气矢量动画（纯 GDI+，不引 SVG 库）
        // ================================================================

        private static string KindOf(string desc)
        {
            string d = (desc ?? "").ToLowerInvariant();
            if (d.Contains("thunder") || d.Contains("storm")) return "thunder";
            if (d.Contains("snow") || d.Contains("sleet") || d.Contains("blizzard")) return "snow";
            if (d.Contains("rain") || d.Contains("drizzle") || d.Contains("shower")) return "rain";
            if (d.Contains("fog") || d.Contains("mist") || d.Contains("haze")) return "fog";
            if (d.Contains("overcast")) return "cloud";
            if (d.Contains("cloud")) return d.Contains("partly") || d.Contains("scattered") ? "cloudsun" : "cloud";
            if (d.Contains("clear") || d.Contains("sunny")) return "sun";
            return "cloud";
        }

        private void DrawWeatherIcon(Graphics g, float x, float y, float size, string kind, double t)
        {
            float k = size / 32f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            switch (kind)
            {
                case "sun": DrawSun(g, x, y, k, t, true); break;
                case "cloudsun": DrawSun(g, x + 9 * k, y - 1 * k, k * 0.8f, t, false); DrawCloud(g, x - 1 * k, y + 5 * k, k, t, 246); break;
                case "cloud": DrawCloud(g, x, y + 3 * k, k, t, 246); break;
                case "fog": DrawFog(g, x, y, k, t); break;
                case "rain": DrawCloud(g, x, y - 3 * k, k, t, 232); DrawRain(g, x, y + 6 * k, k, t, false); break;
                case "thunder": DrawCloud(g, x, y - 3 * k, k, t, 232); DrawRain(g, x, y + 6 * k, k, t, true); DrawBolt(g, x, y + 4 * k, k, t); break;
                case "snow": DrawCloud(g, x, y - 3 * k, k, t, 232); DrawSnow(g, x, y + 6 * k, k, t); break;
                default: DrawCloud(g, x, y + 3 * k, k, t, 246); break;
            }
        }

        private static void DrawSun(Graphics g, float x, float y, float k, double t, bool rays)
        {
            float cx = x + 16 * k, cy = y + 16 * k, r = 7.5f * k;
            if (rays)
            {
                using (Pen p = new Pen(Color.FromArgb(235, 255, 205, 80), Math.Max(1.4f, 2.1f * k)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    double rot = t * 0.45;
                    for (int i = 0; i < 12; i++)
                    {
                        double a = rot + i * Math.PI / 6;
                        float br = (float)(1.5 * k * (0.5 + 0.5 * Math.Sin(t * 2.1 + i * 0.9)));
                        float r0 = r + 2.6f * k, r1 = r0 + 2.6f * k + br;
                        g.DrawLine(p,
                            cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0,
                            cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
                    }
                }
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 210, 88)))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }

        private static GraphicsPath CloudPath(float x, float y, float k)
        {
            GraphicsPath p = new GraphicsPath();
            p.FillMode = FillMode.Winding;
            p.AddEllipse(x + 4 * k, y + 9 * k, 13 * k, 13 * k);
            p.AddEllipse(x + 10 * k, y + 4 * k, 16 * k, 16 * k);
            p.AddEllipse(x + 19 * k, y + 10 * k, 10 * k, 10 * k);
            p.AddRectangle(new RectangleF(x + 6 * k, y + 15 * k, 21 * k, 6 * k));
            return p;
        }

        private static void DrawCloud(Graphics g, float x, float y, float k, double t, int alpha)
        {
            float dx = (float)(Math.Sin(t * 0.8) * 1.6 * k);
            using (GraphicsPath p = CloudPath(x + dx, y, k))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 233, 239, 248)))
                g.FillPath(b, p);
        }

        private static void DrawRain(Graphics g, float x, float y, float k, double t, bool sparse)
        {
            int n = sparse ? 2 : 3;
            using (Pen p = new Pen(Color.FromArgb(215, 132, 182, 255), Math.Max(1.1f, 1.7f * k)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                for (int i = 0; i < n; i++)
                {
                    float ox = (10 + i * 7) * k;
                    float ph = (float)((t * 22 + i * 9.5) % 15);
                    float py = y + (2 + ph) * k;
                    g.DrawLine(p, x + ox, py, x + ox - 1.4f * k, py + 4.6f * k);
                }
            }
        }

        private static void DrawSnow(Graphics g, float x, float y, float k, double t)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 236, 243, 252)))
            {
                for (int i = 0; i < 4; i++)
                {
                    float ph = (float)((t * 11 + i * 7.5) % 15);
                    float sway = (float)(Math.Sin(t * 1.7 + i * 1.6) * 2.2);
                    float px = x + (7 + i * 6) * k + sway * k;
                    float py = y + (2 + ph) * k;
                    g.FillEllipse(b, px, py, 2.6f * k, 2.6f * k);
                }
            }
        }

        private static void DrawBolt(Graphics g, float x, float y, float k, double t)
        {
            double phase = (t % 1.6) / 1.6;
            double pulse = phase < 0.22 ? Math.Sin(phase / 0.22 * Math.PI) : 0;
            int a = (int)(110 + 145 * pulse);
            PointF[] z = new PointF[]
            {
                new PointF(x + 15.5f * k, y + 6 * k),
                new PointF(x + 11.0f * k, y + 16 * k),
                new PointF(x + 14.6f * k, y + 16 * k),
                new PointF(x + 10.5f * k, y + 25 * k),
                new PointF(x + 19.0f * k, y + 13.5f * k),
                new PointF(x + 15.0f * k, y + 13.5f * k),
                new PointF(x + 19.5f * k, y + 6 * k)
            };
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 214, 74)))
                g.FillPolygon(b, z);
        }

        private static void DrawFog(Graphics g, float x, float y, float k, double t)
        {
            using (Pen p = new Pen(Color.FromArgb(215, 226, 233, 244), Math.Max(1.6f, 2.6f * k)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                for (int i = 0; i < 3; i++)
                {
                    float yy = y + (11 + i * 6) * k;
                    float sh = (float)(Math.Sin(t * 1.1 + i * 1.3) * 3.2) * k;
                    g.DrawLine(p, x + 4 * k + sh, yy, x + 28 * k + sh * 0.6f, yy);
                }
            }
        }

        private static string SignOf(string desc)
        {
            string k = KindOf(desc);
            if (k == "sun") return "☀";
            if (k == "cloudsun") return "⛅";
            if (k == "rain") return "☂";
            if (k == "snow") return "❄";
            if (k == "thunder") return "⚡";
            return "☁";
        }

        private static string CnDesc(string desc)
        {
            string d = (desc ?? "").ToLowerInvariant();
            if (d.Contains("thunder")) return "雷";
            if (d.Contains("snow")) return "雪";
            if (d.Contains("rain") || d.Contains("drizzle") || d.Contains("shower")) return "雨";
            if (d.Contains("fog") || d.Contains("mist")) return "雾";
            if (d.Contains("overcast")) return "阴";
            if (d.Contains("cloud")) return "多云";
            if (d.Contains("clear") || d.Contains("sunny")) return "晴";
            return "";
        }

        public void Invalidate2() { dirty = true; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (iconTick != null) { iconTick.Stop(); iconTick.Dispose(); iconTick = null; }
                surface.Dispose();
                if (fontTime != null) { fontTime.Dispose(); fontTime = null; }
                if (fontDate != null) { fontDate.Dispose(); fontDate = null; }
                if (fontWeek != null) { fontWeek.Dispose(); fontWeek = null; }
                if (fontTemp != null) { fontTemp.Dispose(); fontTemp = null; }
                if (fontCity != null) { fontCity.Dispose(); fontCity = null; }
                if (fontSign != null) { fontSign.Dispose(); fontSign = null; }
            }
            base.Dispose(disposing);
        }
    }
}
