using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileDesk
{
    // =====================================================================
    //  单调时钟
    //
    //  Environment.TickCount 是 32 位、约 49 天翻一次，而且精度只有 ~15ms。
    //  动画里到处用 (now - last) 计算 dt，翻符号那一瞬间会得到负数，
    //  于是「卡片闪一下」「动画跳一帧」。统一换成 Stopwatch 的单调时间。
    // =====================================================================

    internal static class Clock
    {
        private static readonly double freq = Stopwatch.Frequency;
        private static readonly long t0 = Stopwatch.GetTimestamp();

        /// <summary>进程启动以来的秒数（单调递增，永不回退）。</summary>
        public static double Now { get { return (Stopwatch.GetTimestamp() - t0) / freq; } }

        /// <summary>毫秒时间戳，用于超时判断（单调）。</summary>
        public static int NowMs { get { return unchecked((int)((Stopwatch.GetTimestamp() - t0) * 1000L / (long)freq)); } }

        /// <summary>a 是否已经晚于 b 指定的毫秒数。</summary>
        public static bool Passed(int stampMs, int ms) { return unchecked(NowMs - stampMs) >= ms; }
    }

    // =====================================================================
    //  帧时钟
    //
    //  为什么不用 System.Windows.Forms.Timer：
    //    WM_TIMER 的粒度就是系统 tick（默认 15.6ms），请求 8ms 也只能拿到
    //    ~64Hz 且抖动极大；请求 16ms 反而跨不过边界退化成 32Hz。
    //    想要 120Hz 平滑动画，只能自己排队等。
    //
    //  这里用「高精度可等待计时器」（Win10 1803+ 的
    //  CREATE_WAITABLE_TIMER_HIGH_RESOLUTION），在独立线程上等，精度 ~0.5ms，
    //  **不需要** timeBeginPeriod(1) —— 那个会把全系统计时器分辨率拉高，费电。
    //
    //  线程只负责「到点了就戳一下 UI 线程」，不做任何绘制，所以不会被
    //  渲染耗时影响节奏。空闲时 rate = 0，线程纯睡，一个 CPU 周期都不烧。
    // =====================================================================

    internal sealed class FrameClock : IDisposable
    {
        public const uint WM_APP_FRAME = 0x8000 + 0x51;   // WM_APP + 81

        private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        private const uint TIMER_ALL_ACCESS = 0x1F0003;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;
        private const uint INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerEx(IntPtr attrs, string name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
            IntPtr completion, IntPtr arg, bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr h);

        private readonly IntPtr wakeEvent, stopEvent, timer;
        private readonly IntPtr targetHwnd;
        private Thread thread;
        private volatile bool running = true;
        private volatile int intervalMs;          // 0 = 暂停
        private int pending;                      // 已投递但 UI 还没处理的帧（合并用）
        private int lastFps;
        private readonly bool highRes;

        /// <summary>创建时钟。<paramref name="hwnd"/> 收到 WM_APP_FRAME 就该画一帧。</summary>
        public FrameClock(IntPtr hwnd)
        {
            targetHwnd = hwnd;
            wakeEvent = CreateEvent(IntPtr.Zero, false, false, null);
            stopEvent = CreateEvent(IntPtr.Zero, true, false, null);
            timer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            highRes = timer != IntPtr.Zero;
            if (!highRes) timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);

            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "TileDesk-FrameClock";
            thread.Priority = ThreadPriority.AboveNormal;   // 只睡不干活，优先级高一点不影响别人
            thread.Start();
        }

        /// <summary>计时器是否拿到了高精度模式（日志用）。</summary>
        public bool HighResolution { get { return highRes; } }

        public int Rate { get { return lastFps; } }

        /// <summary>设置目标帧率。0 = 完全停止（空闲）。</summary>
        public void SetRate(int fps)
        {
            if (fps < 0) fps = 0;
            if (fps > 240) fps = 240;
            int ms = fps <= 0 ? 0 : Math.Max(1, (int)Math.Round(1000.0 / fps));
            if (ms == intervalMs) return;
            intervalMs = ms;
            lastFps = fps;
            SetEvent(wakeEvent);        // 让线程立刻按新节奏重排
        }

        /// <summary>立刻补一帧（输入事件用，别等下一个周期）。</summary>
        public void Kick()
        {
            if (intervalMs <= 0) return;     // 完全空闲时不因为一次悬停就开火，交给调用方 SetRate
            SetEvent(wakeEvent);
        }

        private void Loop()
        {
            IntPtr[] handles = new IntPtr[] { wakeEvent, timer, stopEvent };
            while (running)
            {
                int ms = intervalMs;
                if (ms <= 0)
                {
                    WaitForMultipleObjects(3, handles, false, INFINITE);
                    continue;
                }

                long due = -(long)ms * 10000L;       // 负数 = 相对时间（100ns 单位）
                SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);

                uint r = WaitForMultipleObjects(3, handles, false, (uint)(ms * 4 + 50));
                if (!running) break;
                if (r == WAIT_OBJECT_0 + 2) break;                 // stop
                if (r == WAIT_OBJECT_0 + 1 || r == WAIT_TIMEOUT)   // 计时器到点
                {
                    if (intervalMs <= 0) continue;
                    Post();
                }
                // r == WAIT_OBJECT_0（wake）：只是节奏变了，重新排一次
            }
        }

        private void Post()
        {
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) return;   // 上一帧还没画，丢掉这一拍
            if (targetHwnd == IntPtr.Zero) { Interlocked.Exchange(ref pending, 0); return; }
            if (!Native.PostMessage(targetHwnd, WM_APP_FRAME, IntPtr.Zero, IntPtr.Zero))
                Interlocked.Exchange(ref pending, 0);
        }

        /// <summary>UI 线程在开始处理这一帧时调用，解除「合并」标记。</summary>
        public void FrameConsumed() { Interlocked.Exchange(ref pending, 0); }

        public void Dispose()
        {
            running = false;
            try { SetEvent(stopEvent); } catch { }
            try { if (thread != null) thread.Join(400); } catch { }
            thread = null;
            if (wakeEvent != IntPtr.Zero) CloseHandle(wakeEvent);
            if (stopEvent != IntPtr.Zero) CloseHandle(stopEvent);
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    // =====================================================================
    //  分层窗口画布
    //
    //  原来三个窗口（磁贴墙 / 播放器 / 信息条）各自抄了一遍
    //  CreateDIBSection + CreateCompatibleDC + Bitmap 包装 + Present，
    //  而且 Present 每帧要 Marshal.AllocHGlobal 五次。这里合并成一份：
    //    · 一块非托管暂存区，装 UPDATELAYEREDWINDOWINFO 需要的所有子结构，
    //      只在尺寸变化时重建，平时零分配；
    //    · Graphics 对象复用，避免每帧 new/Dispose；
    //    · 支持带脏区的增量上传。
    // =====================================================================

    internal sealed class LayerSurface : IDisposable
    {
        public Bitmap Bitmap;
        public Graphics G;

        private IntPtr dib, memDc, oldObj, bits;
        private int w, h;

        // 非托管暂存：POINT dst(8) | POINT src(8) | SIZE(8) | BLENDFUNCTION(4) | RECT(16)
        private IntPtr scratch;
        private const int OffDst = 0, OffSrc = 8, OffSize = 16, OffBlend = 24, OffRect = 28;
        private const int ScratchSize = 48;

        public int Width { get { return w; } }
        public int Height { get { return h; } }
        public bool Ready { get { return Bitmap != null; } }

        /// <summary>确保画布是 w×h；尺寸没变直接返回 true，不重建。</summary>
        public bool Ensure(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            if (Bitmap != null && w == width && h == height) return true;
            Release();

            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = width;
            bi.biHeight = -height;       // 自顶向下，和 GDI+ 的坐标系一致
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try { dib = Native.CreateDIBSection(screenDc, ref bi, 0, out bits, IntPtr.Zero, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }

            if (dib == IntPtr.Zero || bits == IntPtr.Zero) { Release(); return false; }

            memDc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (memDc == IntPtr.Zero) { Release(); return false; }
            oldObj = Native.SelectObject(memDc, dib);

            try
            {
                // 直接包住 DIB 内存：GDI+ 画进去就是那块显存，省掉每帧一次全屏拷贝
                Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            }
            catch (Exception ex)
            {
                Config.Log("包装 DIB 失败: " + ex.Message);
                Release();
                return false;
            }

            G = Graphics.FromImage(Bitmap);
            G.CompositingMode = CompositingMode.SourceOver;
            G.SmoothingMode = SmoothingMode.AntiAlias;
            G.InterpolationMode = InterpolationMode.HighQualityBilinear;
            G.PixelOffsetMode = PixelOffsetMode.HighQuality;
            G.CompositingQuality = CompositingQuality.HighSpeed;
            G.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            w = width; h = height;

            if (scratch == IntPtr.Zero) scratch = Marshal.AllocHGlobal(ScratchSize);
            using (Graphics g = Graphics.FromImage(Bitmap)) g.Clear(Color.Transparent);
            return true;
        }

        /// <summary>整块擦成透明。</summary>
        public void ClearAll()
        {
            if (G == null) return;
            CompositingMode m = G.CompositingMode;
            G.CompositingMode = CompositingMode.SourceCopy;
            G.Clear(Color.Transparent);
            G.CompositingMode = m;
        }

        /// <summary>只把一块区域擦成透明（局部重绘用）。</summary>
        public void ClearRect(Rectangle r)
        {
            if (G == null) return;
            if (r.Width <= 0 || r.Height <= 0) return;
            CompositingMode m = G.CompositingMode;
            G.CompositingMode = CompositingMode.SourceCopy;
            using (SolidBrush b = new SolidBrush(Color.Transparent)) G.FillRectangle(b, r);
            G.CompositingMode = m;
        }

        /// <summary>
        /// 交给 DWM。dirty 为空 = 整窗；否则只更新那一块。
        /// constAlpha 是整窗恒定透明度（淡入淡出用，0..1）。
        /// </summary>
        public void Present(IntPtr hwnd, int screenX, int screenY, Rectangle dirty, float constAlpha)
        {
            if (memDc == IntPtr.Zero || Bitmap == null || hwnd == IntPtr.Zero) return;

            int alpha = (int)Math.Round(255 * (constAlpha < 0 ? 0 : (constAlpha > 1 ? 1 : constAlpha)));

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            try
            {
                if (scratch == IntPtr.Zero) scratch = Marshal.AllocHGlobal(ScratchSize);
                Marshal.WriteInt32(scratch, OffDst, screenX);
                Marshal.WriteInt32(scratch, OffDst + 4, screenY);
                Marshal.WriteInt32(scratch, OffSrc, 0);
                Marshal.WriteInt32(scratch, OffSrc + 4, 0);
                Marshal.WriteInt32(scratch, OffSize, w);
                Marshal.WriteInt32(scratch, OffSize + 4, h);
                Marshal.WriteByte(scratch, OffBlend, 0);        // BlendOp = AC_SRC_OVER
                Marshal.WriteByte(scratch, OffBlend + 1, 0);    // BlendFlags
                Marshal.WriteByte(scratch, OffBlend + 2, (byte)alpha);
                Marshal.WriteByte(scratch, OffBlend + 3, 1);    // AlphaFormat = AC_SRC_ALPHA

                Native.UPDATELAYEREDWINDOWINFO info = new Native.UPDATELAYEREDWINDOWINFO();
                info.cbSize = Marshal.SizeOf(typeof(Native.UPDATELAYEREDWINDOWINFO));
                info.hdcDst = screenDc;
                info.pptDst = (IntPtr)((long)scratch + OffDst);
                info.psize = (IntPtr)((long)scratch + OffSize);
                info.hdcSrc = memDc;
                info.pptSrc = (IntPtr)((long)scratch + OffSrc);
                info.crKey = 0;
                info.pblend = (IntPtr)((long)scratch + OffBlend);
                info.dwFlags = Native.ULW_ALPHA;
                info.prcDirty = IntPtr.Zero;

                if (!dirty.IsEmpty && dirty.Width > 0 && dirty.Height > 0)
                {
                    Marshal.WriteInt32(scratch, OffRect, dirty.Left);
                    Marshal.WriteInt32(scratch, OffRect + 4, dirty.Top);
                    Marshal.WriteInt32(scratch, OffRect + 8, dirty.Right);
                    Marshal.WriteInt32(scratch, OffRect + 12, dirty.Bottom);
                    info.prcDirty = (IntPtr)((long)scratch + OffRect);
                }
                Native.UpdateLayeredWindowIndirect(hwnd, ref info);
            }
            catch (Exception ex) { Config.Log("上传画面失败: " + ex.Message); }
            finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        }

        public void Release()
        {
            if (G != null) { try { G.Dispose(); } catch { } G = null; }
            if (Bitmap != null) { try { Bitmap.Dispose(); } catch { } Bitmap = null; }
            if (memDc != IntPtr.Zero)
            {
                if (oldObj != IntPtr.Zero) Native.SelectObject(memDc, oldObj);
                Native.DeleteDC(memDc);
                memDc = IntPtr.Zero; oldObj = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { Native.DeleteObject(dib); dib = IntPtr.Zero; }
            bits = IntPtr.Zero;
            w = h = 0;
        }

        public void Dispose()
        {
            Release();
            if (scratch != IntPtr.Zero) { Marshal.FreeHGlobal(scratch); scratch = IntPtr.Zero; }
        }
    }

    // =====================================================================
    //  投影工厂
    //
    //  两层投影才是「现代系统」的观感：
    //    · 接触阴影（key）—— 小半径、偏深、紧贴卡片下沿，给物体重量；
    //    · 环境阴影（ambient）—— 大半径、很淡、四周均匀，给物体体积。
    //  只有一层的时候，要么像贴纸（太小），要么像雾（太大）。
    //
    //  性能：阴影是低频信号，在**半分辨率**上模糊再放大，肉眼看不出差别，
    //  但模糊的计算量只有 1/4。整张模板按尺寸缓存，只在卡片尺寸变化时重做。
    // =====================================================================

    internal sealed class ShadowBank : IDisposable
    {
        private readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

        /// <summary>
        /// 取（或重做）一张尺寸匹配的投影模板。
        ///
        /// ★ 用字典缓存而不是单槽：悬停时卡片会放大 4.5%，影子必须跟着放大——
        ///   如果只有一张缓存，悬停/离开会反复重建（每次十几毫秒，能感觉到卡顿）。
        /// </summary>
        public Bitmap Get(int cardW, int cardH, int pad, int radius, double opacity, int blurLogical, Color color)
        {
            string key = cardW + "|" + cardH + "|" + pad + "|" + radius + "|" +
                         opacity.ToString("0.###") + "|" + blurLogical + "|" + color.R + "," + color.G + "," + color.B;
            Bitmap hit;
            if (cache.TryGetValue(key, out hit)) return hit;

            int tw = cardW + pad * 2, th = cardH + pad * 2;
            if (tw <= 0 || th <= 0) return null;

            Bitmap dst = Build(tw, th, cardW, cardH, pad, radius, opacity, blurLogical, color);
            cache[key] = dst;
            if (cache.Count > 6) EvictOldest();
            return dst;
        }

        private void EvictOldest()
        {
            string first = null;
            foreach (string k in cache.Keys) { first = k; break; }
            if (first != null)
            {
                cache[first].Dispose();
                cache.Remove(first);
            }
        }

        private static Bitmap Build(int tw, int th, int cardW, int cardH, int pad,
                                    int radius, double opacity, int blurLogical, Color color)
        {
            int halfW = Math.Max(8, tw / 2), halfH = Math.Max(8, th / 2);

            using (Bitmap amb = Mask(halfW, halfH, cardW, cardH, pad, radius, 0f, 0.58f * opacity, color))
            using (Bitmap key = Mask(halfW, halfH, cardW, cardH, pad, radius, 0f, 0.92f * opacity, color))
            {
                // 环境光：模糊半径大、形状略微放大（光会从物体边缘"漏"出去一点）
                ImageFx.BoxBlur(amb, Math.Max(2, blurLogical * 2 / 5));
                // 主光：模糊半径小、整体下移，形成"物体压在桌面上"的接触感
                ImageFx.BoxBlur(key, Math.Max(2, blurLogical / 5));

                Bitmap dst = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(amb, new Rectangle(0, 0, tw, th));
                    g.DrawImage(key, new Rectangle(0, 0, tw, th));
                }
                return dst;
            }
        }

        /// <summary>半分辨率上画一张圆角矩形蒙版。</summary>
        private static Bitmap Mask(int halfW, int halfH, int cardW, int cardH, int pad,
                                   int radius, float dy, double alpha, Color color)
        {
            Bitmap b = new Bitmap(halfW, halfH, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float k = halfW / (float)(cardW + pad * 2);
                float x = pad * k, y = pad * k;
                float w = cardW * k, h = cardH * k;
                float r = Math.Max(1.5f, radius * k);
                // 稍微内缩：最陡的衰减藏在卡片底下，露在外面的只剩平缓尾巴
                float deflate = Math.Max(1.5f, pad * k * 0.16f);
                int a = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, alpha)) * 255);
                using (GraphicsPath p = Shapes.Round(new RectangleF(x + deflate, y + dy + deflate,
                                                              w - deflate * 2, h - deflate * 2),
                                                     Math.Max(1.5f, r - deflate * 0.5f)))
                using (SolidBrush br = new SolidBrush(Color.FromArgb(a, color)))
                    g.FillPath(br, p);
            }
            return b;
        }

        public void Dispose()
        {
            foreach (Bitmap b in cache.Values) if (b != null) b.Dispose();
            cache.Clear();
        }
    }

    // =====================================================================
    //  形状 / 缓动
    // =====================================================================

    internal static class Shapes
    {
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

        public static GraphicsPath Round(Rectangle r, float radius)
        {
            return Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
        }
    }

    internal static class Ease
    {
        public static float OutCubic(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float u = 1f - t;
            return 1f - u * u * u;
        }

        public static float InOutCubic(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f;
        }

        public static float OutQuint(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float u = 1f - t;
            return 1f - u * u * u * u * u;
        }

        /// <summary>过冲一点的弹簧曲线（0..1 输入，可以略微超过 1 再回落）。</summary>
        public static float OutBack(float t, float overshoot)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float u = t - 1f;
            return 1f + (overshoot + 1f) * u * u * u + overshoot * u * u;
        }

        /// <summary>
        /// 指数收敛：和帧率无关的平滑跟随。rate 是「每秒收敛多少倍」。
        /// 比 lerp(a, b, 0.2) 好在帧率变化时速度一致。
        /// </summary>
        public static float Toward(float cur, float target, float ratePerSec, float dt)
        {
            if (ratePerSec <= 0f) return target;
            float k = 1f - (float)Math.Exp(-ratePerSec * dt);
            float v = cur + (target - cur) * k;
            if (Math.Abs(target - v) < 0.0008f) v = target;
            return v;
        }
    }

    // =====================================================================
    //  帧统计（只在日志里用，正常路径零开销）
    // =====================================================================

    internal sealed class Perf
    {
        public int Frames, FullFrames, PartialFrames, FadeFrames, Skipped;
        public double TotalMs, DrawMs, PresentMs, FullMs;
        private double windowStart;

        public void Frame(bool full, double draw, double present)
        {
            Frames++;
            if (full) { FullFrames++; FullMs += draw + present; }
            else PartialFrames++;
            DrawMs += draw;
            PresentMs += present;
            TotalMs += draw + present;
        }

        public void Fade(double ms) { Frames++; FadeFrames++; TotalMs += ms; PresentMs += ms; }

        /// <summary>每 60 秒往日志里写一行；这段时间没画过帧就什么都不写。</summary>
        public void Tick()
        {
            double now = Clock.Now;
            if (windowStart == 0) { windowStart = now; return; }
            if (now - windowStart < 60) return;
            if (Frames > 0)
            {
                int n = Math.Max(1, Frames);
                Config.Log("帧统计: 60 秒内 " + Frames + " 帧（整窗 " + FullFrames + " / 局部 " + PartialFrames +
                           " / 淡出 " + FadeFrames + "），平均 " + (TotalMs / n).ToString("0.0") + " ms/帧" +
                           "（绘制 " + (DrawMs / n).ToString("0.0") + " + 上传 " + (PresentMs / n).ToString("0.0") + "）" +
                           (FullFrames > 0 ? "，整窗 " + (FullMs / Math.Max(1, FullFrames)).ToString("0.0") + " ms/帧" : ""));
            }
            Reset();
        }

        public void Reset()
        {
            Frames = FullFrames = PartialFrames = FadeFrames = Skipped = 0;
            TotalMs = DrawMs = PresentMs = FullMs = 0;
            windowStart = Clock.Now;
        }
    }
}
