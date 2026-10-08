using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TileDesk
{
    /// <summary>
    /// 全局 GDI+ 锁。
    /// GDI+ 在多线程下并发操作（哪怕操作的是**不同**的 Bitmap）也会偶发
    /// InvalidOperationException「对象当前正在其他地方使用」（GDI+ 内部返回 ObjectBusy）。
    /// 把后台线程和 UI 线程的所有 GDI+ 操作串行化，这点开销可以忽略。
    /// </summary>
    internal static class Gdi
    {
        public static readonly object Lock = new object();
    }
    // =====================================================================
    //  数据模型
    // =====================================================================

    public class TileItem
    {
        public string name;
        public string path;
        public string kind;          // lnk | url | exe | folder | file
        public string steamId;
        public string iconSource;    // 实际用来取图标的路径（.url 会用里面的 IconFile）
        public DateTime stamp;
        public string artPath;       // 已解析到的本地封面文件

        public Bitmap icon;          // 图标（可能为 null）
        public Bitmap art;           // 竖版封面（可能为 null）
        public Bitmap header;        // 横版 header 的模糊色底（竖版封面不存在时用）
        public Color tint = Color.FromArgb(60, 70, 95);

        public float hover;          // 0..1 悬停动画进度
        public float launchAnim;     // 0..1 启动成功后的反馈动画进度（1 -> 0）
        /// <summary>
        /// 拖拽退让动画：当前所在的"小数槽位"。移动设备上拖动图标时，其他图标
        /// 是平滑滑开的而不是瞬间跳位 —— 这里就是那个动画的进度。
        /// -1 表示不在拖拽中。
        /// </summary>
        public float animSlot = -1f;
        /// <summary>
        /// 自由排列时的动画位置（小数格子）。自动排列用 animSlot（一维槽位），
        /// 自由排列是二维格子，所以另开一对 —— 拖拽时"被占位的那张往后让一让"、
        /// 松手后卡片平滑落到新格子上，都靠它插值。&lt; 0 表示不在动画中。
        /// </summary>
        public float animCol = -1f, animRow = -1f;
        /// <summary>
        /// 点击水波纹进度：0 -> 1 扩散淡出，-1 表示没有。
        /// 触摸屏上点一下要有明确反馈，不然用户不知道点中没有。
        /// </summary>
        public float ripple = -1f;
        public float rippleX, rippleY;   // 点击位置相对卡片左上角
        public bool loading;
        public string loadError;

        // ---- 卡片位图缓存（跟着项目走，不跟着槽位走）----
        // 之前缓存是"按槽位"放在 TileForm 的一个数组里，重排之后数组里还是旧顺序的
        // 图 —— 于是松手那一瞬间整面墙会用旧顺序的图重画一次，看起来就是"卡片闪回原位"。
        // 改成每张卡自己拿着自己的位图，怎么重排都不会串。
        public Bitmap cacheNormal;      // 常态卡片（含投影）
        public Bitmap cacheHover;       // 悬停态（按需生成）
        public int cacheEpoch;          // 建它时的 cacheEpoch，用来判断要不要重建
        public int cacheX, cacheY;      // 建它时卡片所在的客户区坐标（亚克力要按屏幕位置取样）
        public int cellCol = -1;        // 自由排列时的网格位置；-1 = 还没定
        public int cellRow = -1;

        /// <summary>丢掉这张卡的位图缓存（换封面、改尺寸、项目被移出列表时用）。</summary>
        public void DropCache()
        {
            if (cacheNormal != null) { cacheNormal.Dispose(); cacheNormal = null; }
            if (cacheHover != null) { cacheHover.Dispose(); cacheHover = null; }
            cacheEpoch = 0;
        }

        // 标签换行缓存（避免每帧重新测量文字）
        public string label;
        public int labelMaxW;
        public int labelLines;
        public float labelFontSize;

        public bool HasArt { get { return art != null; } }
    }

    // =====================================================================
    //  颜色工具
    // =====================================================================

    internal static class ColorUtils
    {
        public static Color FromHex(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                string s = hex.Trim().TrimStart('#');
                if (s.Length == 6)
                    return Color.FromArgb(255,
                        Convert.ToInt32(s.Substring(0, 2), 16),
                        Convert.ToInt32(s.Substring(2, 2), 16),
                        Convert.ToInt32(s.Substring(4, 2), 16));
                if (s.Length == 8)
                    return Color.FromArgb(
                        Convert.ToInt32(s.Substring(0, 2), 16),
                        Convert.ToInt32(s.Substring(2, 2), 16),
                        Convert.ToInt32(s.Substring(4, 2), 16),
                        Convert.ToInt32(s.Substring(6, 2), 16));
            }
            catch { }
            return fallback;
        }

        public static Color WithAlpha(Color c, int a) { return Color.FromArgb(Clamp(a), c.R, c.G, c.B); }

        public static Color Saturate(Color c, float f)
        {
            float h, s, l;
            ToHsl(c, out h, out s, out l);
            return FromHsl(h, Math.Min(1f, s * f), l);
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        public static void ToHsl(Color c, out float h, out float s, out float l)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2f;
            if (Math.Abs(max - min) < 0.0001f) { h = 0; s = 0; return; }
            float d = max - min;
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6f : 0f);
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h /= 6f;
        }

        public static Color FromHsl(float h, float s, float l)
        {
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                double q = l < 0.5f ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = Hue2Rgb(p, q, h + 1.0 / 3.0);
                g = Hue2Rgb(p, q, h);
                b = Hue2Rgb(p, q, h - 1.0 / 3.0);
            }
            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
            return p;
        }

        /// <summary>从图标中提取一个“主色调”（偏饱和、偏亮），失败时返回 fallback。</summary>
                public static Color Dominant(Bitmap src, Color fallback)
        {
            lock (Gdi.Lock) { return DominantRaw(src, fallback); }
        }
