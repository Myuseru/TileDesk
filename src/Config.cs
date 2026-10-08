using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

namespace TileDesk
{
    /// <summary>
    /// 用户配置。字段名即 JSON 键名（小驼峰），长度单位是 100% DPI 下的逻辑像素。
    /// </summary>
    public class Config
    {
        // ---------- 卡片布局 ----------
        // ---------- 卡片布局 ----------
        public int cardWidth = 115;
        public int cardHeight = 172;            // 2:3，和 Steam 竖版封面一致
        public int gap = 14;
        public int padding = 28;
        public int cornerRadius = 10;
        /// <summary>整张卡片（含投影）的不透明度，1 = 完全不透明。</summary>
        public double cardOpacity = 1;

        // ---------- 正在播放控件（左下角）----------
        /// <summary>从屏幕右边缘往左拖 = 唤起通知中心（Win8/Win10 自带的手势，Win11 砍了）。</summary>
        public bool edgeSwipeNotify = true;
        /// <summary>桌面空闲多少秒后把卡片和按钮淡出（0 = 从不隐藏）。</summary>
        public double idleHideSeconds = 60;
        public bool showNowPlaying = true;
        public int npWidth = 690;
        public int npHeight = 84;
        public int npMargin = 14;
        public double npPanelOpacity = 0;
        /// <summary>播放器内的小电平条（显示的是屏幕底部那条大频谱，故默认关）。</summary>
        public bool showLevelMeter = false;

        // ---------- 音频频谱（屏幕底部居中）----------
        public bool showSpectrum = true;
        public int spectrumBars = 72;
        public int spectrumHeight = 65;
        public double spectrumWidth = 0.54;
        public double spectrumOpacity = 0.85;
        /// <summary>柱宽比例：1.0 = 柱间无缝，往小调柱子变细、缝变大。</summary>
        public double spectrumBarWidth = 0.85;
        /// <summary>柱子下落时间常数（秒）：越小越跳，越大越稳。</summary>
        public double spectrumFall = 0.12;

        // ---------- 时钟与天气 ----------
        public bool showDeskInfo = true;
        public string weatherCity = "";
        public bool weatherAnim = true;

        // ---------- 内容 / 排序 ----------
        public string sortMode = "Name";        // Name | Date | Kind | Custom
        public bool sortDescending = false;
        public bool watchDesktop = true;
        public bool autoArrange = false;
        public bool showLabels = false;
        public bool labelsOnHover = true;
        public string fontFamily = "Microsoft YaHei UI";
        public double uiScale = 0;

        // ---------- 亚克力（卡片底）----------
        public double acrylicTint = 0.02;
        public double acrylicBrightness = 0.96;
        public int acrylicBlur = 6;
        /// <summary>卡片深色模式：无封面卡片压成深色调（保留色相）。</summary>
        public bool darkCards = true;

        // ---------- 投影 / 按钮 ----------
        public double shadowOpacity = 0.6;
        public int shadowSize = 24;
        public bool showControlButton = true;
        /// <summary>时钟天气与右下按钮上提的逻辑像素（播放器不动）。</summary>
        public int bottomLift = 16;
        public string accentColor = "#4C9AFF";

        // ---------- 卡片顺序与位置（按名字持久化）----------
        public List<string> order = new List<string>();
        public List<string> positions = new List<string>();

        // ---------- 系统集成 ----------
        public bool downloadSteamArt = true;
        public string windowMode = "Bottom";    // Bottom | Desktop
        public bool autoStartWithWindows = true;
        public bool verboseLog = false;

        public List<string> hidden = new List<string>();
        public List<string> extraFolders = new List<string>();
        public List<string> exclude = new List<string>();

        // ---------- 路径 ----------

        private static string dataDir;
        private static string dataOverride;

        /// <summary>命令行 --data 或环境变量 TILEDESK_HOME 可覆盖数据目录。</summary>
        public static void SetDataOverride(string dir)
        {
            if (!string.IsNullOrEmpty(dir)) { dataOverride = dir; dataDir = null; }
        }

        /// <summary>上次探测数据目录失败的原因（仅用于日志）。</summary>
        public static string DataProbeNote = "";

        public static string DataDir
        {
            get
            {
                if (dataDir != null) return dataDir;
                foreach (string cand in DataCandidates())
                {
                    if (string.IsNullOrEmpty(cand)) continue;
                    try
                    {
                        Directory.CreateDirectory(cand);
                        string probe = Path.Combine(cand, ".write-probe");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);
                        dataDir = cand;
                        return dataDir;
                    }
                    catch (Exception ex)
                    {
                        if (DataProbeNote.Length < 400)
                            DataProbeNote += "[" + cand + " 不可写: " + ex.GetType().Name + "] ";
                    }
                }
                dataDir = Path.GetTempPath();
                return dataDir;
            }
        }

