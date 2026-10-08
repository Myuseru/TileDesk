using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileDesk
{
    /// <summary>
    /// 系统音频频谱（WASAPI 回环采集 + 自写 FFT）。
    ///
    /// 和 AudioMeter 的区别：AudioMeter 只能拿到一个总电平峰值，看看音量大小；
    /// 这里是把**默认播放设备正在输出的声音**回环抓下来（IAudioClient + LOOPBACK），
    /// 加汉宁窗做 1024 点 FFT，再按对数频段合并成若干根柱子 —— 所以高中低频是分开的。
    ///
    /// 全程 COM 互操作，不依赖任何外部库。抓取在后台线程，主线程只读结果。
    /// </summary>
    internal static class AudioSpectrum
    {
        // ---------------- COM ----------------
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                           IntPtr format, IntPtr sessionGuid);
            int GetBufferSize(out int frames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out int padding);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            int GetMixFormat(out IntPtr format);
            int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr handle);
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePos, out long qpcPos);
            int ReleaseBuffer(int frames);
            int GetNextPacketSize(out int frames);
        }

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr p);

        private const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        // ---------------- 状态 ----------------
        private const int FFT_SIZE = 1024;
        private static readonly float[] samples = new float[FFT_SIZE * 4];   // 环形缓冲
        private static int writePos;
        private static readonly byte[] tmp4 = new byte[4];   // 读样本用，避免每帧大量分配
        private static int channels = 2, sampleRate = 48000;
        private static volatile bool running;
        private static Thread thread;
        private static volatile bool ready;
        private static int failCount;

        private static float[] bands = new float[0];
        private static float[] bandRaw = new float[0];   // 平滑前的目标值
        private static int lastTick;

        /// <summary>柱子下落时间常数（秒），由主程序按设置写入。</summary>
        public static float FallSec = 0.12f;

        /// <summary>最近是否抓到了数据。</summary>
        public static bool Ready { get { return ready; } }

        private static int lastLoudTick;

        /// <summary>
        /// 当前是否"有声音"。柱子超过阈值算有；并且**保持 2 秒** ——
        /// 歌曲段落之间会有一瞬间安静，不加保持的话墙会在那一瞬间开始淡出，
        /// 淡到 0 之后整个消失（用户实测："显示了几秒后就消失了"）。
        /// </summary>
        public static bool Active
        {
            get
            {
                bool loud = false;
                for (int i = 0; i < bands.Length; i++)
                    if (bands[i] > 0.015f) { loud = true; break; }
                int now = unchecked(Environment.TickCount);
                if (loud) lastLoudTick = now;
                return loud || (lastLoudTick != 0 && unchecked(now - lastLoudTick) < 2000);
            }
        }

        public static void Start()
        {
            if (running || failCount > 3) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "TileDesk-Spectrum";
            thread.Start();
        }

        private static void Stop()
        {
            running = false;
            thread = null;
        }

        /// <summary>
        /// 采集线程：会话断了就自己重连。
        /// 切歌、换输出设备、播放器重启都可能让 IAudioClient 失效；
        /// 原来一次失败就永久退出（表现是频谱从此不动甚至消失），现在会自动重来。
        /// </summary>
        private static void Loop()
        {
            int attempt = 0;
            while (running)
            {
                CaptureSession();
                if (!running) break;
                ready = false;
                attempt++;
                if (attempt > 40) { Config.Log("频谱: 重连次数过多，放弃"); break; }
                Config.Log("频谱: 采集中断，1 秒后重连（第 " + attempt + " 次）");
                for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
            }
        }

        private static void CaptureSession()
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            try
            {
                Config.Log("频谱: 开始初始化回环采集");
                object enObj = new MMDeviceEnumeratorComObject();
                IMMDeviceEnumerator en = (IMMDeviceEnumerator)enObj;
                IMMDevice dev;
                int hr1 = en.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev);
                if (hr1 != 0 || dev == null) { Config.Log("频谱: 取默认播放设备失败 hr=" + hr1); Fail(); return; }

                Guid iid = IID_IAudioClient;
                object o;
                int hr2 = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
                if (hr2 != 0 || o == null) { Config.Log("频谱: 激活 IAudioClient 失败 hr=" + hr2); Fail(); return; }
                client = (IAudioClient)o;

                IntPtr fmt;
                int hr3 = client.GetMixFormat(out fmt);
                if (hr3 != 0 || fmt == IntPtr.Zero) { Config.Log("频谱: GetMixFormat 失败 hr=" + hr3); Fail(); return; }
                try
                {
                    short tag = Marshal.ReadInt16(fmt, 0);
                    channels = Marshal.ReadInt16(fmt, 2);
                    sampleRate = Marshal.ReadInt32(fmt, 4);
                    short bits = Marshal.ReadInt16(fmt, 14);
                    if (channels < 1) channels = 2;
                    if (sampleRate < 8000) sampleRate = 48000;
                    // 共享模式的混音格式几乎总是 32 位浮点；不是的话直接放弃（别去猜别的布局）
                    if (bits != 32 || (tag != 3 && tag != unchecked((short)0xFFFE)))
                    {
                        Config.Log("频谱: 混音格式不是 32 位浮点（tag=" + tag + " bits=" + bits + "），放弃");
                        Fail();
                        return;
                    }

                    // 100ms 缓冲，回环采集
                    int hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                                               1000000L, 0L, fmt, IntPtr.Zero);
                    if (hr != 0) { Fail(); return; }
                }
                finally { CoTaskMemFree(fmt); }

                Guid capIid = IID_IAudioCaptureClient;
                object capObj;
                int hr4 = client.GetService(ref capIid, out capObj);
                if (hr4 != 0 || capObj == null) { Config.Log("频谱: GetService 失败 hr=" + hr4); Fail(); return; }
                capture = (IAudioCaptureClient)capObj;
                int hr5 = client.Start();
                if (hr5 != 0) { Config.Log("频谱: Start 失败 hr=" + hr5); Fail(); return; }
                ready = true;
                Config.Log("频谱: 回环采集已启动 " + sampleRate + "Hz " + channels + "ch");

                while (running)
                {
                    int packet;
                    if (capture.GetNextPacketSize(out packet) != 0) break;
                    if (packet == 0) { Thread.Sleep(4); continue; }

                    while (packet > 0)
                    {
                        IntPtr data; int frames, flags; long dp, qp;
                        if (capture.GetBuffer(out data, out frames, out flags, out dp, out qp) != 0) break;
                        try
                        {
                            if (frames > 0 && data != IntPtr.Zero)
                            {
                                // 只在有声音的包里搬数据；静音包（flags & 2）直接当成 0
                                bool silent = (flags & 0x2) != 0;
                                for (int f = 0; f < frames; f++)
                                {
                                    float v = 0f;
                                    if (!silent)
                                    {
                                        float sum = 0f;
                                        for (int c = 0; c < channels; c++)
                                        {
                                            // 复用 tmp4，别在每帧几万个样本上做分配
                                            Marshal.Copy(data + (f * channels + c) * 4, tmp4, 0, 4);
                                            sum += BitConverter.ToSingle(tmp4, 0);
                                        }
                                        v = sum / channels;
                                    }
                                    samples[writePos] = v;
                                    writePos = (writePos + 1) % samples.Length;
                                }
                            }
                        }
                        finally { capture.ReleaseBuffer(frames); }
                        if (capture.GetNextPacketSize(out packet) != 0) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Config.Log("频谱: 采集失败 " + ex.Message);
                Fail();
            }
            finally
            {
                try { if (client != null) client.Stop(); } catch { }
                ready = false;
            }
        }

        private static void Fail()
        {
            failCount++;
            ready = false;
            // 不设 running=false —— 外层的重连循环还要继续跑
        }

        // ---------------- FFT & 频段 ----------------
        private static float[] re = new float[FFT_SIZE];
        private static float[] im = new float[FFT_SIZE];
        private static float[] window;
        private static float[] mag = new float[FFT_SIZE / 2];
        private static float[] magSmooth = new float[FFT_SIZE / 2];   // 逐 bin 时间平滑，压住单帧抖动

        /// <summary>
        /// 返回 n 根柱子的高度（0..1），按对数频率分布（低 → 高）。
        /// 拿不到音频时返回全 0。
        /// </summary>
        public static float[] Bands(int n)
        {
            if (n < 4) n = 4;
            if (bands.Length != n) { bands = new float[n]; bandRaw = new float[n]; for (int i = 0; i < n; i++) { bands[i] = 0f; bandRaw[i] = 0f; } }

            int now = unchecked(Environment.TickCount);
            float dt = lastTick == 0 ? 0.033f : Math.Min(0.2f, (now - lastTick) / 1000f);
            lastTick = now;

            if (!ready)
            {
                // 采集没起来就慢慢落回 0，别一上一下乱跳
                for (int i = 0; i < n; i++) bands[i] *= 0.85f;
                return bands;
            }

            if (window == null)
            {
                window = new float[FFT_SIZE];
                for (int i = 0; i < FFT_SIZE; i++)
                    window[i] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1)));
            }

            // 取最近 FFT_SIZE 个样本（环形缓冲里最后写入的那一段），加窗
            int start = (writePos - FFT_SIZE + samples.Length) % samples.Length;
            for (int i = 0; i < FFT_SIZE; i++)
            {
                re[i] = samples[(start + i) % samples.Length] * window[i];
                im[i] = 0f;
            }
            Fft(re, im);

            for (int i = 0; i < FFT_SIZE / 2; i++)
            {
                float m0 = (float)Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
                // 相邻帧之间做指数平滑：单帧 FFT 的幅值抖动很大，不平滑柱子会一格一格跳
                // （用户要求："采样率不高的话就做平滑"）。起得快、落得慢。
                float kk = m0 > magSmooth[i] ? 1f - (float)Math.Exp(-dt / 0.030f)
                                              : 1f - (float)Math.Exp(-dt / 0.090f);
                magSmooth[i] += (m0 - magSmooth[i]) * kk;
                mag[i] = magSmooth[i];
            }

            // 对数频段：40Hz → min(16kHz, 奈奎斯特)
            double fMin = 40.0, fMax = Math.Min(16000.0, sampleRate / 2.0 - 1);
            double binHz = (double)sampleRate / FFT_SIZE;
            for (int b = 0; b < n; b++)
            {
                double f0 = fMin * Math.Pow(fMax / fMin, (double)b / n);
                double f1 = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / n);
                int i0 = (int)Math.Floor(f0 / binHz);
                int i1 = (int)Math.Ceiling(f1 / binHz);
                if (i1 <= i0) i1 = i0 + 1;
                if (i0 < 1) i0 = 1;
                if (i1 > mag.Length) i1 = mag.Length;

                float mx = 0f;
                for (int i = i0; i < i1; i++) mx += mag[i];
                mx /= Math.Max(1, i1 - i0);      // 频段内取平均：取峰值会让单根柱子忽高忽低

                // 转成近似 dB 再归一化：低频能量大，不这样处理低频永远顶满
                float db = 20f * (float)Math.Log10(mx / (FFT_SIZE / 4f) + 1e-7f);
                float v = (db + 62f) / 62f;          // -62dB → 0，0dB → 1
                if (v < 0f) v = 0f;
                if (v > 1f) v = 1f;
                v *= 0.55f + 0.45f * (float)Math.Sin(Math.PI * ((double)b / n));   // 轻微钟形，看着自然

                bandRaw[b] = v;
            }

            // 左右平滑：相邻柱子之间再抹两遍（两端按边值处理）。
            // 光有上下（时间）平滑不够 —— 相邻柱子各跳各的，横向看还是毛躁
            // （用户要求："除了上下平滑，也做左右平滑"）。
            for (int pass = 0; pass < 2; pass++)
            {
                float prev = bandRaw[0];
                for (int b = 0; b < n; b++)
                {
                    float next = b < n - 1 ? bandRaw[b + 1] : bandRaw[b];
                    float cur = bandRaw[b];
                    bandRaw[b] = (prev + 2f * cur + next) * 0.25f;
                    prev = cur;
                }
            }

            for (int b = 0; b < n; b++)
            {
                float v2 = bandRaw[b];
                float k = v2 > bands[b] ? 1f - (float)Math.Exp(-dt / 0.020f)      // 起得快
                                        : 1f - (float)Math.Exp(-dt / Math.Max(0.02f, FallSec));   // 落得慢（可设置）
                bands[b] += (v2 - bands[b]) * k;
            }
            return bands;
        }

        /// <summary>原地基-2 FFT（长度必须是 2 的幂）。</summary>
        private static void Fft(float[] r, float[] im2)
        {
            int n = r.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = r[i]; r[i] = r[j]; r[j] = t;
                    t = im2[i]; im2[i] = im2[j]; im2[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float xr = r[b] * cr - im2[b] * ci;
                        float xi = r[b] * ci + im2[b] * cr;
                        r[b] = r[a] - xr; im2[b] = im2[a] - xi;
                        r[a] += xr; im2[a] += xi;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
