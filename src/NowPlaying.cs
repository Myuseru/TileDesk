using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TileDesk
{
    /// <summary>
    /// 「正在播放」的数据快照。纯数据 + 便捷计算，没有任何绘制逻辑。
    /// UI 直接读这个对象即可。
    /// </summary>
    public class NowPlayingState
    {
        /// <summary>QQ 音乐的进程在不在。</summary>
        public bool AppRunning;

        /// <summary>系统媒体会话（SMTC）里能不能找到 QQ 音乐。QQ音乐没播放过东西时可能没有。</summary>
        public bool HasSession;

        public string Title = "";
        public string Artist = "";
        public string Album = "";

        /// <summary>true = 正在播放；false = 暂停。</summary>
        public bool Playing;

        /// <summary>采样时刻的播放位置。</summary>
        public TimeSpan Position;

        /// <summary>曲目总长。</summary>
        public TimeSpan Duration;

        /// <summary>Position 的采样时刻（本地时间）。UI 想做平滑进度条就按它插值。</summary>
        public DateTime SampledAt = DateTime.MinValue;

        public bool CanPrev, CanNext, CanPlay, CanPause;

        /// <summary>专辑封面。可能为 null。**不要 Dispose 它**，换歌时监控器会回收。</summary>
        public Bitmap Cover;

        /// <summary>内容每变一次 +1，UI 可以用它做廉价的变更检测。</summary>
        public int Version;

        /// <summary>
        /// 最近一次进度条跳转被播放器"假装接受"（返回 true 但位置没动）的时刻。
        /// QQ 音乐就是这样：TryChangePlaybackPositionAsync 永远返回 true，但根本不动。
        /// UI 拿它来提示用户"不是你没拖到"，而不是让进度条跳一下又弹回去。
        /// </summary>
        public int SeekRejectedAt;

        /// <summary>按采样时刻插值出来的「此刻」播放位置（暂停时就等于 Position）。</summary>
        public TimeSpan PositionNow
        {
            get
            {
                if (!Playing || SampledAt == DateTime.MinValue) return Position;
                TimeSpan d = DateTime.Now - SampledAt;
                if (d < TimeSpan.Zero) d = TimeSpan.Zero;
                TimeSpan p = Position + d;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p;
            }
        }

        /// <summary>进度 0..1。时长未知时返回 0。</summary>
        public double Progress
        {
            get
            {
                if (Duration <= TimeSpan.Zero) return 0.0;
                double r = PositionNow.TotalSeconds / Duration.TotalSeconds;
                return r < 0 ? 0 : (r > 1 ? 1 : r);
            }
        }

        /// <summary>是否有实际内容可显示。</summary>
        public bool HasContent { get { return Title.Length > 0; } }
    }

    /// <summary>
    /// QQ 音乐「正在播放」监控器 + 控制。
    ///
    /// 数据来源是 Windows 自带的 SMTC（系统媒体传输控件）——
    /// QQ 音乐本来就会往系统媒体面板上报状态，所以这是最正规、最省事的数据源：
    /// 曲名 / 歌手 / 专辑 / 专辑封面 / 播放状态 / 进度 全都有，
    /// 上一首 / 下一首 / 播放暂停 / 跳转 也都能通过它下发。
    ///
    /// 这个类**只负责数据和控制**，不画任何东西。
    /// UI 侧：读 <see cref="State"/>，或者订阅 <see cref="Changed"/>；
    ///        <see cref="Visibility"/> 是已经做好的 0..1 平滑值（QQ音乐开→升到1，退出→降到0），
    ///        可以直接当不透明度用，想做自己的动画就忽略它、只看 State.AppRunning。
    /// </summary>
    public class NowPlayingMonitor : IDisposable
    {
        private const string TargetProcess = "QQMusic";
        private const string TargetAumid = "QQMusic";
        private const int UiTickMs = 50;        // 平滑 visibility 的步进
        private const int SessionPollMs = 250;  // 查询 SMTC 的周期（越小延迟越低；SMTC 是进程内调用，很便宜）
        private const int ProcessPollMs = 700;  // 查询进程的周期（决定淡入淡出多快响应）

        private readonly object sync = new object();
        private NowPlayingState state = new NowPlayingState();
        // 待验证的跳转（播放器不认时要能发现）
        private bool seekPending;
        private TimeSpan seekTarget;
        private int seekPendingAt;

        private Thread worker;
        private volatile bool running;

        private GlobalSystemMediaTransportControlsSessionManager manager;
        private GlobalSystemMediaTransportControlsSession session;

        // visibility 不靠循环步进，而是记下「起点值 + 起点时刻 + 终点值」，
        // 取值时按时间插值 —— 循环步进只有 20Hz（50ms 一次还有抖动），
        // 再快的渲染也救不回来，动画就是一顿一顿的。
        private float visFrom, visTo;
        private int visStartTick;
        private bool visInit;
        private bool lastAppRunning;
        private const float VisFadeSec = 0.8f;   // 淡入淡出时长

        private string lastKey = "";            // 上次取封面的 (title|artist)，用来判断要不要重新取
        private int coverTries;                 // 当前曲目的封面重试次数
        private int coverRetryAt;                // 下次可以重试的时刻（TickCount）
        private string coverDoneKey = "";         // 已经取到封面的曲目 key
        private readonly Queue<Bitmap> retired = new Queue<Bitmap>();

        /// <summary>后台线程触发（不是 UI 线程）。只想在 UI 线程用，就轮询 State.Version。</summary>
        public event EventHandler Changed;

        public NowPlayingMonitor()
        {
            visInit = true;   // 初始不可见
        }

        public NowPlayingState State
        {
            get { lock (sync) return state; }
        }

        /// <summary>
        /// 免锁快照。UI 线程每秒要读几十次状态，如果每次都去抢 sync 锁，
        /// 就会跟后台轮询线程互相等 —— 实测 30 次/秒的锁竞争能吃掉 11% CPU。
        /// 这里只发布一个引用，读侧不加锁（单次引用读取在 .NET 里是原子的）。
        /// </summary>
        public NowPlayingState Latest { get { return latest; } }
        private volatile NowPlayingState latest;

        /// <summary>0..1 的平滑可见度：QQ 音乐在跑就升到 1，退出就降到 0（约 0.8 秒，ease-out）。</summary>
        public float Visibility
        {
            get
            {
                if (!visInit) return 0f;
                float t = unchecked(Environment.TickCount - visStartTick) / (VisFadeSec * 1000f);
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float e = 1f - (1f - t) * (1f - t);   // ease-out，收尾更自然
                return visFrom + (visTo - visFrom) * e;
            }
        }

        /// <summary>系统是否支持 SMTC（Win10 1809+ 才有）。</summary>
        public bool Supported { get; private set; }

        public void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Name = "TileDesk-NowPlaying";
            worker.SetApartmentState(ApartmentState.MTA);   // WinRT 走 MTA
            worker.Start();
        }

        public void Stop()
        {
            running = false;
            try { if (worker != null && worker.IsAlive) worker.Join(1500); } catch { }
            worker = null;
        }

        public void Dispose()
        {
            Stop();
            lock (sync)
            {
                if (state.Cover != null) { try { state.Cover.Dispose(); } catch { } state.Cover = null; }
                while (retired.Count > 0) { try { retired.Dequeue().Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// UI 线程在空闲时调一下，回收换歌时淘汰下来的旧封面。
        /// 不调也不会漏很久（GC 会兜底），但调了更干净。
        /// </summary>
        public void DrainRetired()
        {
            while (true)
            {
                Bitmap b = null;
                lock (sync) { if (retired.Count > 0) b = retired.Dequeue(); }
                if (b == null) return;
                lock (Gdi.Lock) { try { b.Dispose(); } catch { } }
            }
        }

        // ==================== 控制 ====================

        public bool Previous() { return Fire("上一首", delegate(GlobalSystemMediaTransportControlsSession s) { return s.TrySkipPreviousAsync(); }); }
        public bool Next() { return Fire("下一首", delegate(GlobalSystemMediaTransportControlsSession s) { return s.TrySkipNextAsync(); }); }

        /// <summary>播放/暂停切换（一条命令，比自己判断状态再发更可靠）。</summary>
        public bool TogglePlayPause()
        {
            return Fire("播放/暂停", delegate(GlobalSystemMediaTransportControlsSession s) { return s.TryTogglePlayPauseAsync(); });
        }

        public bool Play() { return Fire("播放", delegate(GlobalSystemMediaTransportControlsSession s) { return s.TryPlayAsync(); }); }
        public bool Pause() { return Fire("暂停", delegate(GlobalSystemMediaTransportControlsSession s) { return s.TryPauseAsync(); }); }

        /// <summary>跳到指定位置（进度条拖动用）。</summary>
        public bool Seek(TimeSpan to)
        {
            if (to < TimeSpan.Zero) to = TimeSpan.Zero;
            long ticks = (long)(to.TotalSeconds * 10000000.0);   // 100ns 单位
            GlobalSystemMediaTransportControlsSession s = session;
            if (s == null) return false;
            try
            {
                IAsyncOperation<bool> op = s.TryChangePlaybackPositionAsync(ticks);
                bool ok = WaitBool(op);
                // 以前这里把返回值丢了 —— QQ 音乐要是不支持位置跳转就会静默失败，
                // 表现成"拖进度条没反应"，而且查不出原因。
                Config.Log("跳转[" + ((int)to.TotalMinutes) + ":" + to.Seconds.ToString("00") + "] -> " + (ok ? "成功" : "被播放器拒绝"));
                // 记下来，下一轮轮询去核实播放器到底动没动
                seekPending = true;
                seekTarget = to;
                seekPendingAt = Environment.TickCount;
                // 立刻把本地状态挪过去，UI 不用等下一次轮询
                lock (sync)
                {
                    state.Position = to;
                    state.SampledAt = DateTime.Now;
                    state.Version++;
                }
                return true;
            }
            catch (Exception ex) { Config.Log("跳转失败: " + ex.Message); return false; }
        }

        private bool Fire(string what, Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> call)
        {
            GlobalSystemMediaTransportControlsSession s = session;
            if (s == null)
            {
                Config.Log("控制[" + what + "]失败：还没有 QQ 音乐的媒体会话");
                return false;
            }
            try
            {
                bool ok = WaitBool(call(s));
                Config.Log("控制[" + what + "] -> " + (ok ? "成功" : "被拒绝"));
                return ok;
            }
            catch (Exception ex) { Config.Log("控制[" + what + "]异常: " + ex.Message); return false; }
        }

        // ==================== 主循环 ====================

        private void Loop()
        {
            int sinceSession = int.MaxValue, sinceProcess = int.MaxValue;
            bool appRunning = false;

            while (running)
            {
                try
                {
                    if (sinceProcess >= ProcessPollMs)
                    {
                        sinceProcess = 0;
                        appRunning = IsAppRunning();
                    }
                    if (sinceSession >= SessionPollMs)
                    {
                        sinceSession = 0;
                        PollSession(appRunning);
                    }

                    // 目标一变就记下「当前值 + 时刻 + 新目标」，实际取值时按时间插值，
                    // 这样动画的平滑度只取决于渲染帧率，不受这个 50ms 循环的限制。
                    //
                    // 显示条件：QQ 音乐进程在跑，**或者**系统里有任何活跃的媒体会话 ——
                    // 这样网易云音乐等其它播放器也能把控件叫出来（它们不上报进程名）。
                    bool show = appRunning || session != null;
                    if (show != lastAppRunning)
                    {
                        visFrom = Visibility;
                        visTo = show ? 1f : 0f;
                        visStartTick = unchecked(Environment.TickCount);
                        visInit = true;
                        lastAppRunning = show;
                    }
                }
                catch (Exception ex) { Config.Log("NowPlaying 循环异常: " + ex.Message); }

                Thread.Sleep(UiTickMs);
                sinceSession += UiTickMs;
                sinceProcess += UiTickMs;
            }
        }

        private static bool IsAppRunning()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName(TargetProcess);
                bool any = ps.Length > 0;
                foreach (Process p in ps) { try { p.Dispose(); } catch { } }
                return any;
            }
            catch { return false; }
        }

        private string sessionSig = "";

        private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo pi = s.GetPlaybackInfo();
                return pi != null &&
                       pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { return false; }
        }

        private void PollSession(bool appRunning)
        {
            if (manager == null)
            {
                try
                {
                    manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    Supported = manager != null;
                    Config.Log(Supported
                        ? "NowPlaying: SMTC 就绪"
                        : "NowPlaying: 系统不支持 SMTC（需要 Win10 1809+）");
                }
                catch (Exception ex)
                {
                    Config.Log("NowPlaying: 获取 SMTC 管理器失败: " + ex.Message);
                    manager = null;
                    Supported = false;
                    return;
                }
            }

            GlobalSystemMediaTransportControlsSession found = null;
            try
            {
                // 选择顺序：**QQ 音乐优先 → 任何"正在播放"的会话 → 任何有会话的播放器**。
                // SMTC 是系统级接口，"网易云音乐 / Spotify / PotPlayer…" 只要按规范上报
                // 就能被拿到，不需要为每个播放器写代码。以前这里只认 TargetAumid，
                // 等于把其它播放器全排除了。
                IReadOnlyList<GlobalSystemMediaTransportControlsSession> list = manager.GetSessions();
                GlobalSystemMediaTransportControlsSession anyPlaying = null, anySession = null;
                StringBuilder sig = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    string id = list[i].SourceAppUserModelId;
                    bool isTarget = id != null && id.IndexOf(TargetAumid, StringComparison.OrdinalIgnoreCase) >= 0;
                    string status = "";
                    try
                    {
                        GlobalSystemMediaTransportControlsSessionPlaybackInfo pi = list[i].GetPlaybackInfo();
                        if (pi != null) status = pi.PlaybackStatus.ToString();
                    }
                    catch { }
                    sig.Append(id).Append('(').Append(status).Append(") ");
                    if (isTarget) { if (found == null) found = list[i]; }
                    else if (string.Equals(status, "Playing", StringComparison.OrdinalIgnoreCase))
                    {
                        if (anyPlaying == null) anyPlaying = list[i];
                    }
                    else if (anySession == null) anySession = list[i];
                }

                // 目标在，但没在放歌、而别的播放器正在放 -> 用正在放的那个
                if (found == null || !IsPlaying(found))
                {
                    if (anyPlaying != null) found = anyPlaying;
                    else if (found == null) found = anySession;
                }

                string sigNow = sig.ToString().Trim();
                if (!string.Equals(sigNow, sessionSig))
                {
                    sessionSig = sigNow;
                    Config.Log("NowPlaying: 发现的会话 -> " + (sigNow.Length > 0 ? sigNow : "（无）"));
                }
            }
            catch (Exception ex) { Config.Log("NowPlaying: 枚举会话失败: " + ex.Message); }

            session = found;

            NowPlayingState next = new NowPlayingState();
            next.AppRunning = appRunning;
            next.HasSession = found != null;

            if (found != null)
            {
                try
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackInfo info = found.GetPlaybackInfo();
                    next.Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (info.Controls != null)
                    {
                        next.CanPrev = info.Controls.IsPreviousEnabled;
                        next.CanNext = info.Controls.IsNextEnabled;
                        next.CanPlay = info.Controls.IsPlayEnabled;
                        next.CanPause = info.Controls.IsPauseEnabled;
                    }

                    GlobalSystemMediaTransportControlsSessionTimelineProperties tl = found.GetTimelineProperties();
                    next.Position = tl.Position;
                    next.Duration = tl.EndTime;
                    next.SampledAt = DateTime.Now;

                    GlobalSystemMediaTransportControlsSessionMediaProperties props =
                        Wait(found.TryGetMediaPropertiesAsync());
                    if (props != null)
                    {
                        next.Title = props.Title == null ? "" : props.Title;
                        next.Artist = props.Artist == null ? "" : props.Artist;
                        next.Album = props.AlbumTitle == null ? "" : props.AlbumTitle;

                        string key = next.Title + "|" + next.Artist;
                        if (key != lastKey)
                        {
                            lastKey = key;
                            coverTries = 0;
                            coverRetryAt = 0;
                        }

                        // "这首歌的封面拿到过没有"必须按 key 记，不能只看 state.Cover 空不空：
                        // 换歌那一瞬间状态里还留着上一首的封面，只判断空不空就会以为
                        // "已经有了"而永远不去取新封面 —— 结果新歌一直顶着旧图。
                        if (!string.Equals(coverDoneKey, key))
                        {
                            if (coverTries < 25 && unchecked(Environment.TickCount - coverRetryAt) >= 0)
                            {
                                Bitmap got = FetchCover(props);
                                if (got != null) { next.Cover = got; coverDoneKey = key; coverTries = 0; }
                                else
                                {
                                    coverTries++;
                                    coverRetryAt = unchecked(Environment.TickCount + 1200);
                                }
                            }
                            // 取不到就让 next.Cover 保持 null：空着也比顶着上一首的封面强
                        }
                        else
                        {
                            lock (sync) next.Cover = state.Cover;   // 这首已经拿到过，沿用
                        }
                    }
                }
                catch (Exception ex) { Config.Log("NowPlaying: 读取会话状态失败: " + ex.Message); }
            }

            bool changed;
            lock (sync)
            {
                bool forcePublish = false;   // 跳转被拒时 Differs 看不出来，得强制发布一次
                // ---- 核实上一次跳转有没有真的生效 ----
                // SMTC 只告诉我们"请求被接受"，不代表播放器真的跳了。
                if (seekPending && unchecked(Environment.TickCount - seekPendingAt) > 500)
                {
                    seekPending = false;
                    double off = Math.Abs((next.Position - seekTarget).TotalSeconds);
                    if (off > 4.0)
                    {
                        next.SeekRejectedAt = Environment.TickCount;
                        forcePublish = true;
                        Config.Log("跳转被播放器忽略：自报位置 " + next.Position.TotalSeconds.ToString("0.0") +
                                   " 秒，目标 " + seekTarget.TotalSeconds.ToString("0.0") + " 秒 —— 该播放器不支持 SMTC 位置跳转");
                    }
                }
                changed = Differs(state, next);
                if (changed || forcePublish)
                {
                    // 换歌 -> 旧封面退休，交给 UI 线程回收
                    if (next.Cover != state.Cover && state.Cover != null) retired.Enqueue(state.Cover);
                    next.Version = state.Version + 1;
                    state = next;
                }
            }
            // 无条件发布快照：只在"有变化"时发布的话，稳态下 latest 一直是 null，
            // UI 线程就会退回加锁路径 —— 这正是之前 11% CPU 的来源之一。
            latest = state;

            if (changed && Changed != null)
            {
                try { Changed(this, EventArgs.Empty); } catch { }
            }
        }

        private static bool Differs(NowPlayingState a, NowPlayingState b)
        {
            if (a.AppRunning != b.AppRunning) return true;
            if (a.HasSession != b.HasSession) return true;
            if (a.Playing != b.Playing) return true;
            if (a.Title != b.Title || a.Artist != b.Artist || a.Album != b.Album) return true;
            if (a.Duration != b.Duration) return true;
            if (a.Cover != b.Cover) return true;
            if (a.CanPrev != b.CanPrev || a.CanNext != b.CanNext) return true;
            // 位置偏差超过 1.5 秒也算变化（用户拖了进度条 / 切了段落）
            TimeSpan d = a.Position - b.Position;
            if (d < TimeSpan.Zero) d = d.Negate();
            if (d.TotalSeconds > 1.5) return true;
            return false;
        }

        private static Bitmap FetchCover(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            try
            {
                if (props.Thumbnail == null) return null;
                IRandomAccessStreamWithContentType stream = Wait(props.Thumbnail.OpenReadAsync());
                if (stream == null) return null;

                uint size = (uint)stream.Size;
                if (size == 0 || size > 8 * 1024 * 1024) return null;

                DataReader reader = new DataReader(stream.GetInputStreamAt(0));
                byte[] data;
                try
                {
                    uint loaded = Wait(reader.LoadAsync(size));
                    data = new byte[loaded];
                    reader.ReadBytes(data);
                }
                finally { try { reader.Dispose(); } catch { } }

                if (data.Length < 4) return null;
                using (MemoryStream ms = new MemoryStream(data))
                {
                    // Bitmap 直接从流构造会一直占着流，先拷成独立位图
                    using (Bitmap raw = new Bitmap(ms))
                    {
                        lock (Gdi.Lock)
                        {
                            Bitmap copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                            using (Graphics g = Graphics.FromImage(copy))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(raw, new Rectangle(0, 0, copy.Width, copy.Height));
                            }
                            return copy;
                        }
                    }
                }
            }
            catch (Exception ex) { Config.Log("NowPlaying: 取专辑封面失败: " + ex.Message); return null; }
        }

        // ==================== WinRT await ====================
        //
        // 不能用 WindowsRuntimeSystemExtensions.GetAwaiter ——
        // 机器上 System.Runtime.WindowsRuntime.dll 那份版本太老，只有 IAsyncAction 的重载，
        // 泛型版没有。所以自己用 Completed 事件等。

        private static T Wait<T>(IAsyncOperation<T> op)
        {
            if (op == null) return default(T);
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                op.Completed = delegate(IAsyncOperation<T> o, AsyncStatus s) { done.Set(); };
                if (!done.WaitOne(5000)) return default(T);
            }
            return op.GetResults();
        }

        private static bool WaitBool(IAsyncOperation<bool> op)
        {
            return Wait(op);
        }
    }
}
