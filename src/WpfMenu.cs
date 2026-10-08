using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;


namespace TileDesk
{
    /// <summary>
    /// WPF 版的右键菜单：圆角 + 亚克力模糊 + 大行高 + 图标列。
    ///
    /// 为什么用 WPF：本机 TranslucentTB 那个菜单是 WinUI（XAML MenuFlyout）渲染的，
    /// 而 WinUI 需要 WinAppSDK 运行时（几百 MB），单文件程序背不动。
    /// WPF 是 .NET Framework 自带的（PresentationFramework/WindowsBase），
    /// 同样能拿到亚克力模糊、圆角与自绘项，观感可以对上，且不引入外部依赖。
    ///
    /// 性能：WPF 走硬件合成，只重绘受影响的视觉元素（悬停只改一项的底色），
    /// 不像旧的自绘方案每次悬停都要重画整张图再整窗上传。
    /// </summary>
    internal static class WpfMenu
    {
        // ---- 亚克力：SetWindowCompositionAttribute（ACCENT_ENABLE_BLURBEHIND / ACRYLIC）----
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;

        /// <summary>GradientColor 是 ABGR：半透明的深色底，配合模糊就是亚克力。</summary>
        private const int AcrylicTint = unchecked((int)0xB4211F23);

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
        /// <summary>当前在哪一项上按下了左键（-1 = 没有）。只在同一项上按下再松开才算选中。</summary>
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
                if (string.IsNullOrEmpty(it.Glyph))
                {
                    if (it.Separator) it.Glyph = "";
                    else if (it.Checked) it.Glyph = "\uE73E";
                }
                lastItems.Add(it);
            }
            if (lastItems.Count == 0) return -1;

            System.Windows.Application app = System.Windows.Application.Current;
            if (app == null)
            {
                // 首次调用：WPF 需要一个 Application 实例才能跑窗口（WinForms 主循环仍在跑）。
                app = new System.Windows.Application();
                // ★ 必须显式关闭：默认的 OnLastWindowClose 会在第一张菜单关闭时把整个
                //   WPF Application 关掉，之后再也创建不了窗口 ——
                //   用户实测"点开一次之后就打不开了"。
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            Window win = Build(lastItems, scale);
            win.Left = screenPt.X / scale;      // WPF 用逻辑单位
            win.Top = screenPt.Y / scale;

            // 用 Show + PushFrame，**不用** ShowDialog。
            // ShowDialog 是模态对话框：它会阻止别的窗口拿到焦点，"点菜单外面"的消息
            // 根本传不进来，菜单永远不关。
            //
            // 点菜单外面的处理靠菜单窗口自己的鼠标捕获 + 消息钩子（见 Build 里的
            // SetCapture 那段）。这里**不要**再加全屏遮挡层：试过，那个满屏 Topmost
            // 窗口会把菜单上的点击一起吞掉（用户实测：点「退出 TileDesk」没反应）。
            System.Windows.Threading.DispatcherFrame frame = new System.Windows.Threading.DispatcherFrame();
            win.Closed += delegate(object o, EventArgs e) { frame.Continue = false; };

            win.Topmost = true;
            win.Show();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            return lastResult;
        }

