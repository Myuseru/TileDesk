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
    /// 跟「正在播放」控件一样是**独立的分层窗口**，理由不同：这块内容要求**常驻**，
    /// 而磁贴墙空闲 8 秒会淡出、整体变成透明 —— 画在墙上就跟着没了。独立窗口就
    /// 不受墙的 fadeLevel 影响，永远可见。
    ///
    /// 位置放在右下角按钮（垃圾桶/齿轮）左边：那块地方因为播放器控件更高，
    /// 早就被划进"卡片禁区"了，所以不会和卡片抢地盘。
    /// </summary>
    internal sealed class DeskInfoWindow : Form
    {
        private readonly float scale;
        private readonly Config cfg;

        private Bitmap surface;
        private IntPtr memDc = IntPtr.Zero, dib = IntPtr.Zero;
        private int surfW, surfH;
        private bool dirty = true;

        // 天气（后台线程拉，锁保护）
        private readonly object wlock = new object();
        private string wTemp = "", wText = "", wSign = "", wCity = "";
        private int fetchAtMs;              // 下次该拉的时间（Environment.TickCount）
        private int fetching;               // 0/1，防止并发拉
        private const int FetchIntervalMs = 20 * 60 * 1000;   // 20 分钟

        private string lastName = "";        // 上一次画的内容，变了才重绘
        private double animT;                // 动画时间（秒）
        private System.Windows.Forms.Timer iconTick;   // 图标动画自己的节拍（10fps，够顺又省电）
        private Font fontTime, fontDate, fontWeek, fontTemp, fontCity;
        private Font fontSign;

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

            // 图标动画的节拍：10fps。墙空闲时 tick 是 120ms，指望不上它，所以自己走一个。
            // 只有开关打开、窗口可见时才真的重绘（见 Tick 里那两行判断）。
            iconTick = new System.Windows.Forms.Timer();
            iconTick.Interval = 100;
            iconTick.Tick += delegate
            {
                if (IsDisposed || !cfg.weatherAnim || !Visible) return;
                animT = unchecked(Environment.TickCount) / 1000.0;
                dirty = true;
            };
            iconTick.Start();
        }

        /// <summary>逻辑 360x88（物理尺寸再乘 scale）。</summary>
        public int Width2() { return (int)Math.Round(360 * scale); }
        public int Height2() { return (int)Math.Round(88 * scale); }

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
        //  由磁贴墙的 tick 驱动
        // ================================================================

        /// <summary>
        /// 同步位置/层级并重绘。
        /// <paramref name="assertZ"/> 为 false 时**不动 z 序** —— 磁贴墙开菜单时会把自己
        /// 临时提到 TOPMOST 就为了画菜单，这时候如果信息条还去抢"我要在墙上面"，
        /// 菜单就会被它和播放器控件盖住（用户实测：菜单底部那行版本被时钟挡了）。
        /// </summary>
        public void Sync(IntPtr above, Point location, bool assertZ)
        {
            if (IsDisposed) return;
            if (animT == 0) animT = unchecked(Environment.TickCount) / 1000.0;
            // 定位调度：到点了就取一次（首次立即、之后每 2 小时）；取的时间过长就放弃
            if (!geoRunning)
            {
                if (unchecked(Environment.TickCount - geoNextMs) >= 0) StartGeo();
            }
            else if (unchecked(Environment.TickCount - geoStartMs) > LocateTimeoutMs)
            {
                Config.Log("定位: 超时没结果，放弃本次（改用城市/IP 定位）");
                StopGeo();
            }
            if (!Visible) { Location = location; Show(); dirty = true; }
            else if (Location != location) Location = location;

            if (assertZ && above != IntPtr.Zero && IsHandleCreated)
            {
                // SetWindowPos(hWnd, hWndInsertAfter) = 把 hWnd 放到 hWndInsertAfter 下面；
                // 要"在墙上面"就得把**墙**插到自己下面。
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
            return now.ToString("HH:mm") + "|" + now.ToString("M月d日") + "|" + WeekName(now) + "|" +
                   now.Hour + "|" + w;
        }

        private static string WeekName(DateTime t)
        {
            string[] n = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            return n[(int)t.DayOfWeek];
        }

        // ================================================================
        //  绘制
        // ================================================================

        private void EnsureFonts()
        {
            if (fontTime != null) return;
            string fam = "Microsoft YaHei UI";
            fontTime = new Font(fam, Math.Max(20f, 36f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontDate = new Font(fam, Math.Max(10f, 15f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontWeek = new Font(fam, Math.Max(9f, 13f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontTemp = new Font(fam, Math.Max(11f, 17f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontCity = new Font(fam, Math.Max(9f, 12.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            fontSign = new Font("Segoe UI Symbol", Math.Max(18f, 34f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void Draw()
        {
            if (!EnsureSurface()) return;
            dirty = false;

            DateTime now = DateTime.Now;
            string temp, text, sign, city;
            lock (wlock) { temp = wTemp; text = wText; sign = wSign; city = wCity; }

            using (Graphics g = Graphics.FromImage(surface))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                EnsureFonts();
                int h = surfH;
                float padL = 2 * scale, padR = 2 * scale;

                // ---- 时间（左，大号）----
                string clock = now.ToString("HH:mm");
                SizeF szT = g.MeasureString(clock, fontTime);
                float x = padL;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 255, 255, 255)))
                    g.DrawString(clock, fontTime, b, x, (h - szT.Height) / 2f);
                x += szT.Width + 16 * scale;

                // 分隔线
                using (Pen p = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, scale)))
                    g.DrawLine(p, x, h * 0.22f, x, h * 0.78f);
                x += 16 * scale;

                // ---- 日期 + 星期（中，两行）----
                string d1 = now.ToString("M月d日");
                string d2 = WeekName(now);
                SizeF sd1 = g.MeasureString(d1, fontDate);
                SizeF sd2 = g.MeasureString(d2, fontWeek);
                float blockH = sd1.Height + sd2.Height - 2 * scale;
                float y0 = (h - blockH) / 2f;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(228, 255, 255, 255)))
                    g.DrawString(d1, fontDate, b, x, y0);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 178, 186)))
                    g.DrawString(d2, fontWeek, b, x, y0 + sd1.Height - 2 * scale);

                // ---- 图标：夹在日期块和天气块中间的空位上，放大 ----
                // 布局：[时间] │ [日期/周几]      [☀ 大图标]      [28° / 城市 晴]
                string line1 = temp.Length > 0 ? temp + "°" : "--";
                string wd = CnDesc(text);
                string line2 = (city.Length > 0 ? city : "定位中") + (wd.Length > 0 ? " " + wd : "");
                SizeF s1 = g.MeasureString(line1, fontTemp);
                SizeF s2 = g.MeasureString(line2, fontCity);
                float wBlock = Math.Max(s1.Width, s2.Width);
                float wRight = surfW - padR - wBlock - 24 * scale;     // 温度和地名整体再往左挪 24px

                SizeF sIcon = new SizeF(34 * scale, 34 * scale);
                float iconRight = wRight - 12 * scale;                // 图标与天气块之间只留 12px
                float iconX = iconRight - sIcon.Width;
                float iconY = (h - sIcon.Height) / 2f;
                // 别压到中间那块日期上；位置不够就贴着日期右边（留 6px）
                float minIconX = x + 6 * scale;
                if (iconX < minIconX) iconX = minIconX;
                string kind = KindOf(text);
                if (text.Length > 0)
                {
                    if (cfg.weatherAnim)
                        DrawWeatherIcon(g, iconX, iconY, sIcon.Width, kind, animT);
                    else
                    {
                        SizeF ss = g.MeasureString(sign, fontSign);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                            g.DrawString(sign, fontSign, b, iconX + (sIcon.Width - ss.Width) / 2f, iconY);
                    }
                }

                float bh = s1.Height + s2.Height - 2 * scale;
                float byy = (h - bh) / 2f;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    g.DrawString(line1, fontTemp, b, wRight, byy);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 178, 186)))
                    g.DrawString(line2, fontCity, b, wRight, byy + s1.Height - 2 * scale);
            }

            Present();
            if (!loggedFirst)
            {
                loggedFirst = true;
                Config.Log("信息条: 已绘制 " + surfW + "x" + surfH + " 时间=" + now.ToString("HH:mm") +
                           " 天气=" + (temp.Length > 0 ? temp + "°" : "还没拿到") +
                           " 城市=" + (city.Length > 0 ? city : "-"));
            }
        }

        private bool loggedFirst;

        // ================================================================
        //  天气：wttr.in（免 key），后台拉，失败保留上一次
        // ================================================================

        private void MaybeFetchWeather()
        {
            if (!cfg.showDeskInfo) return;
            int nowMs = unchecked(Environment.TickCount);
            // 启动就刷（只留 200ms 让窗口先出来），别让用户打开时看到"--"
            if (fetchAtMs == 0) fetchAtMs = nowMs + 200;
            if (unchecked(nowMs - fetchAtMs) < 0) return;
            if (Interlocked.CompareExchange(ref fetching, 1, 0) != 0) return;
            fetchAtMs = nowMs + FetchIntervalMs;
            ThreadPool.QueueUserWorkItem(delegate { FetchWeather(); });
        }

        // ================================================================
        //  定位：Windows 位置服务（System.Device.Location）
        //
        //  GeoCoordinateWatcher 内部走的就是系统位置接口 —— 和「设置 → 隐私和安全性 →
        //  位置」里那份"最近访问过位置的程序"名单是同一套，比按 IP 猜准得多。
        //  它是**事件驱动**的（Start 之后不阻塞），所以放在 UI 线程启动也不会卡住墙：
        //  第一次启动时系统可能弹一次权限框，用户点过就长期有效。
        //  没授权 / 定位服务关闭 -> PositionChanged 不会来，自动退回城市/IP 定位。
        // ================================================================

        private System.Device.Location.GeoCoordinateWatcher geoWatch;
        private double geoLat, geoLon;
        private bool geoOk, geoRunning;
        private int geoStartMs, geoNextMs;   // 本次开始时间 / 下次该取的时间
        private string geoState = "";
        private string geoCity;              // 反向地理编码结果缓存
        private double geoCityLat, geoCityLon;

        /// <summary>
        /// 定位节奏：**每 2 小时取一次**。
        ///
        /// 取一次就够了吗？不够 —— 笔记本会移动，城市/天气都得跟着变，所以隔一段时间
        /// 要重新取。但也不能一直挂着：GeoCoordinateWatcher 一旦 Start() 就持续监听，
        /// 系统「设置 → 隐私和安全性 → 位置」里会一直显示"正在访问位置"（也确实费电）。
        /// 所以每次都是「启动 → 拿到就 Dispose 释放 → 排下一次」，两次之间完全没有会话。
        /// </summary>
        private const int LocateIntervalMs = 2 * 60 * 60 * 1000;
        private const int LocateTimeoutMs = 20 * 1000;
        /// <summary>坐标变动超过这个度数（约 5 公里）就重查城市名。</summary>
        private const double CityMoveEps = 0.05;

        /// <summary>
        /// 用完立刻停掉并释放定位。
        ///
        /// 只 Stop() 是不够的：底层的位置会话还挂着，系统记录里会一直显示在访问位置
        /// （实测：只 Stop 时注册表 LastUsedTimeStop 是空的）。必须 Dispose。
        /// </summary>
        private void StopGeo()
        {
            if (!geoRunning) return;
            geoRunning = false;
            geoNextMs = unchecked(Environment.TickCount + LocateIntervalMs);   // 排下一次
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
            geoStartMs = unchecked(Environment.TickCount);
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
                        // 移动了足够远就把城市名缓存作废，重新反查一次
                        if (geoOk && (Math.Abs(c.Latitude - geoCityLat) > CityMoveEps ||
                                      Math.Abs(c.Longitude - geoCityLon) > CityMoveEps))
                        {
                            geoCity = null;
                            Config.Log("定位: 位置变化较大，重新查询城市名");
                        }
                        geoLat = c.Latitude; geoLon = c.Longitude; geoOk = true;
                        if (first)
                            Config.Log("定位: 经纬度 " + geoLat.ToString("0.###") + "," + geoLon.ToString("0.###") +
                                       "（Windows 位置服务）");
                        StopGeo();       // ★ 拿到就释放，别让系统一直记着它在读位置
                        fetchAtMs = 0;   // 位置变了天气也要跟着重取
                        dirty = true;
                    }
                    catch { }
                };
                geoWatch.StatusChanged += delegate(object o, System.Device.Location.GeoPositionStatusChangedEventArgs a)
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
                        StopGeo();       // 拿不到也别一直挂着
                    }
                };
                geoWatch.Start();
                Config.Log("定位: 启动 Windows 位置服务（每 " + (LocateIntervalMs / 3600000) + " 小时一次，取到就释放）");
            }
            catch (Exception ex)
            {
                geoState = "异常";
                Config.Log("定位: 启动失败 " + ex.Message + "（改用城市/IP 定位）");
                geoRunning = false;
                geoNextMs = unchecked(Environment.TickCount + LocateIntervalMs);
            }
        }

        /// <summary>后台线程：拿经纬度（Windows 位置服务给了就用，没给返回 false）。</summary>
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
                    wc.Headers.Add("User-Agent", "curl/8.0");     // wttr.in 认这个
                    wc.Encoding = Encoding.UTF8;
                    json = wc.DownloadString(url);
                }
                if (json == null || json.Length == 0) throw new Exception("空响应");

                string t = Pick(json, "\"temp_C\"\\s*:\\s*\"([^\"]+)\"");
                string desc = Pick(json, "\"weatherDesc\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]+)\"");
                string area = Pick(json, "\"areaName\"\\s*:\\s*\\[\\s*\\{\\s*\"value\"\\s*:\\s*\"([^\"]+)\"");

                // 显示用的地名：**只显示到市**（"中山"），但天气本身仍按精确定位取（横栏），
                // 这样既准又不刺眼。wttr.in 的 areaName 只到镇级（Henglan），所以另找
                // Nominatim 做一次反向地理编码拿市级中文名；失败就退回 areaName。
                string shown = city.Length > 0 ? city : null;             // 用户在设置里填的最高优先
                if (shown == null && latOk)
                {
                    // 城市名只在第一次（或位置明显变化后）反查一次并缓存；
                    // 记下这次用的坐标，供"是否移动过"的比较。
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

        /// <summary>
        /// 经纬度 → 市级中文地名（Nominatim/OSM，免 key）。
        /// 优先级 city → municipality → state_district → county → town → village，
        /// 也就是"能到市就到市"，市拿不到才退到镇。返回前削掉结尾的"市/镇/县"。
        /// </summary>
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
                Config.Log("反向地理编码失败: " + ex.Message + "（地名退回 wttr.in 的 areaName）");
            }
            return null;
        }

        private static string Shorten(string s)
        {
            // 中山市 -> 中山、横栏镇 -> 横栏。只削"市/镇/县"，
            // 不敢动"区"——那会把"东区"削成"东"。
            if (s.Length > 2 && (s.EndsWith("市") || s.EndsWith("镇") || s.EndsWith("县")))
                return s.Substring(0, s.Length - 1);
            return s;
        }

        // ================================================================
        //  天气矢量动画（纯 GDI+，不引 SVG 库）
        //
        //  统一按 32x32 的逻辑网格画，再按 k 缩放到需要的尺寸。
        //  t 是秒级时间，所有动作都由它推出来（无状态，随便重绘）。
        // ================================================================

        /// <summary>把 wttr.in 的英文描述映射成图标种类。</summary>
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
                    double rot = t * 0.45;                       // 光芒整体缓慢转
                    for (int i = 0; i < 12; i++)
                    {
                        double a = rot + i * Math.PI / 6;
                        float br = (float)(1.5 * k * (0.5 + 0.5 * Math.Sin(t * 2.1 + i * 0.9)));   // 呼吸
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
            // 必须用 Winding：默认的 Alternate 会把重叠部分挖空，三个圆之间就出现接缝
            p.FillMode = FillMode.Winding;
            p.AddEllipse(x + 4 * k, y + 9 * k, 13 * k, 13 * k);     // 左
            p.AddEllipse(x + 10 * k, y + 4 * k, 16 * k, 16 * k);    // 中（大）
            p.AddEllipse(x + 19 * k, y + 10 * k, 10 * k, 10 * k);   // 右
            p.AddRectangle(new RectangleF(x + 6 * k, y + 15 * k, 21 * k, 6 * k));
            return p;
        }

        private static void DrawCloud(Graphics g, float x, float y, float k, double t, int alpha)
        {
            float dx = (float)(Math.Sin(t * 0.8) * 1.6 * k);        // 云飘
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
                    float ph = (float)((t * 22 + i * 9.5) % 15);     // 0..15 循环下落
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
            // 每 ~1.6 秒闪一下，闪的时候亮度冲高再回落
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
                    float sh = (float)(Math.Sin(t * 1.1 + i * 1.3) * 3.2) * k;   // 雾带漂移
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

        // ================================================================
        //  分层窗口表面（和 NowPlayingWidget 同一套做法）
        // ================================================================

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

                if (dib == IntPtr.Zero || bits == IntPtr.Zero) { Config.Log("信息条: CreateDIBSection 失败"); return false; }
                memDc = Native.CreateCompatibleDC(IntPtr.Zero);
                if (memDc == IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; return false; }
                Native.SelectObject(memDc, dib);

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

        /// <summary>强制重排（设置里改了尺寸/开关时调用）。</summary>
        public void Invalidate2() { dirty = true; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (iconTick != null) { iconTick.Stop(); iconTick.Dispose(); iconTick = null; }
                lock (Gdi.Lock)
                {
                    if (surface != null) { surface.Dispose(); surface = null; }
                    if (memDc != IntPtr.Zero) { Native.DeleteDC(memDc); memDc = IntPtr.Zero; }
                    if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
                }
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