        private static List<string> DataCandidates()
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(dataOverride)) list.Add(dataOverride);
            string env = Environment.GetEnvironmentVariable("TILEDESK_HOME");
            if (!string.IsNullOrEmpty(env)) list.Add(env);

            string local = null, exeData = null;
            try
            {
                string p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(p)) local = Path.Combine(p, "TileDesk");
            }
            catch { }
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(exeDir)) exeData = Path.Combine(exeDir, "TileDesk-data");
            }
            catch { }

            // 已经在用的那个目录（里面有 config.json）优先。
            // 不这么做的话，运行环境一变（例如从受限进程启动），数据目录就会在
            // %LOCALAPPDATA% 和 exe 旁边之间「搬家」—— 表现就是封面重新下载、
            // 用户拖好的排序丢失。
            bool localInit = local != null && File.Exists(Path.Combine(local, "config.json"));
            bool exeInit = exeData != null && File.Exists(Path.Combine(exeData, "config.json"));

            if (localInit && !exeInit)
            {
                list.Add(local);
                if (exeData != null) list.Add(exeData);
            }
            else if (exeInit && !localInit)
            {
                list.Add(exeData);
                if (local != null) list.Add(local);
            }
            else
            {
                if (local != null) list.Add(local);
                if (exeData != null) list.Add(exeData);
            }

            list.Add(Path.Combine(Path.GetTempPath(), "TileDesk"));
            return list;
        }

        public static string ConfigPath
        {
            get { return Path.Combine(DataDir, "config.json"); }
        }

        public static string ArtCacheDir
        {
            get
            {
                string d = Path.Combine(DataDir, "art");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string LogPath
        {
            get { return Path.Combine(DataDir, "tiledesk.log"); }
        }

        /// <summary>用户自定封面的存放目录。文件名 = 项目标识，所以换封面就是换这个文件。</summary>
        public static string CustomCoverDir
        {
            get
            {
                string d = Path.Combine(DataDir, "covers");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        // ---------- 读写 ----------

        public static Config Load()
        {
            Config cfg = new Config();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    using (FileStream fs = File.OpenRead(ConfigPath))
                    {
                        DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(Config));
                        object o = ser.ReadObject(fs);
                        if (o != null) cfg = (Config)o;
                    }
                }
                else
                {
                    cfg.Save();
                }
                // 读到的配置立刻作用到日志开关上（默认 false = 平时不写详细日志，
                // 只留启动和异常行；要复现 bug 时在菜单/设置里打开）
                Verbose = cfg.verboseLog;
            }
            catch (Exception ex)
            {
                Log("读取配置失败，使用默认值: " + ex.Message);
                try { File.Copy(ConfigPath, ConfigPath + ".bad", true); } catch { }
                cfg = new Config();
            }
            if (cfg.hidden == null) cfg.hidden = new List<string>();
            if (cfg.extraFolders == null) cfg.extraFolders = new List<string>();
            if (cfg.exclude == null) cfg.exclude = new List<string>();
            if (cfg.order == null) cfg.order = new List<string>();
            if (cfg.positions == null) cfg.positions = new List<string>();
            if (!string.Equals(cfg.windowMode, "Desktop", StringComparison.OrdinalIgnoreCase)) cfg.windowMode = "Bottom";
            return cfg;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(ConfigPath, PrettyJson(this), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("保存配置失败: " + ex.Message); }
        }

        private static string PrettyJson(Config c)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"cardWidth\": ").Append(c.cardWidth).Append(",\n");
            sb.Append("  \"cardHeight\": ").Append(c.cardHeight).Append(",\n");
            sb.Append("  \"gap\": ").Append(c.gap).Append(",\n");
            sb.Append("  \"padding\": ").Append(c.padding).Append(",\n");
            sb.Append("  \"cornerRadius\": ").Append(c.cornerRadius).Append(",\n");
            sb.Append("  \"cardOpacity\": ").Append(Num(c.cardOpacity)).Append(",\n");
            // 这两个字段也必须写出去，否则设置界面里的改动会在下次 Save() 时被悄悄丢掉
            sb.Append("  \"idleHideSeconds\": ").Append(Num(c.idleHideSeconds)).Append(",\n");
            sb.Append("  \"edgeSwipeNotify\": ").Append(Bool(c.edgeSwipeNotify)).Append(",\n");
            sb.Append("  \"showNowPlaying\": ").Append(Bool(c.showNowPlaying)).Append(",\n");
            sb.Append("  \"npWidth\": ").Append(c.npWidth).Append(",\n");
            sb.Append("  \"npHeight\": ").Append(c.npHeight).Append(",\n");
            sb.Append("  \"npMargin\": ").Append(c.npMargin).Append(",\n");
            sb.Append("  \"npPanelOpacity\": ").Append(Num(c.npPanelOpacity)).Append(",\n");
        sb.Append("  \"showLevelMeter\": ").Append(Bool(c.showLevelMeter)).Append(",\n");
        sb.Append("  \"bottomLift\": ").Append(c.bottomLift).Append(",\n");
        sb.Append("  \"showSpectrum\": ").Append(Bool(c.showSpectrum)).Append(",\n");
        sb.Append("  \"spectrumBars\": ").Append(c.spectrumBars).Append(",\n");
        sb.Append("  \"spectrumHeight\": ").Append(c.spectrumHeight).Append(",\n");
        sb.Append("  \"spectrumWidth\": ").Append(Num(c.spectrumWidth)).Append(",\n");
        sb.Append("  \"spectrumOpacity\": ").Append(Num(c.spectrumOpacity)).Append(",\n");
        sb.Append("  \"spectrumBarWidth\": ").Append(Num(c.spectrumBarWidth)).Append(",\n");
        sb.Append("  \"spectrumFall\": ").Append(Num(c.spectrumFall)).Append(",\n");
            sb.Append("  \"showDeskInfo\": ").Append(Bool(c.showDeskInfo)).Append(",\n");
            sb.Append("  \"weatherCity\": ").Append(Str(c.weatherCity)).Append(",\n");
            sb.Append("  \"weatherAnim\": ").Append(Bool(c.weatherAnim)).Append(",\n");
            sb.Append("  \"verboseLog\": ").Append(Bool(c.verboseLog)).Append(",\n");
            sb.Append("  \"uiScale\": ").Append(Num(c.uiScale)).Append(",\n");
            sb.Append("  \"sortMode\": ").Append(Str(c.sortMode)).Append(",\n");
            sb.Append("  \"sortDescending\": ").Append(Bool(c.sortDescending)).Append(",\n");
            sb.Append("  \"order\": ").Append(Arr(c.order)).Append(",\n");
            sb.Append("  \"watchDesktop\": ").Append(Bool(c.watchDesktop)).Append(",\n");
            sb.Append("  \"autoArrange\": ").Append(Bool(c.autoArrange)).Append(",\n");
            sb.Append("  \"positions\": ").Append(Arr(c.positions)).Append(",\n");
            sb.Append("  \"showLabels\": ").Append(Bool(c.showLabels)).Append(",\n");
            sb.Append("  \"labelsOnHover\": ").Append(Bool(c.labelsOnHover)).Append(",\n");
            sb.Append("  \"fontFamily\": ").Append(Str(c.fontFamily)).Append(",\n");
            sb.Append("  \"acrylicTint\": ").Append(Num(c.acrylicTint)).Append(",\n");
            sb.Append("  \"acrylicBrightness\": ").Append(Num(c.acrylicBrightness)).Append(",\n");
            sb.Append("  \"acrylicBlur\": ").Append(Num(c.acrylicBlur)).Append(",\n");
        sb.Append("  \"darkCards\": ").Append(Bool(c.darkCards)).Append(",\n");
            sb.Append("  \"shadowOpacity\": ").Append(Num(c.shadowOpacity)).Append(",\n");
            sb.Append("  \"shadowSize\": ").Append(Num(c.shadowSize)).Append(",\n");
            sb.Append("  \"showControlButton\": ").Append(Bool(c.showControlButton)).Append(",\n");
            sb.Append("  \"accentColor\": ").Append(Str(c.accentColor)).Append(",\n");
            sb.Append("  \"downloadSteamArt\": ").Append(Bool(c.downloadSteamArt)).Append(",\n");
            sb.Append("  \"windowMode\": ").Append(Str(c.windowMode)).Append(",\n");
            sb.Append("  \"autoStartWithWindows\": ").Append(Bool(c.autoStartWithWindows)).Append(",\n");
            sb.Append("  \"hidden\": ").Append(Arr(c.hidden)).Append(",\n");
            sb.Append("  \"extraFolders\": ").Append(Arr(c.extraFolders)).Append(",\n");
            sb.Append("  \"exclude\": ").Append(Arr(c.exclude)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Bool(bool b) { return b ? "true" : "false"; }
        private static string Num(double d) { return d.ToString("0.###", CultureInfo.InvariantCulture); }

        private static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string Arr(List<string> items)
        {
            if (items == null || items.Count == 0) return "[]";
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Str(items[i]));
            }
            return sb.Append(']').ToString();
        }

        // ---------- 日志 ----------

        private static readonly object logLock = new object();

        /// <summary>日志上限 1 MB：超过就把当前文件挪成 .1（覆盖上一份），新日志从空开始。</summary>
        private const long LogMaxBytes = 1024 * 1024;

        /// <summary>
        /// 详细日志开关（默认关）。
        ///
        /// **关着的时候一行都不写**（用户明确要求：平时不要有日志，要复现 bug 时才打开开关）。
        /// 打开后写全量诊断。开关本身的状态变化会记一行，方便确认它真的生效了。
        /// </summary>
        public static bool Verbose = false;

        public static void Log(string message)
        {
            try
            {
                if (!Verbose) return;      // 平时彻底静默，日志文件连创建都不会创建
                lock (logLock)
                {
                    // 不做轮转这个文件会无限膨胀（实测跑一阵就 400+ KB / 5000+ 行，
                    // 里面大部分是"控件渲染 / 帧统计"这类周期性诊断）。最多留两份。
                    try
                    {
                        FileInfo fi = new FileInfo(LogPath);
                        if (fi.Exists && fi.Length > LogMaxBytes)
                        {
                            string bak = LogPath + ".1";
                            if (File.Exists(bak)) File.Delete(bak);
                            File.Move(LogPath, bak);
                        }
                    }
                    catch { }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}