        private static Window Build(List<Item> items, float scale)
        {
            Window win = new Window();
            win.WindowStyle = WindowStyle.None;
            // 这一版是用户认可的：非分层窗口 + 整个客户区当玻璃（GlassFrameThickness = -1），
            // 面板自己一点底色都不画，深色由系统的背景层提供
            // （DWMWA_SYSTEMBACKDROP_TYPE = 3 Acrylic 打在窗口上）。
            // 注意不要退回 AllowsTransparency=true —— 分层窗口下这类系统背景接口不生效。
            win.AllowsTransparency = false;
            win.Background = System.Windows.Media.Brushes.Transparent;
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
            // 必须可激活：靠"失活"事件来实现"点菜单外面就关掉"。
            // 设成 ShowActivated=false 的话窗口永远不激活，Deactivated 不会触发，
            // 菜单会一直挂在屏幕上（实测：连开两次就有两个窗口叠着）。
            win.ShowActivated = true;

            double s = scale <= 0 ? 1.0 : scale;

            Border root = new Border();
            root.CornerRadius = new CornerRadius(5 * s);
            root.Margin = new Thickness(0);
            root.Background = System.Windows.Media.Brushes.Transparent;
            root.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            root.BorderThickness = new Thickness(0);
            root.Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = System.Windows.Media.Colors.Black
            };

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(0, 6 * s, 0, 6 * s);
            root.Child = panel;

            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Separator)
                {
                    Border sep = new Border();
                    sep.Height = 1;
                    sep.Margin = new Thickness(7 * s, 3 * s, 7 * s, 3 * s);
                    sep.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                    panel.Children.Add(sep);
                    continue;
                }

                Grid row = new Grid();
                row.Height = 22 * s;
                row.MinWidth = 108 * s;
                ColumnDefinition c0 = new ColumnDefinition();
                c0.Width = new GridLength(22 * s);
                ColumnDefinition c1 = new ColumnDefinition();
                c1.Width = new GridLength(1, GridUnitType.Star);
                row.ColumnDefinitions.Add(c0);
                row.ColumnDefinitions.Add(c1);

                TextBlock glyph = new TextBlock();
                glyph.Text = it.Glyph ?? "";
                glyph.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons");
                glyph.FontSize = 9.5f * s;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(glyph, 0);
                row.Children.Add(glyph);

                TextBlock label = new TextBlock();
                label.Text = it.Text ?? "";
                label.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
                label.FontSize = 7.5f * s;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 0, 9 * s, 0);
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                // 配色：危险项红字、禁用项灰字、其余白字
                System.Windows.Media.Color fg = System.Windows.Media.Color.FromArgb(0xF0, 0xF2, 0xF3, 0xF7);
                if (!it.Enabled) fg = System.Windows.Media.Color.FromArgb(0x60, 0xA0, 0xA3, 0xAD);
                if (it.Danger && it.Enabled) fg = System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);
                SolidColorBrush fgBrush = new SolidColorBrush(fg);
                glyph.Foreground = fgBrush;
                label.Foreground = fgBrush;

                Item captured = it;
                if (it.Enabled)
                {
                    row.Background = System.Windows.Media.Brushes.Transparent;
                    row.MouseEnter += delegate(object o, MouseEventArgs e)
                    {
                        row.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
                    };
                    row.MouseLeave += delegate(object o, MouseEventArgs e)
                    {
                        row.Background = System.Windows.Media.Brushes.Transparent;
                    };
                }
                row.Cursor = it.Enabled ? Cursors.Hand : Cursors.Arrow;
                panel.Children.Add(row);

                if (it.Enabled)
                {
                    // ★ 只认"在这一项上按下、再松开"。不能只听抬起 ——
                    //   菜单是右键弹出来的，此时左键可能本来就按着（或者松手时正好停在
                    //   最后一项上），那样抬起会被当成点击，命中"退出 TileDesk"就把程序关了
                    //   （用户实测："左键一松开就直接关闭程序"）。
                    row.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e)
                    {
                        pressedIndex = captured.Index;
                    };
                    row.MouseLeftButtonUp += delegate(object o, MouseButtonEventArgs e)
                    {
                        if (pressedIndex != captured.Index) return;      // 没在这项上按过，忽略
                        pressedIndex = -1;
                        lastResult = captured.Index;
                        Window owner = Window.GetWindow(row);
                        if (owner != null) owner.Close();
                    };
                    row.MouseLeave += delegate(object o, MouseEventArgs e)
                    {
                        if (pressedIndex == captured.Index) pressedIndex = -1;
                    };
                }
            }

            win.Content = root;
            // 关闭条件：失活即关（点菜单外面），但给一点宽限 ——
            // 菜单刚显示时可能还没拿到焦点，立刻判失活会让它"一闪就没了"。
            System.Windows.Threading.DispatcherTimer grace = new System.Windows.Threading.DispatcherTimer();
            grace.Interval = TimeSpan.FromMilliseconds(220);
            bool armed = false;
            grace.Tick += delegate(object o, EventArgs e)
            {
                grace.Stop();
                armed = true;
                if (!win.IsActive) { lastResult = -1; win.Close(); }
            };
            win.Deactivated += delegate(object o, EventArgs e)
            {
                if (!armed) { grace.Start(); return; }   // 刚显示，先给宽限
                lastResult = -1;
                win.Close();
            };
            win.KeyDown += delegate(object o, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { lastResult = -1; win.Close(); }
            };
            win.Loaded += delegate(object o, RoutedEventArgs e)
            {
                IntPtr h = new WindowInteropHelper(win).Handle;
                ApplyAcrylic(h);

                // ★ "点菜单外面就关闭"靠**鼠标捕获**实现，不依赖 Deactivated：
                //   磁贴墙是 WS_EX_NOACTIVATE（永不激活），点它不改变焦点，菜单收不到
                //   失活事件；全屏透明遮挡层的命中测试也不可靠（都试过，实测不行）。
                //   SetCapture 后所有鼠标消息先送到菜单，落点在菜单矩形之外就直接关闭 ——
                //   原生菜单内部也是这个做法。一次点击会被吞掉，和原生行为一致。
                try { Native.SetCapture(h); } catch { }
                try
                {
                    HwndSource src = HwndSource.FromHwnd(h);
                    if (src != null)
                    {
                        Window captured = win;
                        // ★ 消息里的坐标是**物理像素**，而 WPF 的 ActualWidth/Height 是 DIP。
                        //   200% 缩放下如果直接比较，菜单右半边的点击全会被当成"菜单外面"，
                        //   菜单直接关掉、那一项根本不执行（用户实测：点「退出 TileDesk」没反应）。
                        //   所以这里把窗口尺寸换算成物理像素再比。
                        src.AddHook(delegate(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
                        {
                            if (msg == 0x0201 || msg == 0x0204 || msg == 0x0207)   // 左/右/中键按下
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

                // 边界钳制：菜单要完整落在工作区内。
                // 齿轮和托盘菜单是从屏幕右下角弹出的，不翻边就会有一大半跑到屏幕外
                // （用户实测："菜单都飞到屏幕外了，根本看不见"）。原生菜单自带这个行为，
                // WPF 得自己算：右边/下边放不下就往左/往上翻。
                try
                {
                    Rect wa = SystemParameters.WorkArea;    // 单位是 DIP，和 Left/Top 一致
                    double w = win.ActualWidth, hh = win.ActualHeight;
                    double x = win.Left, y = win.Top;
                    if (x + w > wa.Right) x = Math.Max(wa.Left, wa.Right - w);
                    if (y + hh > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - hh);
                    if (x < wa.Left) x = wa.Left;
                    if (y < wa.Top) y = wa.Top;
                    win.Left = x;
                    win.Top = y;
                }
                catch { }
            };
            win.Closed += delegate(object o, EventArgs e)
            {
                try { Native.ReleaseCapture(); } catch { }
            };
            return win;
        }

        /// <summary>
        /// 抓一次菜单背后的屏幕，做高斯模糊，当作窗口的底。
        ///
        /// 为什么自己抓：系统提供的两条路在这台机器上都拿不到真磨砂（见 Loaded 里的说明）。
        /// 抓屏只在菜单打开时做一次，之后悬停、重绘都复用这张底图，没有持续开销。
        /// </summary>
        private static void ApplyBlurredBackdrop(Window win, Border root)
        {
            try
            {
                // 逻辑 -> 物理像素
                double sx = 1, sy = 1;
                try
                {
                    HwndSource src = (HwndSource)PresentationSource.FromVisual(win);
                    if (src != null && src.CompositionTarget != null)
                    {
                        sx = src.CompositionTarget.TransformToDevice.M11;
                        sy = src.CompositionTarget.TransformToDevice.M22;
                    }
                }
                catch { }

                int px = (int)Math.Round(win.Left * sx);
                int py = (int)Math.Round(win.Top * sy);
                int pw = (int)Math.Round(win.ActualWidth * sx);
                int ph = (int)Math.Round(win.ActualHeight * sy);
                if (pw <= 0 || ph <= 0) return;

                System.Drawing.Rectangle screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                if (px + pw > screen.Width) pw = screen.Width - px;
                if (py + ph > screen.Height) ph = screen.Height - py;
                if (pw <= 0 || ph <= 0) return;

                using (System.Drawing.Bitmap shot = new System.Drawing.Bitmap(pw, ph))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(shot))
                        g.CopyFromScreen(px, py, 0, 0, new System.Drawing.Size(pw, ph));

                    // 缩小再放大 = 廉价模糊，叠三层足够接近磨砂
                    System.Drawing.Bitmap blurred = BoxBlur(shot, 24);
                    System.Windows.Media.ImageSource img = ToImageSource(blurred);
                    if (img == null) return;

                    System.Windows.Media.ImageBrush brush = new System.Windows.Media.ImageBrush(img);
                    brush.Stretch = Stretch.Fill;
                    brush.Opacity = 0.62;      // 剩下的暗度由 AccentPolicy 的深色底补
                    root.Background = brush;
                }
            }
            catch { }
        }

        private static System.Drawing.Bitmap BoxBlur(System.Drawing.Bitmap src, int downscale)
        {
            int w = Math.Max(1, src.Width / downscale);
            int h = Math.Max(1, src.Height / downscale);
            using (System.Drawing.Bitmap small = new System.Drawing.Bitmap(w, h))
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, w, h);
                }
                System.Drawing.Bitmap big = new System.Drawing.Bitmap(src.Width, src.Height);
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(big))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(small, 0, 0, src.Width, src.Height);
                }
                return big;
            }
        }

        private static System.Windows.Media.ImageSource ToImageSource(System.Drawing.Bitmap bmp)
        {
            try
            {
                IntPtr h = bmp.GetHbitmap();
                try
                {
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        h, IntPtr.Zero, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
                finally { DeleteObject(h); }
            }
            catch { return null; }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        /// <summary>把窗口变成亚克力 + 圆角（Win11 支持圆角，旧系统忽略）。</summary>
        private static void ApplyAcrylic(IntPtr hwnd)
        {
            try
            {
                // Win11 的正路：DWMWA_SYSTEMBACKDROP_TYPE = 38，值 3 = Acrylic（真磨砂）。
                // Win10 上会返回失败，落到下面的旧接口。
                // 旧的 ACCENT_ENABLE_ACRYLICBLURBEHIND 在 Win11 上已基本失效 ——
                // 只调它的话窗口只是"变透"，看不到模糊（用户实测："只是通了，不磨砂"）。
                int backdrop = 3;   // 3 = Acrylic（稍重的磨砂）
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                if (hr != 0)
                {
                    backdrop = 2;   // 2 = Mica，Win11 更早期版本可用
                    hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                Config.Log("菜单背景: SYSTEMBACKDROP_TYPE hr=" + hr);

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

                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { }
        }
    }
}