public static Color DominantRaw(Bitmap src, Color fallback)
        {
            if (src == null) return fallback;
            try
            {
                int n = 40;
                int[] hueWeight = new int[24];
                int[] hueR = new int[24], hueG = new int[24], hueB = new int[24];
                using (Bitmap small = new Bitmap(n, n, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, new Rectangle(0, 0, n, n));
                    }
                    BitmapData bd = small.LockBits(new Rectangle(0, 0, n, n),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = bd.Stride;
                        byte[] buf = new byte[stride * n];
                        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
                        for (int y = 0; y < n; y++)
                        {
                            for (int x = 0; x < n; x++)
                            {
                                int i = y * stride + x * 4;
                                int a = buf[i + 3];
                                if (a < 40) continue;
                                Color c = Color.FromArgb(a, buf[i + 2], buf[i + 1], buf[i]);
                                float h, s, l;
                                ToHsl(c, out h, out s, out l);
                                if (l < 0.12f || l > 0.95f) continue;
                                // 黑白灰（饱和度≈0）根本没有"色相"可言，但 ToHsl 会给它们
                                // h=0 —— 于是所有灰色图标都在给红色那一格投票，无封面卡片
                                // 那层"图标主色柔光"就变成一片红。直接跳过无彩色像素，
                                // 让真正有颜色的像素决定主色（一个都没有时走 fallback）。
                                if (s < 0.08f) continue;
                                int bucket = (int)(h * 24f) % 24;
                                int w = (int)(a * (0.25f + s * s) * (l > 0.35f ? 1.35f : 1f));
                                hueWeight[bucket] += w;
                                hueR[bucket] += c.R * w; hueG[bucket] += c.G * w; hueB[bucket] += c.B * w;
                            }
                        }
                    }
                    finally { small.UnlockBits(bd); }
                }
                int best = -1, bestW = 0, total = 0;
                for (int i = 0; i < 24; i++) { total += hueWeight[i]; if (hueWeight[i] > bestW) { bestW = hueWeight[i]; best = i; } }
                if (best < 0 || bestW < 40 || total <= 0) return fallback;
                Color dom = Color.FromArgb(255,
                    hueR[best] / bestW, hueG[best] / bestW, hueB[best] / bestW);
                float hh, ss, ll;
                ToHsl(dom, out hh, out ss, out ll);
                return FromHsl(hh, Math.Min(1f, Math.Max(0.34f, ss)), Math.Min(0.62f, Math.Max(0.34f, ll)));
            }
            catch { return fallback; }
        }
    }

    // =====================================================================
    //  图标提取
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    internal static class ShellIcons
    {
        private static readonly Guid IID_IShellItemImageFactory =
            new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr h, int n, ref BITMAP bm);
        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
            byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr h, IntPtr dc);

        private static readonly Dictionary<string, Bitmap> cache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private static readonly object cacheLock = new object();

                public static Bitmap Get(string path, int size)
        {
            lock (Gdi.Lock) { return GetRaw(path, size); }
        }
