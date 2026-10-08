using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TileDesk
{
    // =====================================================================
    //  桌面层挂载
    // =====================================================================

    internal static class DesktopHost
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private static string ClassOf(IntPtr h)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>父窗口必须铺满屏幕，否则说明抓到了别的程序留下的同名垃圾窗口。</summary>
        public static bool IsUsableHost(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            if (!IsWindowVisible(h)) return false;
            RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;
            Rectangle s = Screen.PrimaryScreen.Bounds;
            return (r.Right - r.Left) >= s.Width / 2 && (r.Bottom - r.Top) >= s.Height / 2;
        }

        /// <summary>
        /// 找一个能承载“桌面挂件”的窗口：
        ///   1) 让 Progman 生成 WorkerW，取与 SHELLDLL_DefView 同级、位于其后面的那个；
        ///   2) Progman 直属的 WorkerW 子窗口（Win11 24H2+ 的常见结构）；
        ///   3) 任意铺满屏幕的顶层 WorkerW；
        ///   4) 最后退回 Progman 本身。
        /// 所有候选都要通过 IsUsableHost 校验。
        /// </summary>
        private class HostScan
        {
            public IntPtr defViewHost = IntPtr.Zero;
            public IntPtr workerSibling = IntPtr.Zero;
            public IntPtr topWorker = IntPtr.Zero;
        }

        public static IntPtr FindDesktopHost()
        {
            IntPtr progman = Native.FindWindow("Progman", null);

            HostScan hs = new HostScan();
            ScanHosts(hs, progman);

            // 已经有可用的 WorkerW 就不用打扰 Progman 了（那条消息可能阻塞 1 秒）
            if (!IsUsableHost(hs.workerSibling) && !IsUsableHost(hs.topWorker) && progman != IntPtr.Zero)
            {
                IntPtr unused;
                Native.SendMessageTimeout(progman, Native.WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 250, out unused);
                Native.SendMessageTimeout(progman, Native.WM_SPAWN_WORKER, new IntPtr(0x0D), new IntPtr(0x01),
                    Native.SMTO_ABORTIFHUNG, 250, out unused);
                hs = new HostScan();
                ScanHosts(hs, progman);
            }

            Config.Log("桌面层候选: defViewHost=0x" + hs.defViewHost.ToInt64().ToString("X") +
                       " workerSibling=0x" + hs.workerSibling.ToInt64().ToString("X") +
                       " topWorker=0x" + hs.topWorker.ToInt64().ToString("X") +
                       " progman=0x" + progman.ToInt64().ToString("X"));

            if (IsUsableHost(hs.workerSibling)) return hs.workerSibling;
            if (IsUsableHost(hs.topWorker)) return hs.topWorker;
            return progman;
        }

        private static void ScanHosts(HostScan hs, IntPtr progman)
        {
            Native.EnumWindows(delegate(IntPtr top, IntPtr lp)
            {
                if (hs.defViewHost == IntPtr.Zero &&
                    Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    hs.defViewHost = top;
                    IntPtr w = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    if (w != IntPtr.Zero) hs.workerSibling = w;
                }
                if (hs.topWorker == IntPtr.Zero && ClassOf(top) == "WorkerW" && IsUsableHost(top))
                    hs.topWorker = top;
                return true;
            }, IntPtr.Zero);
        }
    }

    // =====================================================================
    //  系统集成：桌面图标开关 / 开机自启
    // =====================================================================

    internal static class SystemIntegration
    {
        private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "TileDesk";

        public static bool DesktopIconsHidden
        {
            get
            {
                // 先看"用户眼睛看到的状态"，拿不到再退回注册表 —— 见 DesktopListViewVisible()
                bool? live = DesktopListViewVisible();
                if (live.HasValue) return !live.Value;
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AdvancedKey))
                    {
                        if (k == null) return false;
                        object v = k.GetValue("HideIcons");
                        if (v == null) return false;
                        return Convert.ToInt32(v) != 0;
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>找到桌面的 SHELLDLL_DefView 窗口（Progman 下找不到就在所有顶层窗口里找）。</summary>
        public static IntPtr FindDefView()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                Native.EnumWindows(delegate(IntPtr top, IntPtr lp)
                {
                    if (defView == IntPtr.Zero)
                        defView = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                    return true;
                }, IntPtr.Zero);
            }
            return defView;
        }

        /// <summary>桌面列表视图当前可见吗？拿不到返回 null（调用方退回注册表）。</summary>
        /// <remarks>
        /// 为什么要以"实际可见性"为先：HideIcons 只是 Explorer 记下的一个设置，
        /// 而菜单里的勾要显示的是**用户眼睛看到的状态**。两者在异常路径下会不同步
        /// （设置写进去了、shell 却没重新应用），那时候只看注册表就会给用户一个
        /// 与眼前相反的勾。实测这个信号是可靠的：桌面图标显示时 SysListView32 可见、
        /// 隐藏时不可见。
        /// </remarks>
        private static bool? DesktopListViewVisible()
        {
            try
            {
                IntPtr defView = FindDefView();
                if (defView == IntPtr.Zero) return null;
                IntPtr lv = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
                if (lv == IntPtr.Zero) return null;
                return Native.IsWindowVisible(lv);
            }
            catch { return null; }
        }

        /// <summary>
        /// 切换桌面原生图标。优先通过 Shell 自身的命令（会同步注册表并即时生效），
        /// 失败时退回直接改注册表 + 广播。
        /// </summary>
        public static bool SetDesktopIcons(bool hide)
        {
            if (DesktopIconsHidden == hide) return true;
            try
            {
                IntPtr progman = Native.FindWindow("Progman", null);
                IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView == IntPtr.Zero)
                {
                    Native.EnumWindows(delegate(IntPtr top, IntPtr lp)
                    {
                        if (defView == IntPtr.Zero)
                            defView = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                        return true;
                    }, IntPtr.Zero);
                }
                if (defView != IntPtr.Zero)
                    Native.SendMessage(defView, Native.WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
            }
            catch (Exception ex) { Config.Log("发送桌面图标切换命令失败: " + ex.Message); }

            for (int i = 0; i < 10 && DesktopIconsHidden != hide; i++) Thread.Sleep(60);

            if (DesktopIconsHidden != hide)
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AdvancedKey))
                        if (k != null) k.SetValue("HideIcons", hide ? 1 : 0, RegistryValueKind.DWord);
                }
                catch (Exception ex) { Config.Log("写入 HideIcons 失败: " + ex.Message); return false; }
                RefreshShell();
                for (int i = 0; i < 10 && DesktopIconsHidden != hide; i++) Thread.Sleep(60);
            }

            Config.Log("设置桌面图标 " + (hide ? "隐藏" : "显示") + " => 当前 HideIcons=" + DesktopIconsHidden);
            return DesktopIconsHidden == hide;
        }

        /// <summary>
        /// </summary>

        /// <summary>

        /// <summary>

        private static void RefreshShell()
        {
            try
            {
                IntPtr unused;
                Native.SendMessageTimeout(new IntPtr(0xFFFF), Native.WM_SETTINGCHANGE, IntPtr.Zero,
                    IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 800, out unused);
            }
            catch { }
        }

        public static bool AutoStartEnabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (k == null) return false;
                        object v = k.GetValue(RunValue);
                        return v != null &&
                            v.ToString().IndexOf("TileDesk.exe", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch { return false; }
            }
        }

        public static void SetAutoStart(bool on) { SetAutoStart(on, ExePath); }

        public static void SetAutoStart(bool on, string exePath)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + exePath + "\"", RegistryValueKind.String);
                    else k.DeleteValue(RunValue, false);
                }
                Config.Log("开机自启 => " + on + " (" + exePath + ")");
            }
            catch (Exception ex) { Config.Log("设置开机自启失败: " + ex.Message); }
        }

        // ---------- 安装位置 ----------

        public static string InstallDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "TileDesk");
            }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "TileDesk.exe"); } }

        public static bool HasInstalledCopy
        {
            get { try { return File.Exists(InstalledExe); } catch { return false; } }
        }

        public static bool RunningFromInstallDir
        {
            get
            {
                try
                {
                    return string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        // ---------- 安装 / 卸载 ----------

        /// <summary>把当前 exe 复制到用户目录并设置开机自启，返回安装后的路径。</summary>
        public static string InstallSelf()
        {
            string target = InstalledExe;
            Directory.CreateDirectory(InstallDir);
            File.Copy(ExePath, target, true);
            SetAutoStart(true, target);
            Config.Log("已安装到 " + target);
            return target;
        }

        /// <summary>关自启 + 恢复原生图标 + 删除安装目录（可连同数据目录）。</summary>
        public static void UninstallSelf(bool removeData)
        {
            SetAutoStart(false);
            try { SetDesktopIcons(false); } catch { }

            string installDir = InstallDir;
            bool selfInInstall = false;
            try
            {
                selfInInstall = string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            try
            {
                if (selfInInstall)
                {
                    // 正在运行的 exe 删不掉，交给 cmd 等我们退出后再删
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        "/c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + installDir + "\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                }
                else if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, true);
                }
            }
            catch (Exception ex) { Config.Log("删除安装目录失败: " + ex.Message); }

            if (removeData)
            {
                try { Directory.Delete(Config.DataDir, true); } catch { }
            }
            Config.Log("已卸载（安装目录 " + installDir + "）");
        }

        public static string ExePath
        {
            get
            {
                try
                {
                    string p = Assembly.GetEntryAssembly() != null
                        ? Assembly.GetEntryAssembly().Location
                        : Application.ExecutablePath;
                    return string.IsNullOrEmpty(p) ? Application.ExecutablePath : p;
                }
                catch { return Application.ExecutablePath; }
            }
        }
    }

    // =====================================================================
    //  入口
    // =====================================================================

    internal static class Program
    {
        /// <summary>
        /// 控制自检。用「暂停 -> 确认状态变了 -> 恢复播放」这种可逆方式，
        /// 跑完不会留下任何副作用。
        /// </summary>
        private static void RunNowPlayingControlTest()
        {
            Config.Log("=== NowPlaying 控制自检 ===");
            using (NowPlayingMonitor mon = new NowPlayingMonitor())
            {
                mon.Start();
                System.Threading.Thread.Sleep(2500);   // 等它把会话抓到手

                NowPlayingState s = mon.State;
                Config.Log("起始: 会话=" + s.HasSession + " 状态=" + (s.Playing ? "播放中" : "暂停") +
                           " 曲名=[" + s.Title + "]");

                if (!s.HasSession) { Config.Log("没有媒体会话，控制自检结束"); return; }

                bool wasPlaying = s.Playing;
                bool ok = wasPlaying ? mon.Pause() : mon.Play();
                System.Threading.Thread.Sleep(1500);
                bool nowPlaying = mon.State.Playing;
                Config.Log("发出" + (wasPlaying ? "暂停" : "播放") + ": 返回=" + ok +
                           " 之后状态=" + (nowPlaying ? "播放中" : "暂停") +
                           " -> " + ((nowPlaying != wasPlaying) ? "状态确实变了 ✅" : "状态没变 ❌"));

                // 恢复原状
                if (wasPlaying) mon.Play(); else mon.Pause();
                System.Threading.Thread.Sleep(1200);
                Config.Log("已恢复: 状态=" + (mon.State.Playing ? "播放中" : "暂停"));

                Config.Log("区间控制可用性: 上一首=" + s.CanPrev + " 下一首=" + s.CanNext);
            }
            Config.Log("=== NowPlaying 控制自检结束 ===");
        }

        /// <summary>
        /// 不开界面的自检：跑一段时间的「正在播放」监控，把状态写进日志。
        /// 用来在没有任何 UI 的情况下验证数据层是否拿到东西。
        /// </summary>
        private static void RunNowPlayingTest(int seconds)
        {
            Config.Log("=== NowPlaying 自检开始（" + seconds + " 秒）===");
            using (NowPlayingMonitor mon = new NowPlayingMonitor())
            {
                mon.Start();
                int last = -1;
                DateTime end = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < end)
                {
                    NowPlayingState s = mon.State;
                    if (s.Version != last)
                    {
                        last = s.Version;
                        Config.Log("NowPlaying: 进程=" + s.AppRunning +
                                   " 会话=" + s.HasSession +
                                   " 状态=" + (s.Playing ? "播放中" : "暂停") +
                                   " 曲名=[" + s.Title + "]" +
                                   " 歌手=[" + s.Artist + "]" +
                                   " 专辑=[" + s.Album + "]" +
                                   " 进度=" + s.Position.TotalSeconds.ToString("0.0") + "/" +
                                              s.Duration.TotalSeconds.ToString("0.0") + "s" +
                                   " 封面=" + (s.Cover != null ? s.Cover.Width + "x" + s.Cover.Height : "无") +
                                   " 可控制(上一首/下一首/播放/暂停)=" + s.CanPrev + "/" + s.CanNext +
                                   "/" + s.CanPlay + "/" + s.CanPause +
                                   " 可见度=" + mon.Visibility.ToString("0.00"));
                    }
                    mon.DrainRetired();
                    System.Threading.Thread.Sleep(150);
                }
                Config.Log("=== NowPlaying 自检结束 ===");
            }
        }

        [STAThread]
        private static void Main(string[] args)        {
            // 安装 / 卸载不需要进消息循环，也不受单实例限制
            bool doInstall = false, doUninstall = false, purge = false;
            foreach (string a0 in args)
            {
                string a = a0.ToLowerInvariant();
                if (a == "--install") doInstall = true;
                else if (a == "--uninstall") doUninstall = true;
                else if (a == "--purge") purge = true;
            }
            if (doInstall)
            {
                try { SystemIntegration.InstallSelf(); }
                catch (Exception ex) { Config.Log("命令行安装失败: " + ex.Message); }
                return;
            }
            if (doUninstall)
            {
                try { SystemIntegration.UninstallSelf(purge); }
                catch (Exception ex) { Config.Log("命令行卸载失败: " + ex.Message); }
                return;
            }

            // --np-test [秒数]：不开界面，只跑「正在播放」监控并把状态写进日志，
            // 方便在没有 UI 的情况下验证数据层。
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].ToLowerInvariant() == "--np-ctl")
                {
                    RunNowPlayingControlTest();
                    return;
                }
                if (args[i].ToLowerInvariant() == "--np-test")
                {
                    int secs = 12;
                    if (i + 1 < args.Length) int.TryParse(args[i + 1], out secs);
                    if (secs <= 0) secs = 12;
                    RunNowPlayingTest(secs);
                    return;
                }
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "TileDesk_SingleInstance_Mutex_v1", out created))
            {
                if (!created)
                {
                    MessageBox.Show(
                        "TileDesk 已经在运行了。\n\n" +
                        "设置和退出都在通知区域的托盘图标上（蓝色双卡片图标，\n" +
                        "Win11 可能收在任务栏右下角的「^」溢出区里）：\n" +
                        "  · 右键 = 菜单（设置 / 窗口层级 / 隐藏原生图标 / 退出）\n" +
                        "  · 双击 = 快速显示 / 隐藏磁贴墙",
                        "TileDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                bool windowed = false;
                string modeOverride = null;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a == "--windowed" || a == "-w") windowed = true;
                    else if (a == "--mode" && i + 1 < args.Length) { modeOverride = args[++i]; }
                    else if (a.StartsWith("--mode=")) modeOverride = args[i].Substring(7);
                    else if (a == "--data" && i + 1 < args.Length) Config.SetDataOverride(args[++i]);
                    else if (a.StartsWith("--data=")) Config.SetDataOverride(args[i].Substring(7));
                }

                // 暗色与现代菜单的偏好要在最早期声明（uxtheme 的未公开导出，见 Native.EnableDarkMenus）。
                // 放在 EnableVisualStyles 之前，免得 comctl32 先按亮色初始化。
                Native.EnableDarkMenus();
                // 频谱采集（WASAPI 回环）：后台线程，失败就静默降级
                try { AudioSpectrum.Start(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                    ServicePointManager.DefaultConnectionLimit = 8;
                    ServicePointManager.Expect100Continue = false;
                }
                catch { }
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Config.Log("UI 异常: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Config.Log("未处理异常: " + e.ExceptionObject);
                };

                Config cfg = Config.Load();
                if (modeOverride != null) cfg.windowMode = modeOverride;

                Config.Log("=== TileDesk 启动 v" + AppVersion.Text + " === windowed=" + windowed +
                           " mode=" + cfg.windowMode);
                Config.Log("运行环境: " + Native.DescribeEnvironment());
                Config.Log("数据目录 = " + Config.DataDir +
                           "（封面缓存 " + Config.ArtCacheDir + "）");
                if (Config.DataProbeNote.Length > 0)
                    Config.Log("数据目录探测: " + Config.DataProbeNote);
                try
                {
                    // 启动自动隐藏桌面图标：**暂时关掉**。
                    // 它会向 shell 发一次切换命令，会扰动 explorer 的桌面窗口 ——
                    // 实测把 Progman 都搞没了，连带磁贴墙掉到所有窗口之下。
                    using (TileForm form = new TileForm(cfg, windowed))
                    {
                        Application.Run(new ApplicationContext(form));
                    }
                }
                catch (Exception ex)
                {
                    Config.Log("致命错误: " + ex);
                    MessageBox.Show(ex.ToString(), "TileDesk 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Config.Log("=== TileDesk 退出 ===");
            }
        }
    }
}
