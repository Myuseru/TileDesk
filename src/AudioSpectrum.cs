using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileDesk
{
    /// <summary>
    /// 系统音频频谱（WASAPI 回环采集 + 自写 FFT）。
    ///
    /// 相对 2.x 的三处重写（都是实测的 CPU 大头）：
    ///
    ///  1. **整包拷贝，不再逐样本读**。原来每个采样点都要
    ///     `Marshal.Copy(ptr, tmp4, 0, 4)` + `BitConverter.ToSingle` —— 48kHz 立体声
    ///     就是每秒近 10 万次封送，采集线程常年 5~10% CPU。现在一次
    ///     `Marshal.Copy` 把整个包搬进 byte[]，再用 `Buffer.BlockCopy` 一次性
    ///     转成 float[]（这是一次纯 memcpy），最后只做一次声道混合。
    ///
    ///  2. **事件驱动**。原来 `GetNextPacketSize == 0` 就 `Thread.Sleep(4)` 空转，
    ///     没声音时也在每秒醒 250 次。现在 `SetEventHandle` + `WaitOne`，
    ///     没数据就彻底睡死。
    ///
    ///  3. **FFT 按需算并缓存**。原来 `Bands()` 每被调一次就做一遍 1024 点 FFT，
    ///     而它同时被墙的 tick 和播放器控件的渲染各调一次 —— 每秒 120 次 FFT。
    ///     现在最多 60 次/秒，静音时一次都不做。
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        // ---------------- 状态 ----------------
        private const int FFT_SIZE = 2048;          // 48kHz 下 23Hz/bin，低频分得开
        private const int RING = FFT_SIZE * 2;

        private static readonly float[] ring = new float[RING];
        private static int writePos;
        private static int channels = 2, sampleRate = 48000;
        private static volatile bool running;
        private static Thread thread;
        private static volatile bool ready;
        private static int failCount;

        private static float[] bands = new float[0];
        private static float[] bandRaw = new float[0];
        private static int lastBandTick;
        private static float level;                 // 0..1 RMS（给 Active 用，不依赖 Bands）
        private static int lastLoudTick;

        // FFT 缓存
        private static readonly float[] fftRe = new float[FFT_SIZE];
        private static readonly float[] fftIm = new float[FFT_SIZE];
        private static readonly float[] window = new float[FFT_SIZE];
        private static readonly float[] mag = new float[FFT_SIZE / 2];
        private static readonly float[] magSmooth = new float[FFT_SIZE / 2];
        private static int lastFftTick;
        private static bool windowReady;

        // 频段 → bin 的映射表（柱子数或采样率变了才重算）
        private static int[] bandLo = new int[0], bandHi = new int[0];
        private static int bandMapFor = -1, bandMapRate = -1;

        /// <summary>柱子下落时间常数（秒），由主程序按设置写入。</summary>
        public static float FallSec = 0.12f;

        public static bool Ready { get { return ready; } }

        /// <summary>当前 RMS 电平（0..1）。</summary>
        public static float Level { get { return level; } }

        /// <summary>
        /// 当前是否"有声音"。保持 2 秒 —— 歌曲段落之间会有一瞬间安静，
        /// 不加保持的话墙会在那一瞬间开始淡出。
        /// </summary>
        public static bool Active
        {
            get
            {
                int now = Clock.NowMs;
                if (level > 0.004f) lastLoudTick = now;
                return lastLoudTick != 0 && unchecked(now - lastLoudTick) < 2000;
            }
        }

        public static void Start()
        {
            if (running || failCount > 6) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "TileDesk-Spectrum";
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Start();
        }

        /// <summary>
        /// 采集线程：会话断了就自己重连。
        /// 切歌、换输出设备、播放器重启都可能让 IAudioClient 失效。
        /// </summary>
        private static void Loop()
        {
            int attempt = 0;
            while (running)
            {
                CaptureSession();
                if (!running) break;
                ready = false;
                level = 0f;
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
            IntPtr evt = IntPtr.Zero;
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
                    if (bits != 32 || (tag != 3 && tag != unchecked((short)0xFFFE)))
                    {
                        Config.Log("频谱: 混音格式不是 32 位浮点（tag=" + tag + " bits=" + bits + "），放弃");
                        Fail();
                        return;
                    }

                    evt = CreateEvent(IntPtr.Zero, false, false, null);
                    int flags = AUDCLNT_STREAMFLAGS_LOOPBACK;
                    if (evt != IntPtr.Zero) flags |= AUDCLNT_STREAMFLAGS_EVENTCALLBACK;

                    int hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags,
                                               1000000L, 0L, fmt, IntPtr.Zero);
                    if (hr != 0)
                    {
                        // 有些设备不接受事件回调，退回轮询
                        if (evt != IntPtr.Zero)
                        {
                            CloseHandle(evt); evt = IntPtr.Zero;
                            hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                                                   1000000L, 0L, fmt, IntPtr.Zero);
                        }
                        if (hr != 0) { Config.Log("频谱: Initialize 失败 hr=" + hr); Fail(); return; }
                    }
                }
                finally { CoTaskMemFree(fmt); }

                if (evt != IntPtr.Zero)
                {
                    int hre = client.SetEventHandle(evt);
                    if (hre != 0) { CloseHandle(evt); evt = IntPtr.Zero; }
                }

                Guid capIid = IID_IAudioCaptureClient;
                object capObj;
                int hr4 = client.GetService(ref capIid, out capObj);
                if (hr4 != 0 || capObj == null) { Config.Log("频谱: GetService 失败 hr=" + hr4); Fail(); return; }
                capture = (IAudioCaptureClient)capObj;
                int hr5 = client.Start();
                if (hr5 != 0) { Config.Log("频谱: Start 失败 hr=" + hr5); Fail(); return; }

                ready = true;
                Config.Log("频谱: 回环采集已启动 " + sampleRate + "Hz " + channels + "ch" +
                           (evt != IntPtr.Zero ? "（事件驱动）" : "（轮询）"));

                byte[] raw = new byte[0];
                float[] conv = new float[0];

                while (running)
                {
                    int packet;
                    if (capture.GetNextPacketSize(out packet) != 0) break;
                    if (packet == 0)
                    {
                        if (evt != IntPtr.Zero)
                        {
                            // 事件句柄是自动重置的：WaitOne 拿到就是有数据
                            WaitOne(evt, 200);
                        }
                        else Thread.Sleep(5);
                        continue;
                    }

                    while (packet > 0 && running)
                    {
                        IntPtr data; int frames, pflags; long dp, qp;
                        if (capture.GetBuffer(out data, out frames, out pflags, out dp, out qp) != 0) break;
                        try
                        {
                            if (frames > 0 && data != IntPtr.Zero)
                            {
                                bool silent = (pflags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                                if (silent)
                                {
                                    // 静音包：整段写 0，不碰非托管内存
                                    for (int f = 0; f < frames; f++)
                                    {
                                        ring[writePos] = 0f;
                                        writePos = (writePos + 1) & (RING - 1);
                                    }
                                    level *= 0.85f;
                                }
                                else
                                {
                                    int total = frames * channels;
                                    int bytes = total * 4;
                                    if (raw.Length < bytes) raw = new byte[bytes];
                                    if (conv.Length < total) conv = new float[total];

                                    // ★ 一次整包拷贝 + 一次按位转换，替代逐样本 Marshal.Copy
                                    Marshal.Copy(data, raw, 0, bytes);
                                    Buffer.BlockCopy(raw, 0, conv, 0, bytes);

                                    double sum = 0;
                                    if (channels == 2)
                                    {
                                        for (int f = 0; f < frames; f++)
                                        {
                                            int i = f * 2;
                                            float v = (conv[i] + conv[i + 1]) * 0.5f;
                                            ring[writePos] = v;
                                            writePos = (writePos + 1) & (RING - 1);
                                            sum += (double)v * v;
                                        }
                                    }
                                    else if (channels == 1)
                                    {
                                        for (int f = 0; f < frames; f++)
                                        {
                                            float v = conv[f];
                                            ring[writePos] = v;
                                            writePos = (writePos + 1) & (RING - 1);
                                            sum += (double)v * v;
                                        }
                                    }
                                    else
                                    {
                                        float inv = 1f / channels;
                                        for (int f = 0; f < frames; f++)
                                        {
                                            int b = f * channels;
                                            float s = 0f;
                                            for (int c = 0; c < channels; c++) s += conv[b + c];
                                            float v = s * inv;
                                            ring[writePos] = v;
                                            writePos = (writePos + 1) & (RING - 1);
                                            sum += (double)v * v;
                                        }
                                    }
                                    double rms = Math.Sqrt(sum / Math.Max(1, frames));
                                    level += (float)((rms - level) * 0.35);
                                    if (level > 1f) level = 1f;
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
                if (evt != IntPtr.Zero) CloseHandle(evt);
                ready = false;
            }
        }

        /// <summary>事件句柄的等待包装（避免每次 new WaitHandle）。</summary>
        private static bool WaitOne(IntPtr handle, int ms)
        {
            return WaitForSingleObject(handle, (uint)ms) == 0;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        private static void Fail()
        {
            failCount++;
            ready = false;
        }

        // ---------------- FFT & 频段 ----------------

        /// <summary>
        /// 返回 n 根柱子的高度（0..1），按对数频率分布（低 → 高）。
        /// 拿不到音频时返回全 0。
        /// </summary>
        public static float[] Bands(int n)
        {
            if (n < 4) n = 4;
            if (bands.Length != n)
            {
                bands = new float[n];
                bandRaw = new float[n];
                bandMapFor = -1;
            }

            int now = Clock.NowMs;
            float dt = lastBandTick == 0 ? 0.033f : Math.Min(0.2f, unchecked(now - lastBandTick) / 1000f);
            lastBandTick = now;

            if (!ready)
            {
                for (int i = 0; i < n; i++) bands[i] *= 0.85f;
                return bands;
            }

            // FFT 最多 60 次/秒；静音时不重算
            bool needFft = unchecked(now - lastFftTick) >= 16 && level > 0.0008f;
            if (needFft) { ComputeFft(); lastFftTick = now; }

            EnsureBandMap(n);

            for (int b = 0; b < n; b++)
            {
                int i0 = bandLo[b], i1 = bandHi[b];
                float mx = 0f;
                for (int i = i0; i < i1; i++) mx += mag[i];
                mx /= Math.Max(1, i1 - i0);

                float db = 20f * (float)Math.Log10(mx / (FFT_SIZE / 4f) + 1e-7f);
                float v = (db + 64f) / 64f;
                if (v < 0f) v = 0f;
                if (v > 1f) v = 1f;
                v *= 0.58f + 0.42f * (float)Math.Sin(Math.PI * ((double)b / n));
                bandRaw[b] = v;
            }

            // 横向平滑两遍（两端按边值处理）
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
                float k = v2 > bands[b] ? 1f - (float)Math.Exp(-dt / 0.018f)
                                        : 1f - (float)Math.Exp(-dt / Math.Max(0.02f, FallSec));
                bands[b] += (v2 - bands[b]) * k;
            }
            return bands;
        }

        private static void EnsureBandMap(int n)
        {
            if (bandMapFor == n && bandMapRate == sampleRate) return;
            bandMapFor = n;
            bandMapRate = sampleRate;

            if (bandLo.Length != n) { bandLo = new int[n]; bandHi = new int[n]; }
            double fMin = 40.0, fMax = Math.Min(16000.0, sampleRate / 2.0 - 1);
            double binHz = (double)sampleRate / FFT_SIZE;
            int half = FFT_SIZE / 2;

            for (int b = 0; b < n; b++)
            {
                double f0 = fMin * Math.Pow(fMax / fMin, (double)b / n);
                double f1 = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / n);
                int i0 = (int)Math.Floor(f0 / binHz);
                int i1 = (int)Math.Ceiling(f1 / binHz);
                if (i1 <= i0) i1 = i0 + 1;
                if (i0 < 1) i0 = 1;
                if (i1 > half) i1 = half;
                bandLo[b] = i0;
                bandHi[b] = i1;
            }
        }

        private static void ComputeFft()
        {
            if (!windowReady)
            {
                for (int i = 0; i < FFT_SIZE; i++)
                    window[i] = 0.5f * (1f - (float)Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1)));
                windowReady = true;
            }

            int start = (writePos - FFT_SIZE) & (RING - 1);
            for (int i = 0; i < FFT_SIZE; i++)
            {
                fftRe[i] = ring[(start + i) & (RING - 1)] * window[i];
                fftIm[i] = 0f;
            }
            Fft(fftRe, fftIm);

            int half = FFT_SIZE / 2;
            float dt = 0.016f;
            for (int i = 0; i < half; i++)
            {
                float m0 = (float)Math.Sqrt(fftRe[i] * fftRe[i] + fftIm[i] * fftIm[i]);
                float kk = m0 > magSmooth[i] ? 0.55f : 0.22f;   // 起得快、落得慢
                magSmooth[i] += (m0 - magSmooth[i]) * kk;
                mag[i] = magSmooth[i];
            }
            if (dt <= 0f) dt = 0.016f;
        }

        private static int[] bitrev;

        /// <summary>原地基-2 FFT（长度必须是 2 的幂）。位反转表预计算，只在第一次建。</summary>
        private static void Fft(float[] r, float[] im)
        {
            int n = r.Length;
            if (bitrev == null || bitrev.Length != n)
            {
                bitrev = new int[n];
                int bits = 0;
                while ((1 << bits) < n) bits++;
                for (int i = 0; i < n; i++)
                {
                    int x = i, y = 0;
                    for (int b = 0; b < bits; b++) { y = (y << 1) | (x & 1); x >>= 1; }
                    bitrev[i] = y;
                }
            }

            for (int i = 0; i < n; i++)
            {
                int j = bitrev[i];
                if (i < j)
                {
                    float t = r[i]; r[i] = r[j]; r[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = i + k + half;
                        float xr = r[b] * cr - im[b] * ci;
                        float xi = r[b] * ci + im[b] * cr;
                        r[b] = r[a] - xr; im[b] = im[a] - xi;
                        r[a] += xr; im[a] += xi;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }
}