public static Bitmap GetRaw(string path, int size)
        {
            string key = path + "|" + size;
            lock (cacheLock)
            {
                Bitmap hit;
                if (cache.TryGetValue(key, out hit)) return hit;
            }
            Bitmap bmp = Extract(path, size);
            lock (cacheLock) { cache[key] = bmp; }
            return bmp;
        }

        public static void ClearCache()
        {
            lock (cacheLock)
            {
                foreach (Bitmap b in cache.Values) if (b != null) b.Dispose();
                cache.Clear();
            }
        }

        private static Bitmap Extract(string path, int size)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = IID_IShellItemImageFactory;
                IShellItemImageFactory factory = Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
                if (factory != null)
                {
                    try
                    {
                        factory.GetImage(new SIZE(size, size), SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK, out hbm);
                    }
                    finally { Marshal.ReleaseComObject(factory); }
                    if (hbm != IntPtr.Zero)
                    {
                        // 注意：不能用 Bitmap.FromHbitmap —— 它会丢掉 alpha 通道（得到 32bppRgb 全不透明）
                        Bitmap b = FromHBitmap32(hbm);
                        if (b != null && b.Width > 2 && b.Height > 2) return b;
                        if (b != null) b.Dispose();
                    }
                }
            }
            catch (Exception ex) { Config.Log("图标提取失败 " + path + " : " + ex.Message); }
            finally { if (hbm != IntPtr.Zero) Native.DeleteObject(hbm); }

            try
            {
                using (Icon ico = Icon.ExtractAssociatedIcon(path))
                {
                    if (ico != null)
                    {
                        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.Clear(Color.Transparent);
                            int s = Math.Min(size, Math.Max(ico.Width, ico.Height));
                            g.DrawIcon(ico, new Rectangle((size - s) / 2, (size - s) / 2, s, s));
                        }
                        return b;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>把 shell 返回的 32bpp HBITMAP 逐位复制成带 alpha 的 GDI+ 位图。</summary>
        private static Bitmap FromHBitmap32(IntPtr hbm)
        {
            BITMAP bm = new BITMAP();
            if (GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), ref bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = w;
            bi.biHeight = -h;         // 自顶向下
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;     // BI_RGB

            int stride = w * 4;
            byte[] buf = new byte[stride * h];
            IntPtr hdc = GetDC(IntPtr.Zero);
            int lines;
            try { lines = GetDIBits(hdc, hbm, 0, (uint)h, buf, ref bi, 0); }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
            if (lines == 0) return null;

            // 极少数情况下 shell 给的是预乘 alpha，做一次保守还原（正常直通 alpha 时不会触发）
            UnPremultiplyIfNeeded(buf, w, h, stride);

            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (bd.Stride == stride)
                {
                    Marshal.Copy(buf, 0, bd.Scan0, stride * h);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(buf, y * stride, (IntPtr)((long)bd.Scan0 + y * bd.Stride), Math.Min(stride, bd.Stride));
                }
            }
            finally { dst.UnlockBits(bd); }
            return dst;
        }

        private static void UnPremultiplyIfNeeded(byte[] buf, int w, int h, int stride)
        {
            int semi = 0, viol = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int a = buf[i + 3];
                    if (a == 0 || a == 255) continue;
                    semi++;
                    int mx = Math.Max(buf[i], Math.Max(buf[i + 1], buf[i + 2]));
                    if (mx > a + 8) viol++;
                }
            }
            if (semi < 16) return;
            if (viol * 100 / semi >= 1) return;   // 存在越界 => 直通 alpha
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int a = buf[i + 3];
                    if (a == 0 || a == 255) continue;
                    buf[i] = (byte)Math.Min(255, buf[i] * 255 / a);
                    buf[i + 1] = (byte)Math.Min(255, buf[i + 1] * 255 / a);
                    buf[i + 2] = (byte)Math.Min(255, buf[i + 2] * 255 / a);
                }
            }
        }
    }

    // =====================================================================
    //  文本读取（编码自适应）
    // =====================================================================

    internal static class TextFile
    {
        public static string Read(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
                    return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
                if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                    return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
                try
                {
                    UTF8Encoding strict = new UTF8Encoding(false, true);
                    return strict.GetString(raw);
                }
                catch { return Encoding.Default.GetString(raw); }
            }
            catch (Exception ex) { Config.Log("读取文本失败 " + path + " : " + ex.Message); return ""; }
        }
    }

    // =====================================================================
    //  Steam 封面解析
    // =====================================================================

    internal static class SteamArt
    {
        // 新资源域名（用户提供）：shared.fastly.steamstatic.com/store_item_assets/steam/apps/<id>/...
        private const string AssetBase =
            "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/";
        // 旧域名，作为兜底
        private const string LegacyBase =
            "https://cdn.cloudflare.steamstatic.com/steam/apps/";

        private static string steamRoot;
        private static bool steamRootResolved;
        private static readonly object rootLock = new object();

        public static string SteamRoot
        {
            get
            {
                lock (rootLock)
                {
                    if (steamRootResolved) return steamRoot;
                    steamRootResolved = true;
                    try
                    {
                        Microsoft.Win32.RegistryKey k =
                            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                        if (k != null)
                        {
                            object v = k.GetValue("SteamPath");
                            if (v != null)
                            {
                                string p = v.ToString().Replace('/', '\\');
                                if (Directory.Exists(p)) { steamRoot = p; k.Close(); return steamRoot; }
                            }
                            k.Close();
                        }
                    }
                    catch { }
                    string[] guesses = new string[]
                    {
                        @"C:\Program Files (x86)\Steam",
                        @"C:\Program Files\Steam",
                        @"D:\Steam", @"E:\Steam", @"D:\Program Files (x86)\Steam"
                    };
                    foreach (string g in guesses) if (Directory.Exists(g)) { steamRoot = g; return steamRoot; }
                    return null;
                }
            }
        }

        // ---------------------------------------------------------------
        //  appid 识别
        // ---------------------------------------------------------------

        /// <summary>
        /// 从任意文本里揪出 Steam appid。支持：
        ///   steam://rungameid/730   steam://install/730   steam://run/730
        ///   steam://store/730       steam://&lt;任意单词&gt;/730   steam://730
        ///   steam.exe -applaunch 730
        /// </summary>
        public static string ExtractAppId(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            int i = text.IndexOf("steam://", StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                int p = i + 8;
                int end = p;
                while (end < text.Length && text[end] != ' ' && text[end] != '"' &&
                       text[end] != '\0' && text[end] != '\r' && text[end] != '\n' &&
                       text[end] != '&' && text[end] != '?' && text[end] != '#')
                    end++;
                string body = text.Substring(p, end - p);

                int slash = body.IndexOf('/');
                string id = slash >= 0 ? LeadingDigits(body.Substring(slash + 1)) : null;
                if (!PlausibleAppId(id)) id = LeadingDigits(body);
                if (PlausibleAppId(id)) return id;

                i = text.IndexOf("steam://", i + 8, StringComparison.OrdinalIgnoreCase);
            }

            // steam.exe ... -applaunch 730
            int a = text.IndexOf("-applaunch", StringComparison.OrdinalIgnoreCase);
            if (a >= 0)
            {
                int p = a + 10;
                while (p < text.Length && (text[p] == ' ' || text[p] == '=' || text[p] == '"')) p++;
                string id = LeadingDigits(text.Substring(p));
                if (PlausibleAppId(id)) return id;
            }
            return null;
        }

        private static string LeadingDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (j <= i) return null;
            return s.Substring(i, j - i);
        }

        private static bool PlausibleAppId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (id.Length < 2 || id.Length > 9) return false;
            long v;
            if (!long.TryParse(id, out v)) return false;
            return v > 0;
        }

        /// <summary>桌面快捷方式 → appid。.url 读 URL=；.lnk 直接扫文件里的 UTF-16 字符串。</summary>
        public static string DetectAppId(string shortcutPath)
        {
            try
            {
                string ext = Path.GetExtension(shortcutPath).ToLowerInvariant();
                if (ext == ".url")
                    return ExtractAppId(TextFile.Read(shortcutPath));

                if (ext == ".lnk")
                {
                    byte[] raw = File.ReadAllBytes(shortcutPath);
                    if (raw.Length == 0 || raw.Length > 8 * 1024 * 1024) return null;
                    // .lnk 里的字符串是 UTF-16LE
                    string u16 = Encoding.Unicode.GetString(raw);
                    string id = ExtractAppId(u16);
                    if (id != null) return id;
                    return ExtractAppId(Encoding.Default.GetString(raw));
                }
            }
            catch (Exception ex) { Config.Log("解析 Steam 快捷方式失败 " + shortcutPath + " : " + ex.Message); }
            return null;
        }

        /// <summary>.url 里的 IconFile 行。</summary>
        public static string ParseIconFile(string urlPath)
        {
            string text = TextFile.Read(urlPath);
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), "IconFile", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        // ---------------------------------------------------------------
        //  本地 / 线上封面
        // ---------------------------------------------------------------

        private static readonly string[] PortraitNames = new string[]
        {
            "library_600x900.jpg",
            "library_600x900_2x.jpg",
            "library_600x900_schinese.jpg",
            "library_600x900.png"
        };

        /// <summary>Steam 客户端自己缓存的竖版封面。</summary>
        public static string LocalPortrait(string appId)
        {
            string root = SteamRoot;
            if (root == null || appId == null) return null;
            string dir = Path.Combine(Path.Combine(Path.Combine(root, "appcache"), "librarycache"), appId);
            foreach (string n in PortraitNames)
            {
                string p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string LocalHeader(string appId)
        {
            string root = SteamRoot;
            if (root == null || appId == null) return null;
            string p = Path.Combine(Path.Combine(Path.Combine(Path.Combine(root, "appcache"), "librarycache"), appId), "header.jpg");
            return File.Exists(p) ? p : null;
        }

        /// <summary>我们自己下载的缓存。带 _v2 前缀是为了丢掉早期用错域名抓下来的歪封面。</summary>
        public static string CachePath(string appId)
        {
            return Path.Combine(Config.ArtCacheDir, appId + "_portrait_v2.jpg");
        }

        public static string CachedPortrait(string appId)
        {
            if (appId == null) return null;
            string p = CachePath(appId);
            return File.Exists(p) ? p : null;
        }

        /// <summary>按优先级排列的候选地址：2x → 1x → 简体中文版 → 旧域名。</summary>
        private static string[] CandidateUrls(string appId)
        {
            return new string[]
            {
                AssetBase + appId + "/library_600x900_2x.jpg",
                AssetBase + appId + "/library_600x900.jpg",
                AssetBase + appId + "/library_600x900_schinese_2x.jpg",
                AssetBase + appId + "/library_600x900_schinese.jpg",
                LegacyBase + appId + "/library_600x900_2x.jpg",
                LegacyBase + appId + "/library_600x900.jpg"
            };
        }

        private static byte[] HttpGet(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "TileDesk/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return null;
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[32768];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex) { Config.Log("封面下载失败 " + url + " : " + ex.Message); return null; }
        }

        private static string HttpGetText(string url)
        {
            byte[] b = HttpGet(url);
            if (b == null) return null;
            try { return Encoding.UTF8.GetString(b); }
            catch { return null; }
        }

        /// <summary>
        /// 在 JSON 文本里按 "键 → 键 → 键" 的顺序找最内层的字符串值。
        /// 只用来从 Steam 商店 API 的返回里取一个 URL，不需要完整的 JSON 解析器。
        /// </summary>
        private static string JsonFindString(string json, string[] path)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int pos = 0;
            foreach (string key in path)
            {
                string needle = "\"" + key + "\"";
                int k = json.IndexOf(needle, pos, StringComparison.Ordinal);
                if (k < 0) return null;
                int colon = json.IndexOf(':', k + needle.Length);
                if (colon < 0) return null;
                pos = colon + 1;
            }
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
            if (pos >= json.Length || json[pos] != '"') return null;
            int end = pos + 1;
            while (end < json.Length && json[end] != '"')
            {
                if (json[end] == '\\') end++;
                end++;
            }
            if (end >= json.Length) return null;
            return json.Substring(pos + 1, end - pos - 1).Replace("\\/", "/");
        }

        /// <summary>
        /// 兜底：问 Steam 商店 API 要官方给的竖版封面地址。
        /// 有些游戏的素材放在带内容哈希的路径下（.../apps/&lt;id&gt;/&lt;hash&gt;/header.jpg），
        /// 光靠拼固定文件名是抓不到的，但商店 API 会直接返回真实地址。
        /// </summary>
        private static string StoreCapsuleUrl(string appId)
        {
            string json = HttpGetText("https://store.steampowered.com/api/appdetails?appids=" + appId + "&l=schinese");
            if (string.IsNullOrEmpty(json)) return null;

            string[][] tries = new string[][]
            {
                new string[] { "library_assets_full", "library_capsule", "image2x", "schinese" },
                new string[] { "library_assets_full", "library_capsule", "image2x", "english" },
                new string[] { "library_assets_full", "library_capsule", "image", "schinese" },
                new string[] { "library_assets_full", "library_capsule", "image", "english" }
            };
            foreach (string[] path in tries)
            {
                string v = JsonFindString(json, path);
                if (!string.IsNullOrEmpty(v) && v.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return v;
            }
            return null;
        }

        /// <summary>只接受竖版图，避免把 460x215 的 header 硬拉成 2:3 糊在卡片上。</summary>
        private static bool IsPortrait(byte[] data)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (Image img = Image.FromStream(ms, false, false))
                    return img.Height >= img.Width * 1.15 && img.Height >= 300;
            }
            catch { return false; }
        }

        /// <summary>
        /// 最后一级兜底：问 Steam 客户端 appinfo 的公开镜像要 library_assets_full。
        /// 有些游戏的竖版封面放在带内容哈希的目录下、文件名还可能是 library_capsule.jpg，
        /// 光拼固定文件名永远抓不到，但这里能拿到权威相对路径。
        /// </summary>
        private static string[] SteamCmdCapsuleUrls(string appId)
        {
            string json = HttpGetText("https://api.steamcmd.net/v1/info/" + appId);
            if (string.IsNullOrEmpty(json)) return null;

            string[][] tries = new string[][]
            {
                new string[] { "library_assets_full", "library_capsule", "image2x", "schinese" },
                new string[] { "library_assets_full", "library_capsule", "image2x", "english" },
                new string[] { "library_assets_full", "library_capsule", "image", "schinese" },
                new string[] { "library_assets_full", "library_capsule", "image", "english" }
            };
            List<string> urls = new List<string>();
            foreach (string[] path in tries)
            {
                string rel = JsonFindString(json, path);
                if (string.IsNullOrEmpty(rel) || rel.Length < 5) continue;
                urls.Add(AssetBase + appId + "/" + rel.TrimStart('/'));
            }
            return urls.Count > 0 ? urls.ToArray() : null;
        }

        private static string NoPortraitMarker(string appId)
        {
            return Path.Combine(Config.ArtCacheDir, appId + ".noportrait");
        }

        /// <summary>阻塞式下载竖版封面到缓存，成功返回文件路径。</summary>
        public static string DownloadPortrait(string appId)
        {
            if (appId == null) return null;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11; }
            catch { }

            // 已经确认过拿不到的，7 天内不再重复问网络
            string marker = NoPortraitMarker(appId);
            try
            {
                if (File.Exists(marker) && (DateTime.Now - File.GetLastWriteTime(marker)).TotalDays < 7)
                    return null;
            }
            catch { }

            string dest = CachePath(appId);

            // 第 1 级：按固定文件名拼地址（快，覆盖大多数游戏）
            foreach (string url in CandidateUrls(appId))
                if (TrySave(url, dest)) return dest;

            // 第 2 级：Steam 客户端 appinfo 镜像，拿带哈希目录的真实路径
            string[] sc = SteamCmdCapsuleUrls(appId);
            if (sc != null)
                foreach (string url in sc)
                    if (TrySave(url, dest)) return dest;

            // 第 3 级：商店 API 官方给的地址
            string apiUrl = StoreCapsuleUrl(appId);
            if (apiUrl != null && TrySave(apiUrl, dest)) return dest;

            Config.Log("找不到竖版封面: appid " + appId);
            try { File.WriteAllText(marker, DateTime.Now.ToString()); } catch { }
            return null;
        }

        private static bool TrySave(string url, string dest)
        {
            byte[] data = HttpGet(url);
            if (data == null || data.Length < 512) return false;
            if (!IsPortrait(data))
            {
                Config.Log("跳过非竖版封面 " + url);
                return false;
            }
            try
            {
                string tmp = dest + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                try { File.Delete(NoPortraitMarkerFromPath(dest)); } catch { }
                Config.Log("封面已缓存 " + url);
                return true;
            }
            catch (Exception ex) { Config.Log("写入封面缓存失败: " + ex.Message); return false; }
        }

        private static string NoPortraitMarkerFromPath(string destPath)
        {
            string name = Path.GetFileNameWithoutExtension(destPath);   // <id>_portrait_v2
            int us = name.IndexOf('_');
            if (us > 0) name = name.Substring(0, us);
            return Path.Combine(Path.GetDirectoryName(destPath), name + ".noportrait");
        }

        /// <summary>
        /// 兜底用的横版 header 图。竖版封面确实不存在的游戏（Demo / 未发售）拿它当色底，
        /// 卡片至少有这个游戏自己的配色，而不是一片灰。
        /// </summary>
        public static string DownloadHeader(string appId)
        {
            string local = LocalHeader(appId);
            if (local != null) return local;

            string cached = Path.Combine(Config.ArtCacheDir, appId + "_header.jpg");
            if (File.Exists(cached)) return cached;

            string json = HttpGetText("https://store.steampowered.com/api/appdetails?appids=" + appId);
            if (json == null) return null;
            string url = JsonFindString(json, new string[] { "data", "header_image" });
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

            byte[] data = HttpGet(url);
            if (data == null || data.Length < 512) return null;
            try { File.WriteAllBytes(cached, data); return cached; }
            catch (Exception ex) { Config.Log("写入 header 缓存失败: " + ex.Message); return null; }
        }

        /// <summary>非 Steam 项目：旁边放 <名字>.jpg / cover.jpg / folder.jpg 也能当封面。</summary>
        public static string CustomCover(string itemPath, string itemName)
        {
            string dir = Path.GetDirectoryName(itemPath);
            if (string.IsNullOrEmpty(dir)) return null;
            string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
            string[] bases = new string[] { itemName, "cover", "folder", "poster", "art" };
            foreach (string b in bases)
            {
                foreach (string e in exts)
                {
                    string p = Path.Combine(dir, b + e);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }
    }

    // =====================================================================
    //  模糊 / 背景
    // =====================================================================

    internal static class ImageFx
    {
                public static Bitmap CoverFit(Bitmap src, int w, int h)
        {
            lock (Gdi.Lock) { return CoverFitRaw(src, w, h); }
        }
public static Bitmap CoverFitRaw(Bitmap src, int w, int h)
        {
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);
                double sr = (double)src.Width / src.Height;
                double dr = (double)w / h;
                double sw, sh;
                if (sr > dr) { sh = src.Height; sw = sh * dr; }
                else { sw = src.Width; sh = sw / dr; }
                double sx = (src.Width - sw) / 2.0, sy = (src.Height - sh) / 2.0;
                // 双三次插值会采样到源矩形**外面**，而 GDI+ 把矩形外当成全透明，于是
                // 输出图最外一圈会带一条半透明的毛边（实测 140x210 整圈 alpha≈219）。
                // WrapMode.TileFlipXY 让越界采样按"镜像复制边缘像素"处理，毛边就没了。
                using (ImageAttributes wrap = new ImageAttributes())
                {
                    wrap.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h),
                        (float)sx, (float)sy, (float)sw, (float)sh, GraphicsUnit.Pixel, wrap);
                }
            }
            return dst;
        }

        /// <summary>三次盒式模糊的近似高斯模糊（在缩略图上做，很快）。</summary>
                public static void BoxBlur(Bitmap bmp, int radius)
        {
            lock (Gdi.Lock) { BoxBlurRaw(bmp, radius); }
        }
