// LlamaMonitor — 独立的 llama-server 实时状态窗口
// 单文件 WinForms，编译为独立 exe，不依赖 PowerShell。
//
// 数据来源（全部只读）：
//   GET /health   服务存活
//   GET /props    模型 / build / total_slots / kv_budget_guard
//   GET /slots    槽位任务号、处理状态、prompt token、缓存命中、n_past
//   nvidia-smi    GPU 利用率 / 显存 / 温度 / 功耗
//   llama-server 日志  print_timing 的 n_gen/tg/tg_3s、draft 接受率、prefill 速率、find_slot 警告

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LlamaMonitor
{
    internal static class UiText
    {
        private static bool _english;
        private static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>
        {
            { "WindowTitle", new[] { "llama-server 实时状态", "LlamaMonitor — Live Inference Monitor" } },
            { "AlreadyRunning", new[] { "监控窗口已经在运行了。", "The monitor is already running." } },
            { "StartupFailure", new[] { "监控窗口启动失败：\r\n\r\n{0}\r\n\r\n详情见 {1}", "The monitor could not start:\r\n\r\n{0}\r\n\r\nDetails: {1}" } },
            { "ListNone", new[] { "未发现任何运行中的本地推理服务。", "No local inference services found." } },
            { "ListPorts", new[] { "已探测的常见端口：8080, 11434, 1234, 8000, 30000, 5001, 5000, 1235, 8081", "Common ports checked: 8080, 11434, 1234, 8000, 30000, 5001, 5000, 1235, 8081" } },
            { "ListCustomPort", new[] { "如果服务用的是自定义端口，请用 --port 指定，例如：", "For a custom port, specify --port, for example:" } },
            { "ListFound", new[] { "发现 {0} 个本地推理服务：", "Found {0} local inference service(s):" } },
            { "ListStatus", new[] { "      状态    : ", "      Status      : " } },
            { "ListOnline", new[] { "在线", "Online" } },
            { "ListOffline", new[] { "离线", "Offline" } },
            { "ListModel", new[] { "      模型    : ", "      Model       : " } },
            { "ListCapabilities", new[] { "      可提供  : ", "      Capabilities: " } },
            { "ListVersion", new[] { "      版本    : ", "      Version     : " } },
            { "ListLog", new[] { "      日志    : ", "      Log         : " } },
            { "ListSource", new[] { "      来源    : ", "      Source      : " } },
            { "Connecting", new[] { "● 连接中…", "● Connecting…" } },
            { "Topmost", new[] { "置顶", "Top" } },
            { "Pause", new[] { "暂停", "Pause" } },
            { "Resume", new[] { "继续", "Resume" } },
            { "ViewLog", new[] { "看日志", "View log" } },
            { "WebUI", new[] { "WebUI", "Web UI" } },
            { "LanguageSwitch", new[] { "EN", "中文" } },
            { "SlotSection", new[] { "槽位实时（llama.cpp /slots；其它后端不提供此数据）", "Live slots (llama.cpp /slots; unavailable for other backends)" } },
            { "SlotHeader", new[] { "槽|任务|状态|提示词进度|提示词用量（含缓存命中）|已生成|当前速度|近三秒|上下文占用", "Slot|Task|State|Prompt progress|Prompt tokens (incl. cache)|Generated|Current tok/s|Last 3 s|Context usage" } },
            { "TaskSection", new[] { "各任务时刻（来自服务日志，保留最近 10 分钟）", "Task timings (from server log; last 10 minutes)" } },
            { "TaskHeader", new[] { "槽|已生成数|生成速度|近三秒速度|草稿接受率|预填充进度|最后更新", "Slot|Tokens generated|Generation speed|Last 3 s speed|Draft acceptance|Prefill progress|Last update" } },
            { "LogSection", new[] { "服务日志尾巴（警告统计在底部状态栏）", "Server log tail (warning counts shown in status bar)" } },
            { "Model", new[] { "模型 {0}", "Model {0}" } },
            { "SlotCount", new[] { "  |  槽位 {0} 个", "  |  Slots {0}" } },
            { "Version", new[] { "  |  版本 {0}", "  |  Version {0}" } },
            { "Log", new[] { "  |  日志 {0}", "  |  Log {0}" } },
            { "BackendModelSubtitle", new[] { "后端 {0} | 模型 {1} | 版本 {2} | 日志 {3}", "Backend {0} | Model {1} | Version {2} | Log {3}" } },
            { "RefreshError", new[] { "刷新异常: {0}", "Refresh error: {0}" } },
            { "NoServer", new[] { "● 未发现服务   {0}", "● Server not found   {0}" } },
            { "ServiceUnreachable", new[] { "服务不可达，正在重新搜索…", "Service unreachable; searching again…" } },
            { "ConnectionFailed", new[] { "连接失败，自动重新发现中…", "Connection failed; rediscovering…" } },
            { "ServerFoundAgain", new[] { "已重新找到服务 {0}:{1}", "Server found again at {0}:{1}" } },
            { "ServerOnline", new[] { "● 服务在线   {0}{1}", "● Server online   {0}{1}" } },
            { "BackendOnline", new[] { "● {0} 服务在线   {1}", "● {0} online   {1}" } },
            { "Warnings", new[] { "   警告 {0} 次", "   Warnings {0}" } },
            { "Errors", new[] { "，错误 {0} 次", ", Errors {0}" } },
            { "RefreshStatus", new[] { "[{0}]  刷新 {1} 次   间隔 {2} ms{3}{4}", "[{0}]  Refresh {1}   Interval {2} ms{3}{4}" } },
            { "PausedTag", new[] { "  【已暂停】", "  [Paused]" } },
            { "CachePercent", new[] { " 缓存{0}%", " Cache {0}%" } },
            { "Processing", new[] { "处理中", "Processing" } },
            { "Idle", new[] { "空闲", "Idle" } },
            { "Prefill", new[] { "预填充", "Prefill" } },
            { "Generating", new[] { "生成", "Generating" } },
            { "KvPool", new[] { "键值缓存池 {0} / {1} ({2}%)    安全余量 {3}    默认输出预留 {4}    策略 {5}", "KV cache pool {0} / {1} ({2}%)    Safety reserve {3}    Default output reserve {4}    Policy {5}" } },
            { "NotFound", new[] { "未找到", "Not found" } },
            { "GpuReading", new[] { "nvidia-smi 读取中…", "Reading nvidia-smi…" } },
            { "GpuCallFailed", new[] { "nvidia-smi 调用失败", "nvidia-smi call failed" } },
            { "GpuNoOutput", new[] { "nvidia-smi 无输出", "nvidia-smi returned no output" } },
            { "GpuFormat", new[] { "显卡{0} {1,3}% | {2,4}瓦 | {3,2}℃ | 显存 {4}/{5} 兆", "GPU {0} {1,3}% | {2,4} W | {3,2}°C | VRAM {4}/{5} MB" } },
            { "UnknownBackend", new[] { "未知", "Unknown" } },
            { "MoreModels", new[] { "{0} 等 {1} 个", "{0} and {1} more" } },
            { "CapabilitySlots", new[] { "槽位明细", "Slot details" } },
            { "CapabilityThroughput", new[] { "吞吐", "Throughput" } },
            { "CapabilityModels", new[] { "模型列表", "Model list" } },
            { "CapabilityHealth", new[] { "仅存活探测", "Health check only" } },
            { "DiscoveryCommand", new[] { "命令行指定", "Command-line override" } },
            { "DiscoveryPorts", new[] { "常见端口探测", "Common-port scan" } },
            { "DiscoveryProcess", new[] { "进程 {0} (PID {1})", "Process {0} (PID {1})" } },
            { "DiscoveryProcessCommand", new[] { "由进程 PID {0} 的命令行解析", "Parsed from process PID {0} command line" } },
            { "DiscoveryPort", new[] { "端口探测", "Port scan" } },
            { "NoBackendRetry", new[] { "尚未发现（持续重试中）", "Not found yet (retrying)" } },
            { "CountLabel", new[] { "刷新 {0} 次 | 模型 {1}", "Refresh {0} | Model {1}" } }
        };

        public static bool English { get { return _english; } }

        public static void Configure(string language)
        {
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) _english = true;
            else if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)) _english = false;
            else _english = !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
        }

        public static void Toggle() { _english = !_english; }

        public static string T(string key)
        {
            string[] values;
            if (!Texts.TryGetValue(key, out values)) return key;
            return values[_english ? 1 : 0];
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(key), args);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var cfg = MonitorConfig.Parse(args);
            UiText.Configure(cfg.UiLanguage);
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\LlamaMonitor-singleton", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(UiText.T("AlreadyRunning"), UiText.T("WindowTitle"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // --list：只列出发现到的后端，不开窗（方便脚本调用和排错）
                if (cfg.ListOnly)
                {
                    // 显式包一层 UTF-8 输出流：直接设 Console.OutputEncoding 在
                    // 中文系统上仍会按本地代码页写出 GBK 字节，管道里就成乱码了。
                    System.IO.StreamWriter stdout;
                    try
                    {
                        stdout = new System.IO.StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                        stdout.AutoFlush = true;
                    }
                    catch
                    {
                        stdout = null;
                    }

                    Action<string> w = line =>
                    {
                        if (stdout != null) stdout.WriteLine(line);
                        else Console.WriteLine(line);
                    };

                    var found = cfg.DiscoverBackends(900);
                    if (found.Count == 0)
                    {
                        w(UiText.T("ListNone"));
                        w("");
                        w(UiText.T("ListPorts"));
                        w(UiText.T("ListCustomPort"));
                        w("    LlamaMonitor-cli.exe --list --port 9999");
                    }
                    else
                    {
                        w(UiText.F("ListFound", found.Count));
                        w("");
                        foreach (var b in found)
                        {
                            w("  " + b.KindName + "   " + b.Address
                                + (b.Pid > 0 ? "   PID " + b.Pid : ""));
                            w(UiText.T("ListStatus") + UiText.T(b.Alive ? "ListOnline" : "ListOffline"));
                            w(UiText.T("ListModel") + b.ModelSummary);
                            w(UiText.T("ListCapabilities") + b.Capability);
                            if (!string.IsNullOrEmpty(b.Version))
                                w(UiText.T("ListVersion") + b.Version);
                            string log = !string.IsNullOrEmpty(b.LogFile) ? b.LogFile : ServerDiscovery.ResolveLogFile(null);
                            w(UiText.T("ListLog") + (string.IsNullOrEmpty(log) ? UiText.T("NotFound") : log));
                            w(UiText.T("ListSource") + b.Discovered);
                            w("");
                        }
                    }
                    if (stdout != null) stdout.Flush();
                    return;
                }

                try { DpiFix.SetPerMonitorAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    Application.Run(new MonitorForm(cfg));
                }
                catch (Exception ex)
                {
                    Diag.Write("致命异常: " + ex);
                    MessageBox.Show(UiText.F("StartupFailure", ex.Message, Diag.Path),
                        UiText.T("WindowTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    /// <summary>
    /// 命令行参数。默认全部留空 = 自动发现：找 llama-server 进程并解析其端口、
    /// 日志文件、模型路径。任何一项都可以用参数覆盖。
    /// </summary>
    internal sealed class MonitorConfig
    {
        public string Host = "127.0.0.1";
        public int Port;                 // 0 = 自动发现
        public int UiPort = 0;           // 0 = 用服务端口 +1
        public string LogFile = "";
        public string LogDir = "";
        public int RefreshMs = 1000;
        public int GpuMs = 2000;
        public int LogMs = 1200;
        public bool TopMost = true;
        public bool ListOnly;
        public int ExplicitPid;          // 0 = 不指定
        public string UiLanguage = "";   // empty = follow Windows UI language
        public int RequestedPort;         // preserve the command-line choice after discovery updates Port
        public string RequestedHost = "127.0.0.1";
        public bool HostSpecified;

        public string BaseUrl { get { return "http://" + Host + ":" + Port; } }

        public static MonitorConfig Parse(string[] args)
        {
            var c = new MonitorConfig();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = (i + 1 < args.Length) ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "-port": case "--port": if (next != null) { c.Port = SafeInt(next, 0); c.RequestedPort = c.Port; i++; } break;
                    case "-uiport": case "--uiport": if (next != null) { c.UiPort = SafeInt(next, 0); i++; } break;
                    case "-host": case "--host": if (next != null) { c.Host = next; c.RequestedHost = next; c.HostSpecified = true; i++; } break;
                    case "-logfile": case "--log-file": if (next != null) { c.LogFile = next; i++; } break;
                    case "-logdir": case "--log-dir": if (next != null) { c.LogDir = next; i++; } break;
                    case "-pid": case "--pid": if (next != null) { c.ExplicitPid = SafeInt(next, 0); i++; } break;
                    case "-interval": case "--interval": if (next != null) { c.RefreshMs = Math.Max(200, SafeInt(next, 1000)); i++; } break;
                    case "-lang": case "--lang":
                        if (next != null)
                        {
                            string language = next.ToLowerInvariant();
                            if (language == "en" || language == "zh") c.UiLanguage = language;
                            i++;
                        }
                        break;
                    case "-notopmost": case "--no-topmost": c.TopMost = false; break;
                    case "-list": case "--list": c.ListOnly = true; break;
                }
            }
            return c;
        }

        public List<BackendTarget> DiscoverBackends(int timeoutMs)
        {
            List<BackendTarget> found;
            if (RequestedPort > 0 || HostSpecified)
            {
                var target = new BackendTarget
                {
                    Host = RequestedHost,
                    Port = RequestedPort > 0 ? RequestedPort : 8080,
                    DiscoveryKind = "command"
                };
                found = Backends.Probe(target, timeoutMs)
                    ? new List<BackendTarget> { target } : new List<BackendTarget>();
            }
            else
            {
                found = Backends.DiscoverAll(0, timeoutMs);
            }
            if (ExplicitPid > 0) found = found.Where(b => b.Pid == ExplicitPid).ToList();
            return found;
        }

        private static int SafeInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }
    }

    internal static class DpiFix
    {
        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int value);

        public static void SetPerMonitorAware() { SetProcessDpiAwareness(2); }
    }

    internal static class Diag
    {
        private const long MaxBytes = 1024 * 1024;   // 诊断日志封顶 1 MB

        public static readonly string Path =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "monitor-diag.log");

        private static readonly object Gate = new object();

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    // 服务长期连不上时会持续写日志，超过上限就截断重来，避免无限增长
                    var fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > MaxBytes)
                        File.WriteAllText(Path, "", Encoding.UTF8);

                    File.AppendAllText(Path,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + "\r\n",
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    /// <summary>一个槽位的一行观测数据。</summary>
    internal sealed class SlotRow
    {
        public int Id;
        public long Task = -1;
        public bool Processing;
        public long PromptTotal;
        public long PromptProcessed;
        public long PromptCache;
        public long NDecoded;
        public long NPast;
        public long NCtx;
        public double TpsInstant;
        public double TpsWindow;
        public string ProgressText = "—";
        public string PromptText = "—";
        public string ContextText = "—";

        public string Key
        {
            get
            {
                return string.Join("|", Id, Task, Processing, PromptTotal, PromptProcessed, PromptCache,
                    NDecoded, NPast, NCtx,
                    TpsInstant.ToString("F2"), TpsWindow.ToString("F2"),
                    ProgressText, PromptText, ContextText);
            }
        }
    }

    /// <summary>某个 task 从日志里提取的时刻数据。</summary>
    internal sealed class TaskRow
    {
        public int Slot;
        public long Task;
        public long NGen;
        public double? Tg;
        public double? Tg3;
        public double? PrefillProgress;
        public bool IsPrefill;
        public string Acceptance = "—";
        public string MeanLen = "—";
        public DateTime When = DateTime.Now;
        public DateTime Seen = DateTime.Now;
    }

    /// <summary>
    /// 抗闪烁表格：在控件内部直接开双缓冲（反射设 DataGridView.DoubleBuffered 并不可靠），
    /// 并且不再单独绘制背景，消除刷值时的白底一闪。
    /// </summary>
    internal sealed class BufferedGrid : DataGridView
    {
        public BufferedGrid()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 背景由 OnPaint 一次性画完，避免先擦后画
        }
    }

    /// <summary>抗闪烁面板/布局容器。</summary>
    internal sealed class BufferedPanel : TableLayoutPanel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    internal sealed class MonitorForm : Form
    {
        private static readonly Color CBg = Color.FromArgb(18, 20, 23);
        private static readonly Color CPanel = Color.FromArgb(28, 31, 36);
        private static readonly Color CGrid = Color.FromArgb(40, 44, 50);
        private static readonly Color CFg = Color.FromArgb(226, 230, 236);
        private static readonly Color CDim = Color.FromArgb(138, 146, 156);
        private static readonly Color CAccent = Color.FromArgb(90, 180, 255);
        private static readonly Color COk = Color.FromArgb(110, 210, 130);
        private static readonly Color CWarn = Color.FromArgb(240, 190, 90);
        private static readonly Color CBad = Color.FromArgb(240, 105, 105);
        private static readonly Color CHead = Color.FromArgb(40, 45, 52);
        private static readonly Color CAlt = Color.FromArgb(23, 26, 30);

        private static readonly Font FMono = new Font("Consolas", 9.5f);
        private static readonly Font FMonoB = new Font("Consolas", 10.5f, FontStyle.Bold);
        private static readonly Font FMonoS = new Font("Consolas", 8.25f);
        private static readonly Font FMonoG = new Font("Consolas", 8.25f);   // 表格用，窄屏也能放下全部列

        private readonly MonitorConfig _cfg;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private Label _lblTitle, _lblSub, _lblGpu, _lblKv, _lblStatus;
        private Label _lblSlotHdr, _lblTimeHdr, _lblTailHdr;
        private CheckBox _chkTop;
        private Button _btnPause, _btnLog, _btnWeb, _btnLanguage;
        private DataGridView _gridSlot, _gridTime;
        private TextBox _txtTail;
        private BufferedPanel _root;
        private Dictionary<int, float> _rowDefaults = new Dictionary<int, float>();
        private bool _hasKvGuard;

        private System.Windows.Forms.Timer _timer, _gpuTimer, _logTimer;
        private NoEraseWindow _noEraseSlot, _noEraseTime;

        private bool _paused;
        private int _refreshed;
        private List<SlotRow> _lastSlots;
        private string _gpuText = UiText.T("GpuReading");

        // 自动发现到的服务端；为空表示还没找到
        private ServerTarget _target;
        private List<BackendTarget> _backends = new List<BackendTarget>();
        private List<BackendTarget> _backendRows = new List<BackendTarget>();
        private BackendTarget _picked;
        private bool _showSlots = true;
        private string _logPathLabel = "";
        private DateTime _lastDiscoverAt = DateTime.MinValue;
        private bool _useExplicitLog;

        private readonly Dictionary<int, SlotState> _slotState = new Dictionary<int, SlotState>();
        private readonly List<TaskRow> _tasks = new List<TaskRow>();
        private readonly List<string> _tail = new List<string>();
        private int _warnCount, _errCount;
        private long _logPos;
        private string _logPath;
        private DateTime _lastTailAt = DateTime.MinValue;
        private long _totalLogLines;
        private long _shownLines;
        private readonly StringBuilder _logBuf = new StringBuilder();

        private sealed class SlotState
        {
            public DateTime T = DateTime.Now;
            public long N;
            public readonly Queue<Sample> Hist = new Queue<Sample>();
        }

        private struct Sample { public DateTime T; public long N; }

        /// <summary>
        /// 确定要连哪个服务端。优先级：
        ///   1. 命令行显式给的 --port / --host
        ///   2. 自动发现（读 llama-server 进程命令行 → 端口 → HTTP 探活）
        ///   3. 常见端口逐个探测
        /// 每次连不上时也会重新走一遍，这样服务重启换了端口也能自己跟上。
        /// </summary>
        private void DiscoverTarget(bool firstRun)
        {
            _useExplicitLog = !string.IsNullOrEmpty(_cfg.LogFile);

            var all = _cfg.DiscoverBackends(900);
            _backends = all;
            _lastDiscoverAt = DateTime.Now;

            if (all.Count == 0)
            {
                if (_target != null) _target.Alive = false;
                if (firstRun)
                {
                    _target = new ServerTarget
                    {
                        Host = _cfg.Host,
                        Port = _cfg.Port > 0 ? _cfg.Port : 8080,
                        Discovered = UiText.T("NoBackendRetry")
                    };
                    AppDiag("首次启动未发现任何本地推理后端，进入重试模式");
                }
                _backendRows = new List<BackendTarget>();
                return;
            }

            AppDiag("发现 " + all.Count + " 个后端: " +
                string.Join(", ", all.Select(b => b.KindName + "@" + b.Address).ToArray()));

            _backendRows = all;

            // 选一个"主力"：优先支持槽位明细的（llama.cpp），其次第一个
            var pick = all.FirstOrDefault(b => b.SupportsSlots) ?? all[0];
            _picked = pick;

            _target = new ServerTarget
            {
                Host = pick.Host,
                Port = pick.Port,
                Pid = pick.Pid,
                Executable = pick.Executable,
                ModelPath = pick.Models.Count > 0 ? pick.Models[0] : "",
                LogFile = pick.LogFile,
                FromProcess = pick.Pid > 0,
                Alive = pick.Alive,
                BuildInfo = pick.Version,
                Discovered = pick.KindName + " · " + pick.Discovered
            };

            _cfg.Host = pick.Host;
            _cfg.Port = pick.Port;

            if (_logPathLabel != pick.LogFile) _logPathLabel = pick.LogFile;

            if (_useExplicitLog)
            {
                _logPath = _cfg.LogFile;
            }
            else
            {
                string target = !string.IsNullOrEmpty(pick.LogFile) ? pick.LogFile
                    : ServerDiscovery.ResolveLogFile(_target);
                if (!string.IsNullOrEmpty(target) &&
                    !string.Equals(target, _logPath, StringComparison.OrdinalIgnoreCase))
                    SwitchLogFile(target);
            }

            // 非 llama.cpp 后端没有槽位明细，界面要换提示
            _showSlots = pick.SupportsSlots;
        }

        private static void AppDiag(string text)
        {
            try { Diag.Write("[发现] " + text); } catch { }
        }

        /// <summary>当前该去哪个目录找日志：显式配置优先，否则用正在读的文件所在目录。</summary>
        private string CurrentLogDir()
        {
            if (!string.IsNullOrEmpty(_cfg.LogDir)) return _cfg.LogDir;
            if (!string.IsNullOrEmpty(_logPath))
            {
                try { return Path.GetDirectoryName(_logPath); } catch { }
            }
            if (_target != null && !string.IsNullOrEmpty(_target.Executable))
            {
                try { return Path.GetDirectoryName(_target.Executable); } catch { }
            }
            return "";
        }

        public MonitorForm(MonitorConfig cfg)
        {
            _cfg = cfg;

            // 先把连接参数定下来：显式参数优先，否则自动发现
            DiscoverTarget(firstRun: true);

            Text = UiText.T("WindowTitle");
            BackColor = CBg;
            ForeColor = CFg;
            Font = FMono;
            TopMost = cfg.TopMost;
            KeyPreview = true;

            try
            {
                var pi = typeof(Form).GetProperty("DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(this, true, null);
            }
            catch { }

            var wa = Screen.PrimaryScreen.WorkingArea;

            // 默认给一个紧凑、够用的窗口；列按权重铺满宽度，缩放窗口不会截断内容
            int w = Math.Min(1600, Math.Max(1180, wa.Width - 160));
            int h = Math.Min(950, Math.Max(720, wa.Height - 160));
            ClientSize = new Size(w, h);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(wa.Left + 40, wa.Top + 30);
            MinimumSize = new Size(1180, 720);

            BuildUi();

            _timer = new System.Windows.Forms.Timer { Interval = cfg.RefreshMs };
            _timer.Tick += (s, e) => SafeRefresh(false);
            _timer.Start();

            _gpuTimer = new System.Windows.Forms.Timer { Interval = cfg.GpuMs };
            _gpuTimer.Tick += (s, e) => { if (!_paused) UpdateGpu(); };
            _gpuTimer.Start();

            _logTimer = new System.Windows.Forms.Timer { Interval = cfg.LogMs };
            _logTimer.Tick += (s, e) => { try { ReadLogIncremental(); } catch (Exception ex) { Diag.Write("日志解析异常: " + ex); } };
            _logTimer.Start();

            // 表格：开双缓冲 + 禁掉背景擦除（在窗体句柄建立后挂接）
            Load += (s, e) =>
            {
                try { _noEraseSlot = new NoEraseWindow(_gridSlot); } catch { }
                try { _noEraseTime = new NoEraseWindow(_gridTime); } catch { }
            };

            UpdateGpu();
            SafeRefresh(false);

            FormClosing += (s, e) =>
            {
                _timer.Stop(); _gpuTimer.Stop(); _logTimer.Stop();
            };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        }

        // ───────────────────────── UI ─────────────────────────

        /// <summary>把物理工作区换算成窗体自身的逻辑像素坐标（进程已声明 DPI 感知）。</summary>
        private static Size LogicalScreenSize(Rectangle physical)
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float sx = g.DpiX / 96f;
                    float sy = g.DpiY / 96f;
                    if (sx <= 0) sx = 1f;
                    if (sy <= 0) sy = 1f;
                    return new Size((int)(physical.Width / sx), (int)(physical.Height / sy));
                }
            }
            catch { return physical.Size; }
        }

        private static Label MakeLabel(string text, Font font, Color color)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = color,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 2, 4, 2)
            };
        }

        /// <summary>
        /// 按表头实际渲染宽度分配列宽（字体已含 DPI 缩放），
        /// 保证中文表头不被截断，也不留无谓空隙。
        /// </summary>
        private static int[] MeasureHeaderWidths(string[] headers, int minWidth)
        {
            var result = new int[headers.Length];
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    Size s = TextRenderer.MeasureText(g, headers[i], FMonoG,
                        new Size(int.MaxValue, 28), TextFormatFlags.NoPadding);
                    // 表头宽 + 单元格内边距与排序箭头余量
                    int w = s.Width + 12;
                    if (w < minWidth) w = minWidth;
                    result[i] = w;
                }
            }
            return result;
        }

        /// <summary>给控件打开双缓冲，消掉每秒刷新时的背景闪烁。</summary>
        private static void EnableDoubleBuffer(Control c)
        {
            if (c == null) return;
            try
            {
                var pi = c.GetType().GetProperty("DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(c, true, null);
            }
            catch { }
        }

        /// <summary>
        /// 屏蔽 WM_ERASEBKGND：表格每次刷值时不再先擦背景，
        /// 这是 WinForms 表格"闪一下"的主因。
        /// </summary>
        private sealed class NoEraseWindow : NativeWindow
        {
            private const int WM_ERASEBKGND = 0x0014;

            public NoEraseWindow(Control c)
            {
                EnableDoubleBuffer(c);
                AssignHandle(c.Handle);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_ERASEBKGND)
                {
                    m.Result = (IntPtr)1;   // 已处理，不要再擦背景
                    return;
                }
                base.WndProc(ref m);
            }
        }

        private DataGridView MakeGrid(string[] headers, int[] weights)
        {
            var g = new BufferedGrid
            {
                Font = FMonoG,
                BackgroundColor = CBg,
                GridColor = CGrid,
                BorderStyle = BorderStyle.None,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28,
                AutoSize = false,   // 必须关掉：开了会让整张表按内容撑高，把布局挤坏
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowTemplate = { Height = 26 }
            };
            g.ColumnHeadersDefaultCellStyle.BackColor = CHead;
            g.ColumnHeadersDefaultCellStyle.ForeColor = CFg;
            g.ColumnHeadersDefaultCellStyle.Font = FMonoG;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = CHead;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = CFg;
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            g.DefaultCellStyle.BackColor = CBg;
            g.DefaultCellStyle.ForeColor = CFg;
            g.DefaultCellStyle.SelectionBackColor = CGrid;
            g.DefaultCellStyle.SelectionForeColor = CFg;
            g.AlternatingRowsDefaultCellStyle.BackColor = CAlt;

            // 颜色只在格式化阶段决定，避免每秒改动单元格样式触发重绘
            g.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var row = g.Rows[e.RowIndex];
                if (row.Tag is Color) e.CellStyle.ForeColor = (Color)row.Tag;
                if (row.Tag is SlotRow)
                {
                    var sr = (SlotRow)row.Tag;
                    switch (e.ColumnIndex)
                    {
                        case 2: e.CellStyle.ForeColor = sr.Processing ? CAccent : CDim; break;
                        case 6: e.CellStyle.ForeColor = sr.NDecoded > 0 ? TpsColor(sr.TpsInstant) : CDim; break;
                        case 7: e.CellStyle.ForeColor = sr.NDecoded > 0 ? TpsColor(sr.TpsWindow) : CDim; break;
                        default: e.CellStyle.ForeColor = sr.Processing ? CFg : CDim; break;
                    }
                }
            };
            EnableDoubleBuffer(g);

            // 表头实测宽度作为列宽下限，权重决定多余空间怎么分：            // 窗口够宽时按内容比例铺满，窗口偏窄时先保证中文表头完整。
            int[] measured = MeasureHeaderWidths(headers, 40);

            for (int i = 0; i < headers.Length; i++)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = headers[i],
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    FillWeight = weights[i],
                    MinimumWidth = measured[i]
                };
                g.Columns.Add(col);
            }
            return g;
        }

        private void BuildUi()
        {
            // 单一根布局表：所有区块都是它的行，避免 Dock=Fill 与 Dock=Bottom 互相挤压
            var root = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 11,
                BackColor = CBg,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));   // 0  顶栏
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // 1  槽位标题
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));   // 2  槽位表
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // 3  任务标题
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 46f));   // 4  任务表（自适应）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // 5  KV 行
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));   // 6  日志标题
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 54f));   // 7  日志内容（自适应）
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // 8  状态栏
            _root = root;
            _rowDefaults = new Dictionary<int, float>
            {
                { 1, 22f }, { 2, 84f }, { 3, 22f }, { 5, 22f }, { 6, 20f }, { 8, 24f }
            };
            Controls.Add(root);
            EnableDoubleBuffer(root);

            // ── 0 顶栏 ──
            var top = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                BackColor = CPanel,
                Margin = new Padding(0),
                Padding = new Padding(10, 4, 6, 4)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 356f));
            for (int i = 0; i < 3; i++) top.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 28 : 22));
            root.Controls.Add(top, 0, 0);

            _lblTitle = MakeLabel(UiText.T("Connecting"), FMonoB, CAccent);
            _lblSub = MakeLabel("", FMonoS, CDim);
            _lblGpu = MakeLabel("", FMono, CFg);
            top.Controls.Add(_lblTitle, 0, 0);
            top.Controls.Add(_lblSub, 0, 1);
            top.Controls.Add(_lblGpu, 0, 2);

            var btnHost = new Panel { Dock = DockStyle.Fill, BackColor = CPanel, Margin = new Padding(0) };
            top.Controls.Add(btnHost, 1, 0);
            top.SetRowSpan(btnHost, 3);

            _chkTop = new CheckBox
            {
                Text = UiText.T("Topmost"),
                Font = FMonoS,
                ForeColor = CFg,
                BackColor = Color.Transparent,
                Checked = _cfg.TopMost,
                Location = new Point(0, 8),
                Size = new Size(56, 24)
            };
            _chkTop.CheckedChanged += (s, e) => TopMost = _chkTop.Checked;
            btnHost.Controls.Add(_chkTop);

            _btnPause = MakeButton(UiText.T("Pause"), 62, btnHost);
            _btnLog = MakeButton(UiText.T("ViewLog"), 146, btnHost);
            _btnWeb = MakeButton(UiText.T("WebUI"), 230, btnHost);
            _btnLanguage = MakeButton(UiText.T("LanguageSwitch"), 312, btnHost, 42);
            _btnLanguage.Click += (s, e) =>
            {
                UiText.Toggle();
                ApplyLocalizedUi();
            };

            _btnPause.Click += (s, e) =>
            {
                _paused = !_paused;
                _btnPause.Text = UiText.T(_paused ? "Resume" : "Pause");
            };
            _btnLog.Click += (s, e) =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
                    {
                        Process.Start("notepad.exe", "\"" + _logPath + "\"");
                        return;
                    }
                    // 没有日志文件就退回到最可能相关的目录
                    string dir = !string.IsNullOrEmpty(_cfg.LogDir) ? _cfg.LogDir
                        : (_target != null && !string.IsNullOrEmpty(_target.Executable)
                            ? Path.GetDirectoryName(_target.Executable) : "");
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        Process.Start("explorer.exe", dir);
                }
                catch (Exception ex) { Diag.Write("打开日志失败: " + ex.Message); }
            };
            _btnWeb.Click += (s, e) =>
            {
                try
                {
                    int port = _cfg.UiPort > 0 ? _cfg.UiPort : (_cfg.Port + 1);
                    Process.Start("http://" + _cfg.Host + ":" + port);
                }
                catch (Exception ex) { Diag.Write("打开 WebUI 失败: " + ex.Message); }
            };

            // ── 1/2 槽位表（仅 llama.cpp 这类支持 /slots 的后端才有数据）──
            _lblSlotHdr = MakeLabel(UiText.T("SlotSection"), FMonoS, CAccent);
            root.Controls.Add(_lblSlotHdr, 0, 1);
            _gridSlot = MakeGrid(
                UiText.T("SlotHeader").Split(new[] { '|' }),
                new[] { 30, 46, 58, 140, 168, 58, 72, 70, 172 });
            root.Controls.Add(_gridSlot, 0, 2);

            // ── 3/4 任务时刻表 ──
            _lblTimeHdr = MakeLabel(UiText.T("TaskSection"), FMonoS, CAccent);
            root.Controls.Add(_lblTimeHdr, 0, 3);
            _gridTime = MakeGrid(
                UiText.T("TaskHeader").Split(new[] { '|' }),
                new[] { 30, 74, 84, 80, 168, 80, 150 });
            root.Controls.Add(_gridTime, 0, 4);

            // ── 5 KV 行 ──
            _lblKv = MakeLabel("", FMono, CFg);
            root.Controls.Add(_lblKv, 0, 5);

            // ── 6/7 日志区 ──
            _lblTailHdr = MakeLabel(UiText.T("LogSection"), FMonoS, CDim);
            root.Controls.Add(_lblTailHdr, 0, 6);
            _txtTail = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = FMonoS,
                BackColor = CPanel,
                ForeColor = CFg,
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = false,
                Dock = DockStyle.Fill
            };
            root.Controls.Add(_txtTail, 0, 7);

            // ── 8 状态栏 ──
            _lblStatus = MakeLabel("", FMonoS, CDim);
            _lblStatus.BackColor = CPanel;
            _lblStatus.Padding = new Padding(8, 0, 8, 0);
            root.Controls.Add(_lblStatus, 0, 8);

            ApplyCapabilities();
        }

        /// <summary>
        /// 按当前后端能力决定哪些区块可见。
        /// 只有 llama.cpp 才有槽位明细和任务时刻表，其它后端整块收起来，
        /// 但保留 KV 行（若有）、日志区和状态栏。
        /// </summary>
        private void ApplyCapabilities()
        {
            bool slots = _showSlots;
            SetRowVisible(1, slots);        // 槽位标题
            SetRowVisible(2, slots);        // 槽位表
            SetRowVisible(3, slots);        // 任务标题
            SetRowVisible(4, slots);        // 任务表
            SetRowVisible(5, _hasKvGuard);  // KV 行
            SetRowVisible(6, true);         // 日志标题
            SetRowVisible(7, true);         // 日志内容
            SetRowVisible(8, true);         // 状态栏
        }

        /// <summary>整行隐藏：行高归零 + 行内控件不可见（TableLayoutPanel 没有原生行隐藏）。</summary>
        private void SetRowVisible(int row, bool visible)
        {
            if (_root == null || row >= _root.RowStyles.Count) return;

            if (_root.RowStyles[row].SizeType == SizeType.Percent)
            {
                _root.RowStyles[row].Height = visible ? (row == 4 ? 46f : 54f) : 0f;
                foreach (Control c in _root.Controls)
                    if (_root.GetRow(c) == row)
                        c.Visible = visible;
                return;
            }

            float def;
            if (!_rowDefaults.TryGetValue(row, out def)) def = 22f;
            _root.RowStyles[row].Height = visible ? def : 0f;

            foreach (Control c in _root.Controls)
                if (_root.GetRow(c) == row)
                    c.Visible = visible;
        }

        private static Button MakeButton(string text, int x, Control parent, int width = 80)
        {
            var b = new Button
            {
                Text = text,
                Font = FMonoS,
                Location = new Point(x, 4),
                Size = new Size(width, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = CBg,
                ForeColor = CFg
            };
            b.FlatAppearance.BorderColor = CGrid;
            parent.Controls.Add(b);
            return b;
        }

        private void ApplyLocalizedUi()
        {
            Text = UiText.T("WindowTitle");
            _chkTop.Text = UiText.T("Topmost");
            _btnPause.Text = UiText.T(_paused ? "Resume" : "Pause");
            _btnLog.Text = UiText.T("ViewLog");
            _btnWeb.Text = UiText.T("WebUI");
            _btnLanguage.Text = UiText.T("LanguageSwitch");
            _lblSlotHdr.Text = UiText.T("SlotSection");
            _lblTimeHdr.Text = UiText.T("TaskSection");
            _lblTailHdr.Text = UiText.T("LogSection");
            UpdateGridHeaders(_gridSlot, UiText.T("SlotHeader").Split(new[] { '|' }));
            UpdateGridHeaders(_gridTime, UiText.T("TaskHeader").Split(new[] { '|' }));
            _lastSlots = null;
            UpdateGpu();
            UpdateSubtitle();
            SafeRefresh(true);
        }

        private static void UpdateGridHeaders(DataGridView grid, string[] headers)
        {
            int[] measured = MeasureHeaderWidths(headers, 40);
            for (int i = 0; i < headers.Length && i < grid.Columns.Count; i++)
            {
                grid.Columns[i].HeaderText = headers[i];
                grid.Columns[i].MinimumWidth = measured[i];
            }
            grid.PerformLayout();
            grid.Invalidate();
        }

        // ───────────────────────── 数据抓取 ─────────────────────────

        private static string GetJson(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 4000;
                req.ReadWriteTimeout = 4000;
                req.Method = "GET";
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private Dictionary<string, object> GetObject(string path)
        {
            string raw = GetJson(_cfg.BaseUrl + path);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var o = _json.DeserializeObject(raw);
                return o as Dictionary<string, object>;
            }
            catch { return null; }
        }

        private static long AsLong(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
                catch { return 0; }
            }
            return 0;
        }

        private static string AsString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        private bool _propsMissing = true;
        private string _modelName = "";
        private string _buildInfo = "";
        private string _totalSlots = "";
        private string _lastSubtitle = "";

        /// <summary>
        /// 副标题：模型 / 槽位 / 版本 / 正在读的日志。
        /// 每次都重新拼再比较，这样日志文件晚一步确定时也能补上文件名。
        /// </summary>
        private void UpdateSubtitle()
        {
            string model;
            if (!_showSlots && _picked != null)
                model = _picked.ModelSummary;
            else
            {
                model = !string.IsNullOrEmpty(_modelName) ? _modelName
                    : (_target != null && !string.IsNullOrEmpty(_target.ModelPath) ? _target.ModelPath : "?");
                try { model = Path.GetFileNameWithoutExtension(model); } catch { }
            }

            // 名字太长会顶掉后面的信息，中间截断保留头尾
            if (model != null && model.Length > 44)
                model = model.Substring(0, 26) + "…" + model.Substring(model.Length - 14);

            string text;
            if (!_showSlots && _picked != null)
            {
                text = UiText.F("BackendModelSubtitle", _picked.KindName,
                    string.IsNullOrEmpty(model) ? "?" : model,
                    _picked.Version, LogDisplayName());
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append(UiText.F("Model", string.IsNullOrEmpty(model) ? "?" : model));
                if (!string.IsNullOrEmpty(_totalSlots)) sb.Append(UiText.F("SlotCount", _totalSlots));
                if (!string.IsNullOrEmpty(_buildInfo)) sb.Append(UiText.F("Version", _buildInfo));
                sb.Append(UiText.F("Log", LogDisplayName()));
                text = sb.ToString();
            }
            if (text != _lastSubtitle)
            {
                _lastSubtitle = text;
                SetText(_lblSub, text);
            }
        }

        private void SafeRefresh(bool force)
        {
            if (_paused && !force) return;
            try { RefreshAll(); }
            catch (Exception ex)
            {
                Diag.Write("刷新异常: " + ex);
                SetText(_lblStatus, UiText.F("RefreshError", ex.Message));
            }
        }

        private void RefreshAll()
        {
            DateTime now = DateTime.Now;

            var health = _showSlots ? GetObject("/health") : null;
            string slotsRaw = _showSlots ? GetJson(_cfg.BaseUrl + "/slots") : null;
            bool reachable = _showSlots
                ? !string.IsNullOrEmpty(slotsRaw)
                : Backends.IsAlive(_picked, 900);

            if (!reachable)
            {
                SetText(_lblTitle, UiText.F("NoServer", _cfg.BaseUrl));
                SetColor(_lblTitle, CBad);
                SetText(_lblGpu, UiText.T("ServiceUnreachable"));
                SetText(_lblStatus, "[" + now.ToString("HH:mm:ss") + "] " + UiText.T("ConnectionFailed"));

                // 服务可能换了端口或刚重启：定期重跑发现流程
                if ((now - _lastDiscoverAt).TotalSeconds >= 10)
                {
                    bool hadSlots = _showSlots;
                    DiscoverTarget(false);
                    if (hadSlots != _showSlots) ApplyCapabilities();
                    if (_target != null && _target.Alive)
                        SetText(_lblStatus, "[" + now.ToString("HH:mm:ss") + "] " + UiText.F("ServerFoundAgain", _target.Host, _target.Port));
                }
                return;
            }

            if (!_showSlots)
            {
                SetText(_lblTitle, UiText.F("BackendOnline", _picked.KindName, _cfg.BaseUrl));
                SetColor(_lblTitle, COk);
                UpdateSubtitle();
                SetText(_lblGpu, _gpuText);
                RenderTail(now);
                _refreshed++;
                UpdateRefreshStatus(now);
                return;
            }

            bool online = health != null && AsString(health, "status") == "ok";
            string where = _picked != null && !string.IsNullOrEmpty(_picked.Discovered)
                ? "   [" + _picked.KindName + " · " + _picked.Discovered + "]" : "";
            SetText(_lblTitle, UiText.F("ServerOnline", _cfg.BaseUrl, where));
            SetColor(_lblTitle, online ? COk : CWarn);

            // /props 只在第一次拉取（内容不变）
            if (_propsMissing)
            {
                var props = GetObject("/props");
                if (props != null)
                {
                    _propsMissing = false;
                    string modelPath = AsString(props, "model_path");
                    if (!string.IsNullOrEmpty(modelPath))
                    {
                        try { _modelName = Path.GetFileNameWithoutExtension(modelPath); }
                        catch { _modelName = modelPath; }
                    }
                    _buildInfo = AsString(props, "build_info");
                    _totalSlots = AsString(props, "total_slots");
                    object kv;
                    if (props.TryGetValue("kv_budget_guard", out kv) && kv is Dictionary<string, object>)
                        _kvGuard = (Dictionary<string, object>)kv;
                }
            }

            UpdateSubtitle();
            SetText(_lblGpu, _gpuText);

            // ── 槽位表 ──
            var rows = new List<SlotRow>();
            long ctxSum = 0;
            var slotArray = _json.DeserializeObject(slotsRaw) as object[];
            if (slotArray != null)
            {
                foreach (var item in slotArray)
                {
                    var s = item as Dictionary<string, object>;
                    if (s == null) continue;
                    var r = BuildSlotRow(s, now);
                    ctxSum += r.NPast;
                    rows.Add(r);
                }
            }

            if (!RowsEqual(_lastSlots, rows))
            {
                RenderSlots(rows);
                _lastSlots = rows;
            }

            // ── 任务表 ──
            RenderTasks(now);

            // ── KV ──
            RenderKv(ctxSum);

            // ── 日志尾巴 ──
            RenderTail(now);

            _refreshed++;
            UpdateRefreshStatus(now);
        }

        private void UpdateRefreshStatus(DateTime now)
        {
            string warn = _warnCount > 0
                ? UiText.F("Warnings", _warnCount) + (_errCount > 0 ? UiText.F("Errors", _errCount) : "")
                : "";
            SetText(_lblStatus, UiText.F("RefreshStatus", now.ToString("HH:mm:ss"), _refreshed,
                _cfg.RefreshMs, warn, _paused ? UiText.T("PausedTag") : ""));
        }

        private Dictionary<string, object> _kvGuard;

        private SlotRow BuildSlotRow(Dictionary<string, object> s, DateTime now)
        {
            var r = new SlotRow();
            r.Id = (int)AsLong(s, "id");
            r.Task = AsLong(s, "id_task");
            object p;
            s.TryGetValue("is_processing", out p);
            r.Processing = p is bool && (bool)p;
            r.PromptTotal = AsLong(s, "n_prompt_tokens");
            r.PromptProcessed = AsLong(s, "n_prompt_tokens_processed");
            r.PromptCache = AsLong(s, "n_prompt_tokens_cache");
            r.NDecoded = AsLong(s, "n_decoded");
            r.NCtx = AsLong(s, "n_ctx");

            // /slots 并不提供 n_past：实际占用 = 提示词总长 + 已生成长度。
            // （n_prompt_tokens_processed 只是本轮新算的部分，生成阶段会归零，不能用来算占用）
            r.NPast = r.PromptTotal + r.NDecoded;

            SlotState st;
            if (!_slotState.TryGetValue(r.Id, out st))
            {
                st = new SlotState { T = now, N = r.NDecoded };
                _slotState[r.Id] = st;
            }

            st.Hist.Enqueue(new Sample { T = now, N = r.NDecoded });
            while (st.Hist.Count > 0 && (now - st.Hist.Peek().T).TotalSeconds > 3.2) st.Hist.Dequeue();

            double dt = (now - st.T).TotalSeconds;
            if (dt > 0.35) r.TpsInstant = Math.Max(0, (r.NDecoded - st.N) / dt);

            if (st.Hist.Count > 0)
            {
                var oldest = st.Hist.Peek();
                double dt3 = (now - oldest.T).TotalSeconds;
                if (dt3 > 0.35) r.TpsWindow = Math.Max(0, (r.NDecoded - oldest.N) / dt3);
            }
            st.T = now; st.N = r.NDecoded;

            long done = r.PromptProcessed + r.PromptCache;
            if (r.PromptTotal > 0)
            {
                double pct = Math.Min(100.0, 100.0 * done / r.PromptTotal);
                int bars = (int)Math.Round(pct / 5.0);
                if (bars < 0) bars = 0;
                if (bars > 20) bars = 20;
                var sb = new StringBuilder();
                sb.Append(pct.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5));
                sb.Append("% [");
                sb.Append(new string('#', bars));
                sb.Append(new string('.', 20 - bars));
                sb.Append(']');
                r.ProgressText = sb.ToString();
                double cachePct = 100.0 * r.PromptCache / r.PromptTotal;
                r.PromptText = done.ToString("N0", CultureInfo.InvariantCulture) + "/"
                    + r.PromptTotal.ToString("N0", CultureInfo.InvariantCulture)
                    + UiText.F("CachePercent", cachePct.ToString("0.0", CultureInfo.InvariantCulture));
            }

            if (r.NCtx > 0)
            {
                double pct = 100.0 * r.NPast / r.NCtx;
                r.ContextText = r.NPast.ToString("N0", CultureInfo.InvariantCulture) + "/"
                    + r.NCtx.ToString("N0", CultureInfo.InvariantCulture)
                    + " (" + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%)";
            }            return r;
        }

        private static bool RowsEqual(List<SlotRow> a, List<SlotRow> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Key != b[i].Key) return false;
            return true;
        }

        private void RenderSlots(List<SlotRow> rows)
        {
            _gridSlot.SuspendLayout();
            while (_gridSlot.Rows.Count < rows.Count) _gridSlot.Rows.Add();
            while (_gridSlot.Rows.Count > rows.Count) _gridSlot.Rows.RemoveAt(_gridSlot.Rows.Count - 1);

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var row = _gridSlot.Rows[i];
                row.Tag = r;   // 颜色交给 CellFormatting 统一决定

                DirectSet(row, 0, r.Id.ToString(CultureInfo.InvariantCulture));
                DirectSet(row, 1, r.Task > 0 ? r.Task.ToString(CultureInfo.InvariantCulture) : "—");
                DirectSet(row, 2, UiText.T(r.Processing ? "Processing" : "Idle"));
                DirectSet(row, 3, r.ProgressText);
                DirectSet(row, 4, r.PromptText);
                DirectSet(row, 5, r.NDecoded > 0 ? r.NDecoded.ToString("N0", CultureInfo.InvariantCulture) : "—");
                DirectSet(row, 6, r.NDecoded > 0 ? Fmt(r.TpsInstant) : "—");
                DirectSet(row, 7, r.NDecoded > 0 ? Fmt(r.TpsWindow) : "—");
                DirectSet(row, 8, r.ContextText);
            }
            _gridSlot.ResumeLayout();
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture) + " t/s";
        }

        private static Color TpsColor(double v)
        {
            if (v < 1) return CBad;
            if (v < 5) return CWarn;
            return COk;
        }

        /// <summary>只写值，不碰样式——样式由 CellFormatting 统一处理。</summary>
        private static void DirectSet(DataGridViewRow row, int idx, string text)
        {
            if (idx >= row.Cells.Count) return;
            var cell = row.Cells[idx];
            if (!Equals(cell.Value, text)) cell.Value = text;
        }

        private void RenderTasks(DateTime now)
        {
            DateTime cut = now.AddMinutes(-10);
            // 只保留有实际吞吐数据的任务行，避免被 n_gen=0 的空壳任务刷屏
            List<TaskRow> live = _tasks
                .Where(t => t.Seen > cut && (t.NGen > 0 || t.Tg.HasValue || t.Tg3.HasValue))
                .OrderByDescending(t => t.Seen)
                .Take(10)
                .ToList();

            _gridTime.SuspendLayout();
            while (_gridTime.Rows.Count < live.Count) _gridTime.Rows.Add();
            while (_gridTime.Rows.Count > live.Count) _gridTime.Rows.RemoveAt(_gridTime.Rows.Count - 1);

            for (int i = 0; i < live.Count; i++)
            {
                var t = live[i];
                var row = _gridTime.Rows[i];
                row.Tag = CFg;   // 颜色交给 CellFormatting
                DirectSet(row, 0, t.Slot.ToString(CultureInfo.InvariantCulture));
                DirectSet(row, 1, t.NGen.ToString("N0", CultureInfo.InvariantCulture));
                DirectSet(row, 2, t.Tg.HasValue ? Fmt(t.Tg.Value) : "—");
                DirectSet(row, 3, t.Tg3.HasValue ? Fmt(t.Tg3.Value) : "—");
                DirectSet(row, 4, t.Acceptance);
                DirectSet(row, 5, t.PrefillProgress.HasValue && t.IsPrefill
                    ? t.PrefillProgress.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—");
                DirectSet(row, 6, t.When.ToString("HH:mm:ss") + " " + UiText.T(t.IsPrefill ? "Prefill" : "Generating"));
            }
            _gridTime.ResumeLayout();
        }

        /// <summary>
        /// 后端清单只在 --list（控制台模式）里输出，界面上不再单独占一块面板。
        /// 这里只负责：后端从「支持 /slots」变成「不支持」或反过来时，切换槽位表的显示。
        /// </summary>
        private void SyncCapabilities()
        {
            var rows = _backendRows ?? new List<BackendTarget>();
            bool slots = rows.Count == 0 || rows.Any(b => b.SupportsSlots);
            if (slots != _showSlots)
            {
                _showSlots = slots;
                ApplyCapabilities();
            }
        }

        private void RenderKv(long used)
        {
            bool has = _kvGuard != null;
            if (has != _hasKvGuard)
            {
                _hasKvGuard = has;
                ApplyCapabilities();
            }

            if (!has)
            {
                SetText(_lblKv, "");
                return;
            }
            long pool = AsLong(_kvGuard, "pool_tokens");
            double pct = pool > 0 ? 100.0 * used / pool : 0;
            SetText(_lblKv, UiText.F("KvPool", used.ToString("N0", CultureInfo.InvariantCulture),
                pool.ToString("N0", CultureInfo.InvariantCulture),
                pct.ToString("0.0", CultureInfo.InvariantCulture),
                AsString(_kvGuard, "safety_tokens"), AsString(_kvGuard, "default_output_tokens"),
                AsString(_kvGuard, "policy")));
            SetColor(_lblKv, pct > 85 ? CBad : (pct > 65 ? CWarn : CFg));
        }

        private string LogDisplayName()
        {
            if (!string.IsNullOrEmpty(_logPath)) return Path.GetFileName(_logPath);
            return UiText.T("NotFound");
        }

        /// <summary>
        /// 日志框渲染。
        /// 只追加新行、不整段重写（避免闪烁）；但用 .Text 赋值而不是 AppendText——
        /// AppendText 在这种「Dock=Fill + 表格布局」里的 TextBox 上不触发重绘，
        /// 会出现「内容有、画面空」的情况。缓冲区自己封顶，所以 .Text 赋的是定长文本。
        /// </summary>
        private void RenderTail(DateTime now)
        {
            if (_totalLogLines - _shownLines <= 0) return;
            if ((now - _lastTailAt).TotalSeconds < 0.9) return;

            int newCount = (int)Math.Min(_totalLogLines - _shownLines, _tail.Count);
            for (int i = _tail.Count - newCount; i < _tail.Count; i++)
                _logBuf.Append(Trunc(_tail[i], 190)).Append("\r\n");
            _shownLines = _totalLogLines;

            // 缓冲区封顶，超出就砍掉前面的（保持最近 400 行左右）
            if (_logBuf.Length > 36000)
            {
                string s = _logBuf.ToString();
                int cut = s.Length - 24000;
                int nl = s.IndexOf('\n', cut);
                if (nl >= 0) s = s.Substring(nl + 1);
                _logBuf.Length = 0;
                _logBuf.Append(s);
            }

            _txtTail.Text = _logBuf.ToString();
            _lastTailAt = now;
        }

        private static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // ───────────────────────── 日志解析 ─────────────────────────

        private static readonly Regex ReTiming = new Regex(
            @"print_timing:\s*id\s+(\d+)\s*\|\s*task\s+(-?\d+)\s*\|\s*n_gen\s*=\s*(\d+),\s*tg\s*=\s*([\d.]+)\s*t/s,\s*tg_3s\s*=\s*([\d.]+)",
            RegexOptions.Compiled);

        private static readonly Regex ReDraft = new Regex(
            @"print_timing:\s*id\s+(\d+)\s*\|\s*task\s+(-?\d+)\s*\|\s*draft acceptance\s*=\s*([\d.]+)\s*\(\s*(\d+)\s*accepted\s*/\s*(\d+)\s*generated\)\s*,\s*mean len\s*=\s*([\d.]+)",
            RegexOptions.Compiled);

        private static readonly Regex RePrefill = new Regex(
            @"id\s+(\d+)\s*\|\s*task\s+(\d+)\s*\|\s*prompt processing,\s*n_tokens\s*=\s*(\d+),\s*progress\s*=\s*([\d.]+),\s*t\s*=\s*([\d.]+)\s*s\s*/\s*([\d.]+)\s*tokens per second",
            RegexOptions.Compiled);

        private static readonly Regex ReWarn = new Regex(
            @"non-consecutive token position|W find_slot", RegexOptions.Compiled);

        private static readonly Regex ReError = new Regex(
            @"^\d{4}\.\d{2}\.\d{2}\.\d{3}\.\d{3}\s+E\s", RegexOptions.Compiled);

        private void ReadLogIncremental()
        {
            if (_paused) return;

            // 显式指定了日志文件就只盯它，不去猜别的
            if (!_useExplicitLog)
            {
                // 服务重启会写新日志文件，必须跟着切过去。
                // 先按目标推导（进程命令行里的 --log-file），推导不到再扫目录。
                string candidates = ServerDiscovery.ResolveLogFile(_target);
                if (string.IsNullOrEmpty(candidates))
                    candidates = FindNewestLog(CurrentLogDir());

                if (!string.IsNullOrEmpty(candidates) &&
                    !string.Equals(candidates, _logPath, StringComparison.OrdinalIgnoreCase))
                    SwitchLogFile(candidates);
            }

            if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return;

            var fi = new FileInfo(_logPath);
            if (fi.Length < _logPos)
            {
                // 文件被截断：从头读，并重置「已显示」计数，否则差值变负会永久卡住
                _logPos = 0;
                _shownLines = 0;
            }
            if (fi.Length == _logPos) return;

            string chunk;
            long consumed;
            try
            {
                using (var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(_logPos, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        chunk = sr.ReadToEnd();
                }
                consumed = Encoding.UTF8.GetByteCount(chunk);
            }
            catch (Exception ex)
            {
                Diag.Write("读日志失败: " + ex.Message);
                return;
            }

            if (consumed <= 0) return;
            _logPos += consumed;

            foreach (string line in chunk.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.Length == 0) continue;

                if (ReWarn.IsMatch(line))
                {
                    _warnCount++;
                }
                if (ReError.IsMatch(line)) _errCount++;

                Match m = ReTiming.Match(line);
                if (m.Success)
                {
                    var t = GetOrAddTask(int.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
                    t.NGen = long.Parse(m.Groups[3].Value);
                    t.Tg = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                    t.Tg3 = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
                    t.IsPrefill = false;
                    t.When = DateTime.Now;
                }
                else
                {
                    m = ReDraft.Match(line);
                    if (m.Success)
                    {
                        var t = GetOrAddTask(int.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
                        double acc = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                        t.Acceptance = acc.ToString("P1", CultureInfo.InvariantCulture)
                            + " (" + m.Groups[4].Value + "/" + m.Groups[5].Value + ")";
                        t.MeanLen = m.Groups[6].Value;
                        t.Seen = DateTime.Now;
                    }
                    else
                    {
                        m = RePrefill.Match(line);
                        if (m.Success)
                        {
                            var t = GetOrAddTask(int.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
                            t.NGen = long.Parse(m.Groups[3].Value);
                            t.Tg = double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);       // 预填充速度
                            t.Tg3 = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * 100.0; // 进度百分比
                            t.PrefillProgress = t.Tg3.Value;
                            t.IsPrefill = true;
                            t.When = DateTime.Now;
                            t.Seen = DateTime.Now;
                        }
                    }
                }
                _tail.Add(line);
                _totalLogLines++;
                if (_tail.Count > 500) _tail.RemoveRange(0, _tail.Count - 500);
            }
        }

        /// <summary>
        /// 切换到新的日志文件：行号计数必须归零，否则差值变负会永久卡住不刷新。
        /// 直接把读取位置跳到文件末尾前 64 KB，这样冷启动立刻显示最新内容，
        /// 不用从头补读整个日志。
        /// </summary>
        private void SwitchLogFile(string path)
        {
            _logPath = path;
            _logPos = 0;
            _totalLogLines = 0;
            _shownLines = 0;
            _logBuf.Length = 0;

            // 换了日志文件，之前那份统计出来的任务行就失效了，清掉
            _tasks.Clear();

            try
            {
                const long tailBytes = 64 * 1024;
                var fi = new FileInfo(path);
                if (fi.Length <= tailBytes) return;

                long start = fi.Length - tailBytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    int b;
                    while ((b = fs.ReadByte()) >= 0)
                    {
                        if (b == (int)'\n') break;   // 对齐到下一行开头，避免半行
                    }
                    _logPos = fs.Position;
                }
            }
            catch (Exception ex)
            {
                Diag.Write("定位日志末尾失败: " + ex.Message);
                _logPos = 0;
            }
        }

        private TaskRow GetOrAddTask(int slot, long task)
        {
            foreach (var t in _tasks)
            {
                if (t.Task == task)
                {
                    t.Slot = slot;
                    return t;
                }
            }
            var n = new TaskRow { Slot = slot, Task = task, When = DateTime.Now, Seen = DateTime.Now };
            _tasks.Add(n);
            if (_tasks.Count > 40)
            {
                _tasks.Sort((a, b) => a.Seen.CompareTo(b.Seen));
                _tasks.RemoveRange(0, _tasks.Count - 40);
            }
            return n;
        }

        private static string FindNewestLog(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return "";
                var files = new DirectoryInfo(dir).GetFiles("llama-server-*.log");
                if (files.Length == 0) return "";
                return files.OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;
            }
            catch { return ""; }
        }

        // ───────────────────────── GPU ─────────────────────────

        private void UpdateGpu()
        {
            string raw = null;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=index,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    raw = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                }
            }
            catch (Exception ex)
            {
                _gpuText = UiText.T("GpuCallFailed");
                Diag.Write("nvidia-smi 失败: " + ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(raw)) { _gpuText = UiText.T("GpuNoOutput"); return; }

            var parts = new List<string>();
            foreach (string line in raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var c = line.Split(',').Select(x => x.Trim()).ToArray();
                if (c.Length < 6) continue;
                double used, total, power;
                double.TryParse(c[2], NumberStyles.Any, CultureInfo.InvariantCulture, out used);
                double.TryParse(c[3], NumberStyles.Any, CultureInfo.InvariantCulture, out total);
                double.TryParse(c[5], NumberStyles.Any, CultureInfo.InvariantCulture, out power);
                int pct = total > 0 ? (int)Math.Round(100.0 * used / total) : 0;
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    UiText.T("GpuFormat"),
                    c[0], c[1], (int)Math.Round(power), c[4], (long)used, (long)total));
            }
            _gpuText = parts.Count > 0 ? string.Join("   ‖   ", parts.ToArray()) : UiText.T("GpuNoOutput");
        }

        // ───────────────────────── 小工具 ─────────────────────────

        private static void SetText(Control c, string text)
        {
            if (c != null && c.Text != text) c.Text = text;
        }

        private static void SetColor(Control c, Color color)
        {
            if (c != null && c.ForeColor != color) c.ForeColor = color;
        }
    }
}
