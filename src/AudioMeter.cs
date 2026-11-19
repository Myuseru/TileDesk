using System;
using System.Runtime.InteropServices;

namespace TileDesk
{
    /// <summary>
    /// 系统音频电平表（WASAPI 的 IAudioMeterInformation）。
    ///
    /// 拿到的是**默认播放设备当前的实际音量峰值**（0..1），所有会话混音之后的电平，
    /// 所以播放器在放什么它就跟什么。用它驱动"正在播放"控件底部那条电平条。
    ///
    /// 注意：这里拿到的是**电平**，不是频谱。真正的频谱要把系统音频回环抓下来做 FFT
    /// （IAudioClient + IAudioCaptureClient + 自写 FFT），那是另一套东西；
    /// 现在这条是真实响应的电平显示，不是假动画。
    ///
    /// 全部通过 COM 互操作，不依赖任何外部库。
    /// </summary>
    internal static class AudioMeter
    {
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

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
            int GetMeteringChannelCount(out int count);
            int GetChannelsPeakValues(int channelCount, [Out] float[] peaks);
        }

        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioMeterInformation = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        private const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;

        private static IAudioMeterInformation meter;
        private static readonly object gate = new object();
        private static int failCount;

        /// <summary>当前系统音频峰值（0..1）。拿不到就返回 0。</summary>
        public static float Peak()
        {
            lock (gate)
            {
                if (failCount > 40) return 0f;      // 设备一直拿不到就别反复试了
                try
                {
                    if (meter == null)
                    {
                        object enObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                        IMMDeviceEnumerator en = (IMMDeviceEnumerator)enObj;
                        IMMDevice dev;
                        if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev) != 0 || dev == null)
                        {
                            failCount++;
                            return 0f;
                        }
                        Guid iid = IID_IAudioMeterInformation;
                        object o;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0 || o == null)
                        {
                            failCount++;
                            return 0f;
                        }
                        meter = (IAudioMeterInformation)o;
                    }
                    float p;
                    if (meter.GetPeakValue(out p) != 0) { meter = null; failCount++; return 0f; }
                    if (p < 0f) p = 0f;
                    if (p > 1f) p = 1f;
                    return p;
                }
                catch
                {
                    meter = null;
                    failCount++;
                    return 0f;
                }
            }
        }

        // ---- 条形显示的平滑状态（快速起、缓慢落，看着才像电平表）----
        private static float[] bars = new float[0];
        private static float level;
        private static int lastTick;

        /// <summary>
        /// 返回 n 条柱子的高度（0..1）。
        /// 形状是一条固定的钟形包络 × 当前电平 —— 反应的是真实音量，
        /// 只是没有做频率分离，所以不是逐频段的频谱。
        /// </summary>
        public static float[] Levels(int n)
        {
            if (n < 1) n = 1;
            if (bars.Length != n) { bars = new float[n]; }

            int now = unchecked(Environment.TickCount);
            float dt = lastTick == 0 ? 0.033f : Math.Min(0.2f, (now - lastTick) / 1000f);
            lastTick = now;

            float p = Peak();
            // 电平本身也平滑一下：起得快、落得慢
            float kv = p > level ? 1f - (float)Math.Exp(-dt / 0.05f) : 1f - (float)Math.Exp(-dt / 0.35f);
            level += (p - level) * kv;

            for (int i = 0; i < n; i++)
            {
                // 钟形包络：中间高、两边低，看起来像频谱
                float t = (n == 1) ? 0.5f : (float)i / (n - 1);
                float env = 0.34f + 0.66f * (float)Math.Sin(Math.PI * t);
                float target = level * env;
                float k = target > bars[i] ? 1f - (float)Math.Exp(-dt / 0.035f)
                                           : 1f - (float)Math.Exp(-dt / 0.22f);
                bars[i] += (target - bars[i]) * k;
                if (bars[i] < 0f) bars[i] = 0f;
                if (bars[i] > 1f) bars[i] = 1f;
            }
            return bars;
        }
    }
}