public static void BoxBlurRaw(Bitmap bmp, int radius)
        {
            if (radius < 1) return;
            int w = bmp.Width, h = bmp.Height;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] src = new byte[stride * h];
                byte[] tmp = new byte[stride * h];
                Marshal.Copy(bd.Scan0, src, 0, src.Length);
                for (int pass = 0; pass < 3; pass++)
                {
                    BlurH(src, tmp, w, h, stride, radius);
                    BlurV(tmp, src, w, h, stride, radius);
                }
                Marshal.Copy(src, 0, bd.Scan0, src.Length);
            }
            finally { bmp.UnlockBits(bd); }
        }

        private static void BlurH(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int div = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int sr = 0, sg = 0, sb = 0, sa = 0;
                for (int i = -r; i <= r; i++)
                {
                    int x = Clamp(i, 0, w - 1) * 4 + row;
                    sb += src[x]; sg += src[x + 1]; sr += src[x + 2]; sa += src[x + 3];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    dst[o] = (byte)(sb / div); dst[o + 1] = (byte)(sg / div);
                    dst[o + 2] = (byte)(sr / div); dst[o + 3] = (byte)(sa / div);
                    int add = Clamp(x + r + 1, 0, w - 1) * 4 + row;
                    int sub = Clamp(x - r, 0, w - 1) * 4 + row;
                    sb += src[add] - src[sub]; sg += src[add + 1] - src[sub + 1];
                    sr += src[add + 2] - src[sub + 2]; sa += src[add + 3] - src[sub + 3];
                }
            }
        }

        private static void BlurV(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int div = r * 2 + 1;
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int sr = 0, sg = 0, sb = 0, sa = 0;
                for (int i = -r; i <= r; i++)
                {
                    int y = Clamp(i, 0, h - 1) * stride + col;
                    sb += src[y]; sg += src[y + 1]; sr += src[y + 2]; sa += src[y + 3];
                }
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride + col;
                    dst[o] = (byte)(sb / div); dst[o + 1] = (byte)(sg / div);
                    dst[o + 2] = (byte)(sr / div); dst[o + 3] = (byte)(sa / div);
                    int add = Clamp(y + r + 1, 0, h - 1) * stride + col;
                    int sub = Clamp(y - r, 0, h - 1) * stride + col;
                    sb += src[add] - src[sub]; sg += src[add + 1] - src[sub + 1];
                    sr += src[add + 2] - src[sub + 2]; sa += src[add + 3] - src[sub + 3];
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // =====================================================================
    //  亚克力背景：一张全屏模糊壁纸，卡片按自己的屏幕位置从里面取样
    // =====================================================================

    internal static class Acrylic
    {
        /// <summary>模糊壁纸副本相对屏幕的缩放比例（1/4 足够，还自带一层平滑）。</summary>
        public const int Div = 4;

        /// <summary>
        /// 生成整屏的模糊壁纸缩略图，坐标与真实屏幕一一对应（除以 Div）。
        ///
        /// ★ 读**壁纸文件**，不用抓屏。
        ///   抓屏试过了：会把当时开着的其它窗口一起抓进去 —— 用户实测卡片底上出现了
        ///   一整块黑（正是编辑器窗口被糊进去的）。壁纸文件永远只有壁纸，没有这个污染。
        ///   坐标对齐靠 CoverFit 适配屏幕尺寸，正常比例下与屏幕一致。
        /// </summary>
        public static Bitmap BuildWallpaperBlur(int screenW, int screenH, double blurLogical, double dpiScale)
        {
            string wp = Native.GetWallpaperPath();
            if (string.IsNullOrEmpty(wp) || !File.Exists(wp)) return null;

            int bw = Math.Max(24, screenW / Div);
            int bh = Math.Max(14, screenH / Div);

            Image raw = null;
            Bitmap src = null;
            try
            {
                raw = Image.FromFile(wp);
                src = new Bitmap(raw);

                Bitmap small = ImageFx.CoverFit(src, bw, bh);
                int radius = (int)Math.Round(blurLogical * dpiScale / Div);
                if (radius < 2) radius = 2;
                ImageFx.BoxBlur(small, radius);
                return small;
            }
            catch (Exception ex)
            {
                Config.Log("生成亚克力壁纸失败: " + ex.Message);
                return null;
            }
            finally
            {
                if (src != null) src.Dispose();
                if (raw != null) raw.Dispose();
            }
        }
    }

    // ================================================================
    //  右键菜单项
    // ================================================================

    internal class MenuEntry
        {
            public string text;
            public Action action;
            public bool separator;
            public bool checkable;
            public bool checkedState;
            public bool enabled = true;
            public string glyph = "";          // 左侧图标（Segoe Fluent Icons 字形）
            public bool danger;                // 危险项（永久删除之类）：菜单里画成红字

            /// <summary>
            /// 按文字自动配一个 Windows 11 系统图标。
            /// 这样几十个菜单项的调用点一个都不用改，也不会漏配。
            /// </summary>
            private static string GlyphFor(string t)
            {
                if (string.IsNullOrEmpty(t)) return "";
                if (t.Contains("打开") || t.Contains("运行")) return "\uE8E5";
                if (t.Contains("重命名")) return "\uE8AC";
                if (t.Contains("删除") || t.Contains("清空")) return "\uE74D";
                if (t.Contains("回收站")) return "\uE74D";
                if (t.Contains("还原")) return "\uE777";
                if (t.Contains("设置") || t.Contains("选项")) return "\uE713";
                if (t.Contains("刷新")) return "\uE72C";
                if (t.Contains("退出") || t.Contains("关闭")) return "\uE7E8";
                if (t.Contains("添加") || t.Contains("新建") || t.Contains("新增")) return "\uE710";
                if (t.Contains("隐藏") || t.Contains("显示")) return "\uE7B3";
                if (t.Contains("排列") || t.Contains("排序") || t.Contains("对齐")) return "\uE8CB";
                if (t.Contains("锁定") || t.Contains("固定")) return "\uE72E";
                if (t.Contains("置顶") || t.Contains("最上")) return "\uE718";
                if (t.Contains("关于") || t.Contains("信息")) return "\uE946";
                if (t.Contains("编辑") || t.Contains("修改")) return "\uE70F";
                if (t.Contains("浏览") || t.Contains("位置")) return "\uE8B7";
                if (t.Contains("放大") || t.Contains("缩小")) return "\uE71E";
                if (t.Contains("帮助")) return "\uE897";
                return "\uE76C";   // 兜底：Win11 的"更多"点
            }

            public static MenuEntry Sep() { return new MenuEntry { separator = true }; }
            public static MenuEntry Item(string t, Action a)
            { return new MenuEntry { text = t, action = a, glyph = GlyphFor(t) }; }
            /// <summary>危险项（如"永久删除"）：画成红色，提醒不可恢复。</summary>
            public static MenuEntry Danger(string t, Action a)
            {
                MenuEntry e = new MenuEntry { text = t, action = a, glyph = GlyphFor(t) };
                e.danger = true;
                return e;
            }
            public static MenuEntry Check(string t, bool chk, Action a)
            {
                return new MenuEntry { text = t, action = a, checkable = true, checkedState = chk, glyph = GlyphFor(t) };
            }
        }

}

