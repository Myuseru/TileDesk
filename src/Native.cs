using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace TileDesk
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int cx, cy; public SIZE(int w, int h) { cx = w; cy = h; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [Flags]
    internal enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        BIGGERSIZEOK = 0x01,
        MEMORYONLY = 0x02,
        ICONONLY = 0x04,
        THUMBNAILONLY = 0x08,
        INCACHEONLY = 0x10
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int ULW_ALPHA = 0x02;
        public const int BI_RGB = 0;
        public const int DIB_RGB_COLORS = 0;

        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_HIDE = 0;
        public const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // 计时器精度：WM_TIMER 的粒度默认是系统 tick（约 15.6ms），
        // 也就是墙最多只能跑到 ~64fps 的理论值、实测常常只有十几 fps。
        // 频谱要跟音乐动，必须把粒度降到 1ms。
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint ms);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint ms);

        private static bool fastTimer;

        /// <summary>按需切换高精度计时器（成对调用，内部自己做状态跟踪）。</summary>
        public static void SetFastTimer(bool on)
        {
            if (on == fastTimer) return;
            try
            {
                if (on) TimeBeginPeriod(1); else TimeEndPeriod(1);
                fastTimer = on;
            }
            catch { fastTimer = on; }
        }
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOREDRAW = 0x0008;
        public const uint SWP_NOZORDER = 0x0004;

        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public const uint WM_SETTINGCHANGE = 0x001A;
        public const uint WM_COMMAND = 0x0111;
        public const uint WM_SPAWN_WORKER = 0x052C;
        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_ACTIVATE = 0x0006;
        public const int SC_MINIMIZE = 0xF020;
        public const int MA_ACTIVATE = 1;
        public const int MA_NOACTIVATE = 3;
        public const int WA_INACTIVE = 0;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        public const uint SPI_GETDESKWALLPAPER = 0x0073;
        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;

        public const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        public const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        public const int SW_SHOWNORMAL = 1;

        // ---------------- 打开 shell 特殊文件夹 / 在资源管理器里定位文件 ----------------
        //
        // 绝对不要 Process.Start("explorer.exe", ...) 去起资源管理器。
        // 那是用 CreateProcess 自己拉一个 explorer.exe，在不少环境下会初始化失败：
        // 「explorer.exe - 应用程序无法正常启动(0xc0000142)」（STATUS_DLL_INIT_FAILED）。
        // 交给 shell 做就不会有这个问题 —— shell 用它自己的方式开窗口。

        // ---------------- shell 特殊文件夹 ----------------

        public static readonly Guid FOLDERID_RecycleBinFolder =
            new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");

        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderIDList(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppidl);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
                                                    uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        public static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl,
                                                            IntPtr[] apidl, uint dwFlags);

        // ---------------- 回收站：纯 API 操作（不需要开任何窗口） ----------------
        //
        // 打开资源管理器窗口这条路在受限环境下会 E_ACCESSDENIED / 0xc0000142，
        // 但下面这两个 shell32 API 是当前进程直接干活，不依赖 shell 开窗口。

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        public const uint SHERB_NOCONFIRMATION = 0x00000001;
        public const uint SHERB_NOPROGRESSUI = 0x00000002;
        public const uint SHERB_NOSOUND = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        /// <summary>统计回收站里有几项、占多少字节。失败返回 false。</summary>
        public static bool QueryRecycleBin(out long items, out long bytes)
        {
            items = 0; bytes = 0;
            try
            {
                SHQUERYRBINFO q = new SHQUERYRBINFO();
                q.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                int hr = SHQueryRecycleBin(null, ref q);
                if (hr != 0) return false;
                items = q.i64NumItems;
                bytes = q.i64Size;
                return true;
            }
            catch { return false; }
        }

        /// <summary>清空回收站。返回 0 表示成功。</summary>
        public static int EmptyRecycleBin(IntPtr owner)
        {
            try
            {
                int hr = SHEmptyRecycleBin(owner, null,
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                return hr == 0 ? 0 : hr;
            }
            catch (Exception ex) { Config.Log("清空回收站异常: " + ex.Message); return -1; }
        }

        /// <summary>
        /// 打开回收站。**按进程完整性级别分流** —— 这是整件事的关键。
        ///
        /// 如果进程是从「带低完整性标签的目录」启动的（例如被沙箱 / 工作区管理的目录），
        /// 系统会把它降权到 Low（正常用户进程是 Medium = RID 0x2000）。此时：
        ///   * 自己 CreateProcess 拉 explorer.exe -> 0xc0000142 弹系统错误框
        ///   * SHOpenFolderAndSelectItems         -> 0x80070005 E_ACCESSDENIED
        ///   * Shell.Application / InvokeVerb     -> 返回成功但窗口不显示
        ///   * 连 %LOCALAPPDATA% 都写不进去
        /// Medium 下这些全都正常，所以低完整性时不要白费力气去起 explorer.exe
        /// （那只会给用户弹一个"应用程序无法正常启动"的框，完全无从下手）。
        /// </summary>
        public static int OpenRecycleBin(IntPtr owner)
        {
            int rid = IntegrityRid();
            if (rid >= 0x2000)
            {
                try
                {
                    System.Diagnostics.ProcessStartInfo psi =
                        new System.Diagnostics.ProcessStartInfo("explorer.exe");
                    psi.Arguments = "shell:RecycleBinFolder";
                    psi.UseShellExecute = true;   // 必须 true：让 shell 启动，别自己 CreateProcess
                    System.Diagnostics.Process.Start(psi);
                    Config.Log("打开回收站: explorer.exe shell:RecycleBinFolder (RID=0x" + rid.ToString("X4") + ")");
                    return 0;
                }
                catch (Exception ex) { Config.Log("explorer 方式打开回收站失败: " + ex.Message); }
            }
            else
            {
                Config.Log("进程被降权 RID=0x" + rid.ToString("X4") + "，跳过 explorer.exe（会弹 0xc0000142）");
            }

            int com = OpenRecycleBinCom();
            if (com == 0) { Config.Log("打开回收站: Shell.Application COM"); return 0; }

            Config.Log("打开回收站失败: RID=0x" + rid.ToString("X4") + " COM=" + com);
            return com != 0 ? com : -1;
        }
        private static int OpenRecycleBinCom()
        {
            object shell = null, ns = null, self = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return -1;
                shell = Activator.CreateInstance(t);
                // 10 = CSIDL_BITBUCKET（回收站）
                ns = t.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod,
                                    null, shell, new object[] { 10 });
                if (ns == null) return -2;
                self = ns.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty,
                                                 null, ns, null);
                if (self == null) return -3;
                self.GetType().InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod,
                                            null, self, new object[] { "open" });
                return 0;
            }
            catch (Exception ex) { Config.Log("回收站 COM 异常: " + ex.Message); return -4; }
            finally
            {
                if (self != null) { try { Marshal.ReleaseComObject(self); } catch { } }
                if (ns != null) { try { Marshal.ReleaseComObject(ns); } catch { } }
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }
            }
        }

        private static int OpenRecycleBinPidl(IntPtr owner)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                Guid g = FOLDERID_RecycleBinFolder;
                int hr = SHGetKnownFolderIDList(ref g, 0, IntPtr.Zero, out pidl);
                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    Config.Log("回收站 PIDL: SHGetKnownFolderIDList hr=0x" + hr.ToString("X8"));
                    return hr != 0 ? hr : unchecked((int)0x80004005);
                }
                // 用 SHOpenFolderAndSelectItems 而不是 ShellExecuteEx：
                // 前者是 shell 自己去开窗口（受限环境也能用），
                // 后者喂 PIDL 会被拒（访问被拒绝）。
                int r = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                if (r != 0) Config.Log("回收站 PIDL: SHOpenFolderAndSelectItems hr=0x" + r.ToString("X8"));
                return r == 0 ? 0 : r;
            }
            catch (Exception ex) { Config.Log("打开回收站异常: " + ex.Message); return -1; }
            finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
        }

        // ---------------- 运行环境体检 ----------------
        // 受限环境（例如从别的程序的沙箱/终端里启动）下，进程完整性级别会低于 Medium
        // 且通常被放进 Job 对象。此时 explorer.exe 起不来（0xc0000142），
        // 凡是需要 CreateProcess 外部程序的操作都会失败 —— 先记下来省得瞎猜。

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr info, int len, out int ret);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr p, IntPtr job, out bool result);

        /// <summary>当前进程的完整性级别 RID（正常用户进程 = 0x2000）。</summary>
        public static int IntegrityRid()
        {
            try
            {
                IntPtr tok;
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) return -1;
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    int ret;
                    if (!GetTokenInformation(tok, 25, buf, 64, out ret)) return -1;
                    IntPtr sid = Marshal.ReadIntPtr(buf, 0);
                    if (sid == IntPtr.Zero) return -1;
                    byte cnt = Marshal.ReadByte(sid, 1);
                    return cnt > 0 ? Marshal.ReadInt32(sid, 8 + 4 * (cnt - 1)) : -1;
                }
                finally { Marshal.FreeHGlobal(buf); Marshal.Release(tok); }
            }
            catch { return -1; }
        }
        public static string DescribeEnvironment()
        {
            string s = "";
            try
            {
                IntPtr tok;
                if (OpenProcessToken(GetCurrentProcess(), 0x0008, out tok))
                {
                    IntPtr buf = Marshal.AllocHGlobal(64);
                    try
                    {
                        int ret;
                        if (GetTokenInformation(tok, 25, buf, 64, out ret))   // TokenIntegrityLevel
                        {
                            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; }
                            //   offset 0 : PSID  Sid
                            //   offset 8 : DWORD Attributes   <-- 这里以前被当成 RID 读了，
                            //                                    读到的是 SE_GROUP_INTEGRITY|ENABLED = 0x60，
                            //                                    于是得出"进程被沙箱限制"的错误结论。
                            // 真正的 RID 在 SID 内部：Revision(1) Count(1) Authority(6) SubAuthority[]
                            IntPtr sid = Marshal.ReadIntPtr(buf, 0);
                            if (sid != IntPtr.Zero)
                            {
                                byte cnt = Marshal.ReadByte(sid, 1);
                                int rid = cnt > 0 ? Marshal.ReadInt32(sid, 8 + 4 * (cnt - 1)) : 0;
                                s += "完整性 RID=0x" + rid.ToString("X4");
                                if (rid < 0x2000) s += "(低于 Medium，进程被降权)";
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); Marshal.Release(tok); }
                }
            }
            catch { }
            try
            {
                bool inJob;
                if (IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob)) s += " InJob=" + inJob;
            }
            catch { }
            return s;
        }

        /// <summary>在资源管理器里定位并选中一个文件。</summary>
        public static int ShowInExplorer(string path)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attrs;
                int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs);
                if (hr != 0 || pidl == IntPtr.Zero) return hr != 0 ? hr : unchecked((int)0x80004005);
                int r = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                if (r != 0) Config.Log("回收站 PIDL: SHOpenFolderAndSelectItems hr=0x" + r.ToString("X8"));
                return r == 0 ? 0 : r;
            }
            catch (Exception ex) { Config.Log("定位文件异常: " + ex.Message); return -1; }
            finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

        public const uint GW_HWNDNEXT = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        public const uint TME_LEAVE = 0x00000002;

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_NULL = 0x0000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

        // ---- 鼠标按键状态（用来处理"菜单开着时点到别处要关掉"）----
        //
        // 我们的窗口是 WS_EX_NOACTIVATE + 逐像素透明的：点到桌面或别的窗口时，
        // 那些像素 alpha=0，消息直接穿透过去，我们收不到任何通知。
        // 所以只能主动轮询按键状态，不能指望收到"别处被点了"的消息。
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// 阻塞到 DWM 完成下一帧合成为止。截屏取"菜单背后的画面"时必须先调它 ——
        /// 隐藏窗口后 DWM 是异步合成的，不等一帧就截屏，抓到的还是上一帧（菜单自己）。
        /// </summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        // ================================================================
        //  发送 Win 组合键（用来唤起系统面板）
        //
        //  通知中心没有公开 API，只能模拟 Win 组合键：
        //    Windows 11 -> Win+N 打开通知中心（Win+A 是快速设置）
        //    Windows 10 -> Win+A 打开操作中心（里面才有通知）
        // ================================================================

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // x64 上 sizeof(INPUT) 是 40 字节（union 里最大的是 MOUSEINPUT）。
        // 只声明 KEYBDINPUT 的话 Marshal.SizeOf 只算 32 —— cbSize 不对，
        // SendInput 会直接返回 0（ERROR_INVALID_PARAMETER）。所以必须强制 Size。
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct INPUT
        {
            [FieldOffset(0)] public int type;
            [FieldOffset(8)] public KEYBDINPUT ki;   // x64 下 union 从偏移 8 开始
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public const ushort VK_LWIN = 0x5B;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int INPUT_KEYBOARD = 1;

        public static bool IsWindows11
        {
            get { return Environment.OSVersion.Version.Build >= 22000; }
        }

        /// <summary>唤起通知中心（Win11 用 Win+N，Win10 用 Win+A）。</summary>
        public static void OpenNotificationCenter()
        {
            ushort key = IsWindows11 ? (ushort)0x4E : (ushort)0x41;   // N : A

            // 分三次发，中间留一点间隔。一次性把 4 个事件塞进一个 SendInput，
            // 有些 shell 热键处理不过来（实测 Win+N 不生效）。
            uint r = 0;
            r += SendOne(VK_LWIN, false);
            System.Threading.Thread.Sleep(40);
            r += SendOne(key, false);
            r += SendOne(key, true);
            System.Threading.Thread.Sleep(40);
            r += SendOne(VK_LWIN, true);
            Config.Log("通知中心: Win+" + (char)key + " sizeof(INPUT)=" + Marshal.SizeOf(typeof(INPUT)) + " SendInput 返回 " + r + "/4" +
                       (r < 4 ? "（被系统拒绝了）" : ""));
        }

        private static uint SendOne(ushort vk, bool up)
        {
            INPUT[] one = new INPUT[1];
            one[0].type = INPUT_KEYBOARD;
            one[0].ki.wVk = vk;
            if (up) one[0].ki.dwFlags = KEYEVENTF_KEYUP;
            return SendInput(1, one, Marshal.SizeOf(typeof(INPUT)));
        }

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;

        public static bool KeyDown(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        public static bool AnyMouseDown()
        {
            return KeyDown(VK_LBUTTON) || KeyDown(VK_RBUTTON) || KeyDown(VK_MBUTTON);
        }

        // ================================================================
        //  系统级空闲时间
        //
        //  我们的窗口不激活，收不到键盘消息，所以不能只统计自己的鼠标事件。
        //  GetLastInputInfo 给的是**全系统**最后一次输入的时刻（键盘+鼠标都算），
        //  正是"桌面上没有任何操作"需要的口径。
        // ================================================================

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>距上一次系统输入（键盘或鼠标）过去了多少毫秒。</summary>
        public static int SystemIdleMs()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return 0;
            return unchecked(Environment.TickCount - (int)lii.dwTime);
        }

        // ================================================================
        //  关于"提高帧率"的一个实测结论
        //
        //  System.Windows.Forms.Timer 走 WM_TIMER，而 WM_TIMER 的最小粒度就是系统
        //  计时器周期（默认 15.6ms）—— 所以 tick.Interval = 8 实际得到的是 ~62 帧/秒。
        //  实测（本机 Win11）：`timeBeginPeriod(1)` **不能**改变这一点（开/不开都是
        //  ~62 ticks/s，高分辨率计时器只影响等待类 API 的精度）；而请求 16ms 反而会
        //  因为跨不过 15.6ms 那个边界而退化成 ~31ms（32fps）。
        //  结论：动画一律用 Interval = 8 换取"每个系统 tick 一帧"，不要在全局计时器
        //  分辨率上白付耗电的账；要再高只能换多媒体计时器 / 独立渲染线程，不值当。
        // ================================================================

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        /// <summary>
        /// UpdateLayeredWindow 的 indirect 版本。唯一的区别是多了 <c>prcDirty</c>：
        /// 告诉 DWM「只有这一块变了」，它就只更新那一块，不用把整张 3840x2064 的
        /// 位图重新合成一遍。悬停/进度条这类小改动全靠它省下上传开销。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct UPDATELAYEREDWINDOWINFO
        {
            public int cbSize;
            public IntPtr hdcDst;
            public IntPtr pptDst;
            public IntPtr psize;
            public IntPtr hdcSrc;
            public IntPtr pptSrc;
            public int crKey;
            public IntPtr pblend;
            public int dwFlags;
            public IntPtr prcDirty;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
            uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        /// <summary>窗口类名（用 Unicode 版本，免得中文/特殊字符出岔子）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int index);
        public const int LOGPIXELSX = 88;

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        /// <summary>
        /// 取窗口 DPI。不能只信 WinForms 的 DeviceDpi —— 在只声明了 system-DPI-aware
        /// 的 .NET Framework 进程里它经常返回 96，导致整个界面按 100% 缩放。
        /// </summary>
        public static int GetWindowDpi(IntPtr hwnd)
        {
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    uint d = GetDpiForWindow(hwnd);
                    if (d >= 72 && d <= 768) return (int)d;
                }
            }
            catch { }
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero)
                {
                    int d = GetDeviceCaps(dc, LOGPIXELSX);
                    ReleaseDC(IntPtr.Zero, dc);
                    if (d >= 72 && d <= 768) return d;
                }
            }
            catch { }
            return 0;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buffer, uint winIni);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        /// <summary>
        /// 用 shell 的默认动作打开文件/快捷方式 —— 等价于在资源管理器里双击。
        ///
        /// lpDirectory 必须留空！留空时 shell 会用「快捷方式自身记录的工作目录」；
        /// 自己塞一个进去会把它覆盖掉。很多启动器（育碧 / EA / Riot）要求以自己的
        /// 安装目录为工作目录，被改成桌面之后会静默退出 —— 表现就是「点了没反应，
        /// 程序也没起来」。这个坑就是 Launch() 里那句
        /// psi.WorkingDirectory = Path.GetDirectoryName(lnk) 造成的。
        ///
        /// 返回 0 表示成功，否则是 Win32 错误码（1223 = 用户在 UAC 上点了取消）。
        /// </summary>
        public static int ShellExecuteOpen(IntPtr owner, string path)
        {
            SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
            sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            sei.fMask = 0;
            sei.hwnd = owner;
            sei.lpVerb = null;        // 默认动作
            sei.lpFile = path;
            sei.lpParameters = null;
            sei.lpDirectory = null;   // 关键：不要覆盖快捷方式的工作目录
            sei.nShow = SW_SHOWNORMAL;
            if (ShellExecuteEx(ref sei)) return 0;
            return Marshal.GetLastWin32Error();
        }

        // ---------------- 放进回收站 ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        public const uint FO_DELETE = 0x0003;
        public const ushort FOF_SILENT = 0x0004;
        public const ushort FOF_NOCONFIRMATION = 0x0010;
        public const ushort FOF_ALLOWUNDO = 0x0040;
        public const ushort FOF_NOERRORUI = 0x0400;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

        /// <summary>删除到回收站（可还原），而不是永久删除。</summary>
        public static bool SendToRecycleBin(IntPtr owner, string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.hwnd = owner;
                op.wFunc = FO_DELETE;
                op.pFrom = path + "\0";     // 需要双 null 结尾，封送器再补一个
                op.pTo = null;
                op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT);
                return SHFileOperation(ref op) == 0;
            }
            catch (Exception ex) { Config.Log("删除到回收站失败 " + path + " : " + ex.Message); return false; }
        }

        public const int VK_CONTROL = 0x11;

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        /// <summary>当前抓着鼠标的那个窗口（没有则 IntPtr.Zero）。</summary>
        [DllImport("user32.dll")]
        public static extern IntPtr GetCapture();

        // ---------------- 让标题栏跟随暗色 ----------------

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Win11 的圆角窗口（无边框窗口也能圆）。</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            try { int pref = 2; DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); }
            catch { }
        }

        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 让进程内的系统菜单用暗色。
        ///
        /// 系统弹出菜单（TrackPopupMenu 创建的那个 #32768）跟随的是"应用的暗色偏好"，
        /// 而 WinForms 进程默认不声明，所以菜单一直是亮色。uxtheme 里有两个未公开导出
        /// （按序数取）：
        ///   #135 SetPreferredAppMode(2 = AllowDark) —— 进程级，让菜单跟随暗色；
        ///   #133 AllowDarkModeForWindow(hwnd, true) —— 窗口级。
        /// TranslucentTB 那类程序的暗色菜单也是这么拿到的。
        /// </summary>
        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#133", SetLastError = true)]
        private static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        public static void EnableDarkMenus()
        {
            try
            {
                // 1 = AllowDark（跟随系统，Win11 下菜单为暗色 + 圆角），
                // 2 是 ForceDark，观感更硬，不用。
                SetPreferredAppMode(1);
                AllowDarkModeForWindow(IntPtr.Zero, true);
            }
            catch { }
        }

        /// <summary>把窗口标题栏切成暗色（Win10 1809+ 用 19，Win11 用 20）。</summary>
        public static void UseDarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        }

        /// <summary>
        /// Ctrl 键现在是不是按着（Ctrl+滚轮 = 缩放卡片）。
        ///
        /// 必须用 GetAsyncKeyState，**不能**用 GetKeyState：
        /// GetKeyState 给的是"本线程处理最后一条消息时"的键盘状态，而磁贴墙这个窗口
        /// 永远不激活、压根收不到键盘消息 —— 它的返回值就是过时/无意义的，
        /// 于是普通滚轮有时会被当成 Ctrl+滚轮，卡片尺寸莫名其妙地被缩放。
        /// GetAsyncKeyState 问的是"此刻物理键盘的状态"，跟窗口有没有焦点无关。
        /// </summary>
        public static bool CtrlPressed
        {
            get { try { return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0; } catch { return false; } }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern IShellItemImageFactory SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid);

        public static string GetWallpaperPath()
        {
            try
            {
                StringBuilder sb = new StringBuilder(4096);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                    return sb.ToString();
            }
            catch { }
            return null;
        }

        public static void ShowProperties(IntPtr owner, string path)
        {
            SHELLEXECUTEINFO sei = new SHELLEXECUTEINFO();
            sei.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            sei.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI;
            sei.hwnd = owner;
            sei.lpVerb = "properties";
            sei.lpFile = path;
            sei.nShow = SW_SHOWNORMAL;
            ShellExecuteEx(ref sei);
        }
    }
}
