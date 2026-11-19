using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace TileDesk
{
    /// <summary>
    /// 右键菜单：独立的 WPF 窗口，系统亚克力 + 圆角 + 大行高 + Fluent 图标列。
    ///
    /// 为什么用 WPF：WinUI 需要几百 MB 的 WinAppSDK 运行时，单文件程序背不动；
    /// WPF 是 .NET Framework 自带的，同样能拿到亚克力模糊、圆角与硬件合成，
    /// 而且只重绘受影响的视觉元素 —— 鼠标划过只改一行底色，不像自绘方案要整窗重传。
    ///
    /// v3 的改动：
    ///   · 尺寸全部用 **DIP**。原来把 scale 又乘了一遍（DIP 本身已经含 DPI），
    ///     200% 缩放下菜单行高翻倍、字大得离谱。
    ///   · 入场动画：整块从 96% 透明度 + 下移 6px 落到原位（120ms，cubic ease-out）。
    ///   · 行高 34、圆角 6、图标列 36、勾选标记在右，和 Win11 原生菜单对齐。
    ///   · 支持 ↑↓ / Enter / Esc 键盘操作。
    /// </summary>
    internal static class WpfMenu
    {
        // ---- 亚克力：SetWindowCompositionAttribute（旧系统兜底）----
        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENTPOLICY
        {
            public int AccentState, AccentFlags, GradientColor, AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINCOMPATTRDATA
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;

        /// <summary>GradientColor 是 ABGR：半透明深色底，配合模糊就是亚克力。
        /// alpha 别太高 —— 太实就成了"一块黑"，系统材质的模糊完全透不出来。</summary>
        private const int AcrylicTint = unchecked((int)0xA41E1D22);

        // ---- 点击菜单外即关闭：低级鼠标钩子 ----
        // 桌面/磁贴墙都是不抢激活的窗口，点击它们不会触发 Deactivated，
        // 所以只靠失活关不了菜单。低级钩子不吞事件，只负责"点在外面就关"。
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public Native.POINT pt;
            public uint mouseData, flags, time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSE_LBDOWN = 0x0201;
        private const int WM_MOUSE_RBDOWN = 0x0204;

        private static IntPtr mouseHook;
        private static Native.HookProc mouseProc;   // 防 GC 回收委托
        private static Window hookWin;
        private static IntPtr hookHwnd;

        public sealed class Item
        {
            public string Text;
            public string Glyph;
            public bool Separator;
            public bool Enabled = true;
            public bool Checked;
            public bool Danger;
            public int Index = -1;
        }

        private static List<Item> lastItems;
        private static int lastResult = -1;
        private static int pressedIndex = -1;

        /// <summary>弹出菜单，返回被选项在原列表里的下标；取消返回 -1。</summary>
        public static int Show(IList<MenuEntry> entries, System.Drawing.Point screenPt, float scale)
        {
            lastItems = new List<Item>();
            lastResult = -1;
            pressedIndex = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                MenuEntry e = entries[i];
                if (e == null) continue;
                Item it = new Item();
                it.Index = i;
                it.Separator = e.separator;
                it.Text = e.text;
                it.Glyph = e.glyph;
                it.Enabled = e.enabled;
                it.Checked = e.checkable && e.checkedState;
                it.Danger = e.danger;
                lastItems.Add(it);
            }
            if (lastItems.Count == 0) return -1;

            System.Windows.Application app = System.Windows.Application.Current;
            if (app == null)
            {
                app = new System.Windows.Application();
                // 默认的 OnLastWindowClose 会在第一张菜单关闭时把整个 WPF Application 关掉，
                // 之后再也创建不了窗口（用户实测："点开一次之后就打不开了"）。
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            Window win = Build(lastItems);

            double dpi = DipScale();
            win.Left = screenPt.X / dpi;
            win.Top = screenPt.Y / dpi;

            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            win.Closed += delegate { frame.Continue = false; RemoveOutsideClickHook(); };

            win.Topmost = true;
            win.Show();
            InstallOutsideClickHook(win);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            return lastResult;
        }

        /// <summary>物理像素 / DIP 的比例。</summary>
        private static double DipScale()
        {
            try
            {
                double dipW = System.Windows.SystemParameters.PrimaryScreenWidth;
                double pxW = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                if (dipW > 10 && pxW > 10) return pxW / dipW;
            }
            catch { }
            return 1.0;
        }

        private static Window Build(List<Item> items)
        {
            Window win = new Window();
            win.WindowStyle = WindowStyle.None;
            // 非分层窗口 + 整个客户区当玻璃：这样 DWMWA_SYSTEMBACKDROP_TYPE 才生效。
            // 一旦 AllowsTransparency = true 就变成分层窗口，系统材质会失效。
            win.AllowsTransparency = false;
            win.Background = Brushes.Transparent;
            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(-1)
            });
            win.ShowInTaskbar = false;
            win.Topmost = true;
            win.SizeToContent = SizeToContent.WidthAndHeight;
            win.ResizeMode = ResizeMode.NoResize;
            win.SnapsToDevicePixels = true;
            win.UseLayoutRounding = true;
            win.ShowActivated = true;

            Border root = new Border();
            root.CornerRadius = new CornerRadius(10);
            root.Margin = new Thickness(0);
            root.Background = Brushes.Transparent;
            root.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            root.BorderThickness = new Thickness(0);
            // ★ 不要给根元素挂 DropShadowEffect：它会把整棵视觉树推入软件/中间位图
            //   渲染，配合系统亚克力时菜单会变成一片平灰（系统圆角+阴影由 DWM 提供）。

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(0, 6, 0, 6);
            root.Child = panel;

            List<Border> rows = new List<Border>();

            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Separator)
                {
                    Border sep = new Border();
                    sep.Height = 1;
                    sep.Margin = new Thickness(12, 5, 12, 5);
                    sep.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
                    panel.Children.Add(sep);
                    rows.Add(null);
                    continue;
                }

                Border row = new Border();
                row.CornerRadius = new CornerRadius(6);
                row.Height = 34;
                row.MinWidth = 214;
                row.Margin = new Thickness(6, 1, 6, 1);
                row.Background = Brushes.Transparent;
                row.SnapsToDevicePixels = true;

                Grid grid = new Grid();
                ColumnDefinition c0 = new ColumnDefinition();
                c0.Width = new GridLength(36);
                ColumnDefinition c1 = new ColumnDefinition();
                c1.Width = new GridLength(1, GridUnitType.Star);
                ColumnDefinition c2 = new ColumnDefinition();
                c2.Width = new GridLength(30);
                grid.ColumnDefinitions.Add(c0);
                grid.ColumnDefinitions.Add(c1);
                grid.ColumnDefinitions.Add(c2);

                TextBlock glyph = new TextBlock();
                glyph.Text = string.IsNullOrEmpty(it.Glyph) ? "" : it.Glyph;
                glyph.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                glyph.FontSize = 13;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(glyph, 0);
                grid.Children.Add(glyph);

                TextBlock label = new TextBlock();
                label.Text = it.Text ?? "";
                label.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
                label.FontSize = 12.6;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(label, 1);
                grid.Children.Add(label);

                TextBlock check = new TextBlock();
                check.Text = it.Checked ? "\uE73E" : "";
                check.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                check.FontSize = 12;
                check.VerticalAlignment = VerticalAlignment.Center;
                check.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(check, 2);
                grid.Children.Add(check);

                System.Windows.Media.Color fg = System.Windows.Media.Color.FromArgb(0xF2, 0xF2, 0xF3, 0xF6);
                if (!it.Enabled) fg = System.Windows.Media.Color.FromArgb(0x72, 0xA8, 0xAC, 0xB6);
                else if (it.Danger) fg = System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x7C, 0x7C);
                SolidColorBrush fgBrush = new SolidColorBrush(fg);
                glyph.Foreground = fgBrush;
                label.Foreground = fgBrush;
                check.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

                row.Child = grid;
                if (!it.Enabled) row.Opacity = 1.0;

                Item captured = it;
                int visualIndex = rows.Count;
                rows.Add(row);

                if (it.Enabled)
                {
                    row.MouseEnter += delegate
                    {
                        row.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                    };
                    row.MouseLeave += delegate
                    {
                        if (pressedIndex == captured.Index) pressedIndex = -1;
                        row.Background = Brushes.Transparent;
                    };
                    row.MouseLeftButtonDown += delegate
                    {
                        pressedIndex = captured.Index;
                    };
                    row.MouseLeftButtonUp += delegate
                    {
                        if (pressedIndex != captured.Index) return;
                        pressedIndex = -1;
                        lastResult = captured.Index;
                        Window w = Window.GetWindow(row);
                        if (w != null) w.Close();
                    };
                }
                row.Cursor = it.Enabled ? Cursors.Hand : Cursors.Arrow;

                panel.Children.Add(row);
            }

            win.Content = root;

            // ---- 入场动画：只动内容，不动窗口（窗口透明度会破坏系统材质）----
            TranslateTransform tt = new TranslateTransform(0, 6);
            root.RenderTransform = tt;
            root.Opacity = 0;

            // ---- 键盘导航 ----
            int sel = -1;
            Action<int> highlight = delegate(int idx)
            {
                for (int k = 0; k < rows.Count; k++)
                {
                    if (rows[k] == null) continue;
                    if (k == idx) rows[k].Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
                    else rows[k].Background = Brushes.Transparent;
                }
            };
            win.KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { lastResult = -1; win.Close(); return; }
                if (e.Key == Key.Enter)
                {
                    if (sel >= 0 && sel < rows.Count && rows[sel] != null && items[sel].Enabled)
                    {
                        lastResult = items[sel].Index;
                        win.Close();
                    }
                    return;
                }
                int dir = (e.Key == Key.Down) ? 1 : ((e.Key == Key.Up) ? -1 : 0);
                if (dir == 0) return;
                int start = sel < 0 ? (dir > 0 ? -1 : rows.Count) : sel;
                for (int step = 1; step <= rows.Count; step++)
                {
                    int idx = (start + dir * step + rows.Count * 2) % rows.Count;
                    if (idx >= 0 && idx < rows.Count && rows[idx] != null && items[idx].Enabled)
                    {
                        sel = idx;
                        highlight(idx);
                        break;
                    }
                }
            };

            // ---- 关闭条件 ----
            System.Windows.Threading.DispatcherTimer grace = new System.Windows.Threading.DispatcherTimer();
            grace.Interval = TimeSpan.FromMilliseconds(220);
            bool armed = false;
            grace.Tick += delegate
            {
                grace.Stop();
                armed = true;
                if (!win.IsActive) { lastResult = -1; win.Close(); }
            };
            win.Deactivated += delegate
            {
                if (!armed) { grace.Start(); return; }
                lastResult = -1;
                win.Close();
            };

            win.Loaded += delegate
            {
                IntPtr h = new WindowInteropHelper(win).Handle;
                ApplyAcrylic(h);

                // ★ 打开时必须**真的把它激活**（前台窗口）。
                //   磁贴墙是 WS_EX_NOACTIVATE，本进程不是前台进程，Show() 之后菜单
                //   一直是"未激活"态：Win11 的系统材质在未激活窗口上不绘制，菜单就是
                //   一片平灰，得等鼠标划过某一项把它激活才变透明（用户实测）。
                //   另外补一次 SWP_FRAMECHANGED + DwmFlush：材质刚设置完那一帧，
                //   DWM 还没重算边框，不等它合成完，用户看到的第一帧仍是旧的平灰。
                bool activated = false;
                try { win.Activate(); } catch { }
                try { activated = Native.ForceForeground(h); } catch { }
                try
                {
                    Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                        Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                    Native.DwmFlush();
                }
                catch { }
                if (!activated)
                {
                    // 前台锁没绕过时至少把视觉状态补上：让 DWM 立刻重算一次，
                    // 别等第一次鼠标悬停才把材质画出来。
                    try
                    {
                        Native.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero,
                            Native.RDW_INVALIDATE | Native.RDW_FRAME | Native.RDW_UPDATENOW |
                            Native.RDW_ALLCHILDREN);
                    }
                    catch { }
                }
                Config.Log("菜单激活: " + (activated ? "前台" : "非前台（已强制重绘兜底）"));

                // 入场动画
                DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130));
                fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                root.BeginAnimation(UIElement.OpacityProperty, fade);

                DoubleAnimation slide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(150));
                slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.YProperty, slide);

                // "点菜单外面就关闭"靠鼠标捕获：磁贴墙是 WS_EX_NOACTIVATE，
                // 点它不改变焦点，菜单收不到失活事件。
                try { Native.SetCapture(h); } catch { }
                try
                {
                    HwndSource src = HwndSource.FromHwnd(h);
                    if (src != null)
                    {
                        Window captured = win;
                        src.AddHook(delegate(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
                        {
                            if (msg == 0x0201 || msg == 0x0204 || msg == 0x0207)
                            {
                                double sx = 1, sy = 1;
                                try
                                {
                                    HwndSource s2 = HwndSource.FromHwnd(hwnd);
                                    if (s2 != null && s2.CompositionTarget != null)
                                    {
                                        sx = s2.CompositionTarget.TransformToDevice.M11;
                                        sy = s2.CompositionTarget.TransformToDevice.M22;
                                    }
                                }
                                catch { }
                                int cx = (short)((long)l & 0xFFFF);
                                int cy = (short)(((long)l >> 16) & 0xFFFF);
                                double ww = captured.ActualWidth * sx, wh = captured.ActualHeight * sy;
                                if (cx < 0 || cy < 0 || cx >= ww || cy >= wh)
                                {
                                    lastResult = -1;
                                    captured.Close();
                                    handled = true;
                                }
                            }
                            return IntPtr.Zero;
                        });
                    }
                }
                catch { }

                // 边界钳制：右下角弹出的菜单不翻边会有一大半跑到屏幕外
                try
                {
                    Rect wa = SystemParameters.WorkArea;
                    double w2 = win.ActualWidth, hh = win.ActualHeight;
                    double x = win.Left, y = win.Top;
                    if (x + w2 > wa.Right) x = Math.Max(wa.Left, wa.Right - w2);
                    if (y + hh > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - hh);
                    if (x < wa.Left) x = wa.Left;
                    if (y < wa.Top) y = wa.Top;
                    win.Left = x;
                    win.Top = y;
                }
                catch { }
            };

            win.Closed += delegate
            {
                try { Native.ReleaseCapture(); } catch { }
            };

            return win;
        }

        private static void InstallOutsideClickHook(Window win)
        {
            try
            {
                hookWin = win;
                hookHwnd = new WindowInteropHelper(win).EnsureHandle();
                mouseProc = OutsideClickHook;
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc,
                                                    Native.GetModuleHandle(null), 0);
            }
            catch (Exception ex) { Config.Log("装鼠标钩子失败: " + ex.Message); }
        }

        private static void RemoveOutsideClickHook()
        {
            if (mouseHook != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(mouseHook); } catch { }
                mouseHook = IntPtr.Zero;
            }
            hookWin = null;
            hookHwnd = IntPtr.Zero;
        }

        private static IntPtr OutsideClickHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_MOUSE_LBDOWN || msg == WM_MOUSE_RBDOWN)
                {
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(MSLLHOOKSTRUCT));
                    IntPtr hit = Native.WindowFromPoint(new Native.POINT(info.pt.X, info.pt.Y));
                    if (hit != hookHwnd)
                    {
                        // 点在菜单外面：关掉菜单。不吞事件，让这次点击正常生效。
                        Window w = hookWin;
                        if (w != null)
                            w.Dispatcher.BeginInvoke(new Action(delegate
                            {
                                try { lastResult = -1; RemoveOutsideClickHook(); w.Close(); }
                                catch { }
                            }));
                    }
                }
            }
            return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        /// <summary>亚克力 + 圆角。Win11 走 SYSTEMBACKDROP_TYPE，旧系统退回 AccentPolicy。</summary>
        private static void ApplyAcrylic(IntPtr hwnd)
        {
            try
            {
                Native.SetCornerPreference(hwnd, Native.DWMWCP_ROUND);
                Native.SetImmersiveDarkMode(hwnd, true);

                // ★ 系统材质（DWMWA_SYSTEMBACKDROP_TYPE = Acrylic）成功时**绝不能**
                //   再叠 AccentPolicy —— AccentPolicy 会把刚设置的系统材质顶掉。
                //
                // ★ 另一个必要条件：窗口必须保留 **WS_THICKFRAME** 结构框。
                //   WPF 的 WindowStyle=None + WindowChrome 会把结构框拆掉，
                //   这时 SetBackdrop 虽然**返回成功**，DWM 却根本不绘制材质 ——
                //   菜单变成一片平灰（实测 (84,84,84) 纯色、无噪点、无透底）。
                //   补上 WS_THICKFRAME 并让 DWM 重新计算边框后，材质才真的出来。
                int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
                if ((style & Native.WS_THICKFRAME) == 0)
                {
                    Native.SetWindowLong(hwnd, Native.GWL_STYLE, style | Native.WS_THICKFRAME);
                    Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                        Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                }

                bool ok = Native.SetBackdrop(hwnd, Native.DWMSBT_TRANSIENTWINDOW);
                if (!ok) ok = Native.SetBackdrop(hwnd, Native.DWMSBT_MAINWINDOW);

                if (!ok)
                {
                    // 旧系统兜底：AccentPolicy 的亚克力（带 tint）
                    ACCENTPOLICY policy = new ACCENTPOLICY();
                    policy.AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND;
                    policy.AccentFlags = 2;
                    policy.GradientColor = AcrylicTint;
                    IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENTPOLICY)));
                    try
                    {
                        Marshal.StructureToPtr(policy, ptr, false);
                        WINCOMPATTRDATA data = new WINCOMPATTRDATA();
                        data.Attribute = WCA_ACCENT_POLICY;
                        data.Data = ptr;
                        data.SizeOfData = Marshal.SizeOf(typeof(ACCENTPOLICY));
                        SetWindowCompositionAttribute(hwnd, ref data);
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }

                Config.Log("菜单背景: " + (ok ? "DWM 系统亚克力" : "AccentPolicy 兜底"));
            }
            catch { }
        }
    }
}
