using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TileDesk
{
    /// <summary>
    /// Windows 11 材质层。
    ///
    /// ★ 这一版最重要的一件事：把亚克力的**配比**做对。
    ///
    ///   2.x / 3.0 的做法是「重度模糊的壁纸 + 一点点压暗」，也就是
    ///   80% 的模糊背景 + 20% 的着色 —— 那看起来就是一块糊掉的图，
    ///   也就是用户说的「糊成一团」。
    ///
    ///   WinUI 3 的 AcrylicBrush 正好反过来：
    ///       TintColor #202020, TintOpacity 0.8, TintLuminosityOpacity 0.5, Blur 30px
    ///   即 **80% 是那层近乎不透明的着色，只有 20% 是背景的模糊**。
    ///   所以原生亚克力看上去是一块干净的深色玻璃，只是隐约透出背后的色相；
    ///   而不是"能看清背后是什么"的毛玻璃。
    ///
    ///   把配比翻过来之后，卡片立刻从"糊掉的壁纸"变成"Win11 的玻璃"。
    ///
    /// 另一件事：原生亚克力背后永远有东西在动，而磁贴墙在桌面层，
    /// 背后**永远是壁纸本身**（没有别的窗口）。所以"实时模糊"和
    /// "预先模糊的壁纸"在这里是同一张图 —— 不需要为每张卡开独立窗口
    /// 去骗 DWM 的 backdrop，那只会把 11 张卡变成 11 个 HWND。
    /// </summary>
    internal static class Material
    {
        // ---- WinUI 3 AcrylicBrush 的默认参数 ----
        public const float DefaultTintOpacity = 0.80f;   // 着色层不透明度
        public const float DefaultSaturation = 1.50f;    // 背景饱和度提升
        public const int DefaultBlur = 30;               // 模糊半径（逻辑像素）
        public const float BrightnessDark = 0.125f;      // #202020
        public const float BrightnessLight = 0.95f;      // #F3F3F3

        // ---- 噪点 ----
        private const int NoiseSize = 256;
        private static readonly Dictionary<int, Bitmap> noiseCache = new Dictionary<int, Bitmap>();
        private static readonly object noiseLock = new object();

        /// <summary>
        /// 一张可平铺的细噪点，alpha 上限由 <paramref name="maxAlpha"/> 决定（1..255）。
        ///
        /// 必须是 **Format32bppArgb（非预乘）**：用 32bppPArgb 写"alpha=8 + 颜色 0..255"
        /// 是不合法的预乘数据（预乘要求 颜色 ≤ alpha），GDI+ 按预乘解释后整块变成电视雪花。
        ///
        /// 噪点用中灰而不是纯白：纯白会在深色玻璃上变成一层"灰尘"，
        /// Win11 的噪点是一层几乎察觉不到的亮度起伏。
        ///
        /// ★ 强度必须体现在**纹理的 alpha** 上。TextureBrush 不支持 ImageAttributes，
        ///   所以不能"画的时候再调淡"——只能按强度生成不同的纹理并缓存。
        ///   旧实现里 strength 只用来判断"要不要画"，纹理 alpha 永远固定 0..12，
        ///   于是「磨砂颗粒」滑条拖到底也不会有任何变化（用户实测"根本没有生效"）。
        /// </summary>
        public static Bitmap Noise(int maxAlpha)
        {
            int a = Math.Max(1, Math.Min(255, maxAlpha));
            lock (noiseLock)
            {
                Bitmap hit;
                if (noiseCache.TryGetValue(a, out hit)) return hit;

                Bitmap b = new Bitmap(NoiseSize, NoiseSize, PixelFormat.Format32bppArgb);
                Random rnd = new Random(20260101);
                BitmapData bd = b.LockBits(new Rectangle(0, 0, NoiseSize, NoiseSize),
                                           ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[bd.Stride];
                    for (int y = 0; y < NoiseSize; y++)
                    {
                        for (int x = 0; x < NoiseSize; x++)
                        {
                            int i = x * 4;
                            // 32..224：对比度 192，比旧版 96..208（112）高得多。
                            // Win7 Aero 的磨砂是"细密的高对比颗粒"，不是几乎看不见的起伏；
                            // 太低的对比度在低不透明度卡片上会完全消失。
                            byte v = (byte)(32 + rnd.Next(0, 193));
                            row[i] = v; row[i + 1] = v; row[i + 2] = v;
                            row[i + 3] = (byte)rnd.Next(0, a + 1);
                        }
                        Marshal.Copy(row, 0, (IntPtr)((long)bd.Scan0 + y * bd.Stride), row.Length);
                    }
                }
                finally { b.UnlockBits(bd); }

                // 只留最近用过的几档，避免拖滑条时无限增长
                if (noiseCache.Count >= 12)
                {
                    int firstKey = 0;
                    foreach (int k in noiseCache.Keys) { firstKey = k; break; }
                    noiseCache[firstKey].Dispose();
                    noiseCache.Remove(firstKey);
                }
                noiseCache[a] = b;
                return b;
            }
        }

        /// <summary>默认强度（14）的噪点，兼容旧调用。</summary>
        public static Bitmap Noise() { return Noise(14); }

        /// <summary>
        /// 饱和度 + 亮度矩阵（**亮度守恒**）。
        ///
        /// 每行系数之和恒为 1：饱和度 1 时是单位矩阵，调大只拉开通道差异，整体明度不变。
        ///
        /// ★ 非对角线系数必须用**该行通道自己的亮度权重**。
        ///   GDI+ 是"行向量 × 矩阵"：R' = R*M00 + G*M10 + B*M20，
        ///   所以 R' 里 G 的系数是 lg*inv（不是 lr*inv）。
        ///   旧实现把行和列写反了：M10/M20 都用了 lr*inv，M01/M21 都用了 lg*inv——
        ///   结果饱和度一偏离 1 就整体串色（用户实测：&gt;1 偏红、&lt;1 偏绿）。
        /// </summary>
        public static ColorMatrix SatBright(float sat, float bright)
        {
            if (sat < 0f) sat = 0f;
            if (sat > 3f) sat = 3f;
            float inv = 1f - sat;
            const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            ColorMatrix m = new ColorMatrix();
            // R' = (lr*inv + sat)*R + (lg*inv)*G + (lb*inv)*B
            m.Matrix00 = (lr * inv + sat) * bright;
            m.Matrix01 = (lr * inv) * bright;
            m.Matrix02 = (lr * inv) * bright;
            // G' = (lr*inv)*R + (lg*inv + sat)*G + (lb*inv)*B
            m.Matrix10 = (lg * inv) * bright;
            m.Matrix11 = (lg * inv + sat) * bright;
            m.Matrix12 = (lg * inv) * bright;
            // B' = (lr*inv)*R + (lg*inv)*G + (lb*inv + sat)*B
            m.Matrix20 = (lb * inv) * bright;
            m.Matrix21 = (lb * inv) * bright;
            m.Matrix22 = (lb * inv + sat) * bright;
            return m;
        }

        /// <summary>
        /// 把 0..1 的明度换算成着色色。
        ///
        /// ★ 纯中性灰（R=G=B）。早期版本在深色时做 B+4 / R-1 的"冷调"，
        ///   想模仿 Win11 的冷灰感 —— 但叠加在浅色/粉色壁纸上会整体偏紫，
        ///   而且因为 tint 是全局固定的，所有卡片不管什么位置都是同一个紫色调
        ///   （用户实测："所有卡片同一个色调，哪里来的"）。
        ///   要色相就直接换壁纸或调「玻璃明度」，不要让材质偷偷加色。
        /// </summary>
        public static Color TintFromBrightness(double brightness)
        {
            double b = Math.Max(0.0, Math.Min(1.0, brightness));
            int v = (int)Math.Round(255 * b);
            return Color.FromArgb(v, v, v);
        }

        /// <summary>
        /// 画一层亚克力。
        ///
        /// <paramref name="source"/> 是模糊后的背景（壁纸缩略图或 header 色底），
        /// <paramref name="src"/> 是要取的那一块（源像素坐标）。
        /// <paramref name="opacity"/> 是整块材质的透明度（0..1），用来实现"玻璃浓度"。
        /// </summary>
        public static void PaintAcrylic(Graphics g, RectangleF dest, float radius,
                                        Bitmap source, RectangleF src,
                                        Color tint, double tintOpacity, double saturation,
                                        double opacity)
        {
            PaintAcrylic(g, dest, radius, source, src, tint, tintOpacity, saturation,
                         opacity, 14, 1);
        }

        /// <summary>带磨砂颗粒参数的版本。</summary>
        public static void PaintAcrylic(Graphics g, RectangleF dest, float radius,
                                        Bitmap source, RectangleF src,
                                        Color tint, double tintOpacity, double saturation,
                                        double opacity, double noise, double noiseScale)
        {
            if (opacity <= 0.004) return;
            float op = (float)Math.Max(0.0, Math.Min(1.0, opacity));

            using (GraphicsPath path = Shapes.Round(dest, radius))
            {
                Region saved = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                try
                {
                    if (source != null && src.Width > 0.5f && src.Height > 0.5f)
                    {
                        // 1) 背景模糊（顺带提饱和：Win11 会把背景的彩度拉起来，
                        //    不然透过深色玻璃看就是一片灰）
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(SatBright((float)saturation, 1f));
                            g.DrawImage(source, Rectangle.Round(dest),
                                        src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                        }

                        // 2) 着色层 —— 这是亚克力的主体（默认 80%）
                        int a = (int)Math.Round(255 * tintOpacity * op);
                        if (a > 0)
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, tint)))
                                g.FillRectangle(b, dest);

                        // 3) 磨砂颗粒（噪点）—— 强度直接体现在纹理 alpha 上
                        int na = (int)Math.Round(Math.Max(0, noise) * op);
                        if (na > 0)
                        {
                            using (TextureBrush nb = new TextureBrush(Noise(na)))
                            {
                                nb.WrapMode = WrapMode.Tile;
                                float ns = (float)Math.Max(0.5, Math.Min(8.0, noiseScale));
                                if (Math.Abs(ns - 1f) > 0.01f) nb.ScaleTransform(ns, ns);
                                g.FillRectangle(nb, dest);
                            }
                        }
                    }
                    else
                    {
                        // 拿不到壁纸：退回 Win11 的 solid fallback（不透明实色），
                        // 半透明的实色会让桌面从卡片里透出来，比实色更难看。
                        Color solid = Mix(tint, Color.Black, 0.10f);
                        using (SolidBrush b = new SolidBrush(solid)) g.FillRectangle(b, dest);
                    }
                }
                finally { g.Clip = saved; }
            }
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>悬停时给材质加的一层极淡白：Win11 的 hover 就是"底色亮 4%"，不是放大发光。</summary>
        public static void PaintHoverWash(Graphics g, RectangleF dest, float radius, int alpha)
        {
            if (alpha <= 0) return;
            using (GraphicsPath p = Shapes.Round(dest, radius))
            {
                Region saved = g.Clip;
                g.SetClip(p, CombineMode.Intersect);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    g.FillRectangle(b, dest);
                g.Clip = saved;
            }
        }
    }
}
