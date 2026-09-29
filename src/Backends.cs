// 多种本地推理后端的适配层。
//
// 原则：每个后端能提供什么就用什么，拿不到的一律留空/标“不适用”，
//       绝不假装所有后端都有 llama.cpp 那样的 /slots 槽位数据。
//
//   llama.cpp (llama-server) 槽位级：提示词进度、缓存命中、每槽吞吐  → 最完整
//   vLLM                     有 /metrics，能拿到累计生成 token 数差分 → 可算吞吐
//   Ollama / LM Studio 等     只有模型列表 + 健康状态                → 只报存在与模型
//
// 所有探测都是只读 GET，不修改任何后端状态。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaMonitor
{
    internal enum BackendKind
    {
        Unknown,
        LlamaCpp,
        Ollama,
        LmStudio,
        Vllm,
        SgLang,
        KoboldCpp,
        TextGenWebUi,
        LocalAi,
        TabbyApi
    }

    /// <summary>一个探测到的后端实例，以及它能提供哪些能力。</summary>
    internal sealed class BackendTarget
    {
        public BackendKind Kind = BackendKind.Unknown;
        public string Host = "127.0.0.1";
        public int Port;
        public int Pid;
        public string Executable = "";
        public string CommandLine = "";

        public bool Alive;
        public string Version = "";
        public string LogFile = "";

        /// <summary>该后端报告已加载/可用的模型。</summary>
        public List<string> Models = new List<string>();

        // 能力标记：界面据此决定显示哪些列
        public bool SupportsSlots;        // /slots 槽位明细
        public bool SupportsMetrics;      // 能算整体吞吐
        public bool SupportsModelList;

        /// <summary>Discovery metadata stays language-neutral; labels are rendered for the current UI language.</summary>
        public string DiscoveryKind = "";
        public string DiscoveryProcess = "";
        public int DiscoveryPid;

        public string Discovered
        {
            get
            {
                if (DiscoveryKind == "command") return UiText.T("DiscoveryCommand");
                if (DiscoveryKind == "ports") return UiText.T("DiscoveryPorts");
                if (DiscoveryKind == "process") return UiText.F("DiscoveryProcess", DiscoveryProcess, DiscoveryPid);
                return "";
            }
        }

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case BackendKind.LlamaCpp: return "llama.cpp";
                    case BackendKind.Ollama: return "Ollama";
                    case BackendKind.LmStudio: return "LM Studio";
                    case BackendKind.Vllm: return "vLLM";
                    case BackendKind.SgLang: return "SGLang";
                    case BackendKind.KoboldCpp: return "KoboldCpp";
                    case BackendKind.TextGenWebUi: return "text-gen-webui";
                    case BackendKind.LocalAi: return "LocalAI";
                    case BackendKind.TabbyApi: return "TabbyAPI";
                    default: return UiText.T("UnknownBackend");
                }
            }
        }

        public string Address { get { return Host + ":" + Port; } }

        public string ModelSummary
        {
            get
            {
                if (Models.Count == 0) return "—";
                if (Models.Count == 1) return Models[0];
                return UiText.F("MoreModels", Models[0], Models.Count - 1);
            }
        }

        /// <summary>这个后端能提供什么，显示在界面上，避免用户困惑为什么有些列是空的。</summary>
        public string Capability
        {
            get
            {
                var parts = new List<string>();
                if (SupportsSlots) parts.Add(UiText.T("CapabilitySlots"));
                if (SupportsMetrics) parts.Add(UiText.T("CapabilityThroughput"));
                if (SupportsModelList) parts.Add(UiText.T("CapabilityModels"));
                return parts.Count > 0 ? string.Join("+", parts.ToArray()) : UiText.T("CapabilityHealth");
            }
        }

        public string Label
        {
            get { return KindName + "  " + Address + (Pid > 0 ? "  (PID " + Pid + ")" : ""); }
        }
    }

    internal static class Backends
    {
        /// <summary>进程名 → 后端类型。用于按进程快速识别。</summary>
        private static readonly Dictionary<string, BackendKind> ByProcess =
            new Dictionary<string, BackendKind>(StringComparer.OrdinalIgnoreCase)
            {
                { "llama-server",   BackendKind.LlamaCpp },
                { "llama-cli",      BackendKind.LlamaCpp },
                { "ollama",         BackendKind.Ollama },
                { "ollama app",     BackendKind.Ollama },
                { "LM Studio",      BackendKind.LmStudio },
                { "lms",            BackendKind.LmStudio },
                { "koboldcpp",      BackendKind.KoboldCpp },
                { "koboldcpp_cu12", BackendKind.KoboldCpp },
                { "python",         BackendKind.Unknown },   // vLLM/SGLang 跑在 python 下，看端口判断
                { "vllm",           BackendKind.Vllm },
                { "sglang",         BackendKind.SgLang },
                { "tabbyAPI",       BackendKind.TabbyApi },
                { "text-generation-webui", BackendKind.TextGenWebUi },
                { "local-ai",       BackendKind.LocalAi },
            };

        /// <summary>端口探测时的候选（端口 + 该端口最可能的后端类型）。</summary>
        private sealed class PortHint
        {
            public int Port;
            public BackendKind Kind;
            public PortHint(int port, BackendKind kind) { Port = port; Kind = kind; }
        }

        /// <summary>常见端口 → 最可能的后端。用于端口探测时的判定顺序。</summary>
        private static readonly PortHint[] PortHints =
        {
            new PortHint(8080,  BackendKind.LlamaCpp),
            new PortHint(11434, BackendKind.Ollama),
            new PortHint(1234,  BackendKind.LmStudio),
            new PortHint(8000,  BackendKind.Vllm),
            new PortHint(30000, BackendKind.SgLang),
            new PortHint(5001,  BackendKind.KoboldCpp),
            new PortHint(5000,  BackendKind.TextGenWebUi),
            new PortHint(1235,  BackendKind.LlamaCpp),
            new PortHint(8081,  BackendKind.LlamaCpp),
        };

        private static readonly Regex ReTagModel = new Regex(
            "\"model\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        private static readonly Regex ReTagName = new Regex(
            "\"name\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        /// <summary>探测所有可能的本地后端。</summary>
        public static List<BackendTarget> DiscoverAll(int explicitPort, int timeoutMs)
        {
            var found = new List<BackendTarget>();
            var tried = new HashSet<int>();

            // 1) 从进程命令行拿到准确端口（最可信）
            foreach (var p in ProcessBackends())
            {
                if (tried.Contains(p.Port)) continue;
                tried.Add(p.Port);
                if (Probe(p, timeoutMs)) found.Add(p);
            }

            // 2) 显式端口
            if (explicitPort > 0 && !tried.Contains(explicitPort))
            {
                tried.Add(explicitPort);
                var t = new BackendTarget { Port = explicitPort, DiscoveryKind = "command" };
                if (Probe(t, timeoutMs)) found.Add(t);
            }

            // 3) 常见端口（已经试过的跳过）
            foreach (var hint in PortHints)
            {
                if (tried.Contains(hint.Port)) continue;
                tried.Add(hint.Port);
                var t = new BackendTarget { Port = hint.Port, Kind = hint.Kind, DiscoveryKind = "ports" };
                if (Probe(t, timeoutMs)) found.Add(t);
            }

            return found.OrderBy(f => f.Port).ToList();
        }

        /// <summary>按进程识别后端，并尝试从命令行解析端口。</summary>
        private static List<BackendTarget> ProcessBackends()
        {
            var list = new List<BackendTarget>();
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return list; }

            foreach (var p in procs)
            {
                try
                {
                    BackendKind kind;
                    if (!ByProcess.TryGetValue(p.ProcessName, out kind)) continue;
                    if (kind == BackendKind.Unknown && p.ProcessName.Equals("python", StringComparison.OrdinalIgnoreCase))
                        continue;   // python 太泛，交给端口探测

                    int port = PortFromCommandLine(p.Id) ?? DefaultPortFor(kind);
                    if (port <= 0) continue;

                    var t = new BackendTarget
                    {
                        Kind = kind,
                        Port = port,
                        Pid = p.Id,
                        CommandLine = "",
                        DiscoveryKind = "process",
                        DiscoveryProcess = p.ProcessName,
                        DiscoveryPid = p.Id
                    };
                    try { t.Executable = p.MainModule != null ? p.MainModule.FileName : ""; } catch { }
                    list.Add(t);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return list;
        }

        private static int DefaultPortFor(BackendKind kind)
        {
            switch (kind)
            {
                case BackendKind.LlamaCpp: return 8080;
                case BackendKind.Ollama: return 11434;
                case BackendKind.LmStudio: return 1234;
                case BackendKind.Vllm: return 8000;
                case BackendKind.SgLang: return 30000;
                case BackendKind.KoboldCpp: return 5001;
                case BackendKind.TextGenWebUi: return 5000;
                case BackendKind.LocalAi: return 8080;
                case BackendKind.TabbyApi: return 5000;
                default: return 0;
            }
        }

        private static int? PortFromCommandLine(int pid)
        {
            string cmd = NativeCommandLine.Get(pid);
            if (string.IsNullOrEmpty(cmd)) return null;
            var args = ServerDiscovery.SplitArgs(cmd);
            foreach (string name in new[] { "--port", "-p", "--api-port", "--listen-port", "--host-port" })
            {
                string v = ServerDiscovery.GetArg(args, name);
                int port;
                if (v != null && int.TryParse(v, out port) && port > 0 && port <= 65535) return port;
            }
            return null;
        }

        /// <summary>探测一个实例：探活 + 识别类型 + 抓取能力。</summary>
        public static bool Probe(BackendTarget t, int timeoutMs)
        {
            string body;

            // /health 是 llama.cpp、vLLM、SGLang、LocalAI 都认的探活入口
            if (TryGet(t, "/health", timeoutMs, out body))
            {
                t.Alive = true;
                if (t.Kind == BackendKind.Unknown)
                    t.Kind = GuessFromHealth(body);
            }

            // Ollama 没有 /health，但有 /api/version
            if (!t.Alive)
            {
                if (TryGet(t, "/api/version", timeoutMs, out body))
                {
                    t.Alive = true;
                    t.Kind = BackendKind.Ollama;
                    t.Version = ExtractString(body, "version");
                }
            }

            // 再兜一层：能列出模型就说明它是个推理服务
            if (!t.Alive)
            {
                if (TryGet(t, "/v1/models", timeoutMs, out body))
                {
                    t.Alive = true;
                    if (t.Kind == BackendKind.Unknown) t.Kind = BackendKind.TextGenWebUi;
                }
            }

            if (!t.Alive) return false;

            // 按类型补齐元数据
            Identify(t, timeoutMs);
            FetchModels(t, timeoutMs);
            LocateLog(t);
            return true;
        }

        public static bool IsAlive(BackendTarget t, int timeoutMs)
        {
            if (t == null) return false;
            string body;
            if (t.Kind == BackendKind.Ollama && TryGet(t, "/api/version", timeoutMs, out body)) return true;
            if (TryGet(t, "/health", timeoutMs, out body)) return true;
            return TryGet(t, "/v1/models", timeoutMs, out body);
        }

        private static BackendKind GuessFromHealth(string body)
        {
            // vLLM 的 /health 返回空 body 或 200；llama.cpp 返回 {"status":"ok"}
            if (body != null && body.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0)
                return BackendKind.LlamaCpp;
            return BackendKind.Unknown;
        }

        /// <summary>用各后端自己的特征接口确定类型与版本。</summary>
        private static void Identify(BackendTarget t, int timeoutMs)
        {
            string body;

            // llama.cpp: /props 里有 build_info / model_path / total_slots
            if (TryGet(t, "/props", timeoutMs, out body))
            {
                if (body.IndexOf("build_info", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    body.IndexOf("model_path", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    t.Kind = BackendKind.LlamaCpp;
                    t.Version = ExtractString(body, "build_info");
                    t.SupportsSlots = true;
                    t.SupportsModelList = true;

                    string mp = ExtractString(body, "model_path");
                    if (!string.IsNullOrEmpty(mp))
                    {
                        try { t.Models.Add(Path.GetFileNameWithoutExtension(mp)); } catch { t.Models.Add(mp); }
                    }
                }
            }

            // Ollama: /api/tags 或 /api/ps
            if (t.Kind == BackendKind.Ollama || t.Kind == BackendKind.Unknown)
            {
                if (TryGet(t, "/api/tags", timeoutMs, out body) && body.IndexOf("\"models\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    t.Kind = BackendKind.Ollama;
            }

            // vLLM: /metrics 是 Prometheus 文本，认 vllm: 前缀
            if (TryGet(t, "/metrics", timeoutMs, out body))
            {
                if (body.IndexOf("vllm:", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    t.Kind = BackendKind.Vllm;
                    t.SupportsMetrics = true;
                }
                else if (body.IndexOf("sglang", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    t.Kind = BackendKind.SgLang;
                    t.SupportsMetrics = true;
                }
            }

            // KoboldCpp: /api/v1/model 返回 {"result":"..."}
            if (t.Kind == BackendKind.Unknown || t.Kind == BackendKind.KoboldCpp)
            {
                if (TryGet(t, "/api/v1/model", timeoutMs, out body) && body.IndexOf("\"result\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    t.Kind = BackendKind.KoboldCpp;
            }

            if (TryGet(t, "/api/extra/version", timeoutMs, out body) && body.IndexOf("kobold", StringComparison.OrdinalIgnoreCase) >= 0)
                t.Kind = BackendKind.KoboldCpp;
        }

        /// <summary>抓模型列表（能用 OpenAI 兼容接口的都试一遍）。</summary>
        private static void FetchModels(BackendTarget t, int timeoutMs)
        {
            string body;

            if (t.Kind == BackendKind.Ollama)
            {
                if (TryGet(t, "/api/ps", timeoutMs, out body))
                {
                    foreach (Match m in ReTagModel.Matches(body))
                    {
                        string name = m.Groups["v"].Value;
                        if (!string.IsNullOrEmpty(name) && !t.Models.Contains(name)) t.Models.Add(name);
                    }
                }
                if (t.Models.Count == 0 && TryGet(t, "/api/tags", timeoutMs, out body))
                {
                    foreach (Match m in ReTagName.Matches(body))
                    {
                        string name = m.Groups["v"].Value;
                        if (!string.IsNullOrEmpty(name) && !t.Models.Contains(name)) t.Models.Add(name);
                    }
                }
                t.SupportsModelList = t.Models.Count > 0;
                return;
            }

            if (t.Models.Count > 0) { t.SupportsModelList = true; return; }

            if (TryGet(t, "/v1/models", timeoutMs, out body))
            {
                foreach (Match m in ReTagModel.Matches(body))
                {
                    string name = m.Groups["v"].Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    try { name = Path.GetFileName(name); } catch { }
                    if (!t.Models.Contains(name)) t.Models.Add(name);
                }
                t.SupportsModelList = t.Models.Count > 0;
            }
        }

        /// <summary>找该后端可能在写的日志文件。</summary>
        private static void LocateLog(BackendTarget t)
        {
            if (t.Kind == BackendKind.LlamaCpp)
            {
                // llama-server 会把 --log-file 写进命令行
                string cmd = NativeCommandLine.Get(t.Pid);
                if (!string.IsNullOrEmpty(cmd))
                {
                    var args = ServerDiscovery.SplitArgs(cmd);
                    string lf = ServerDiscovery.GetArg(args, "--log-file");
                    if (!string.IsNullOrEmpty(lf) && File.Exists(lf)) t.LogFile = lf;
                }
            }

            if (string.IsNullOrEmpty(t.LogFile) && t.Kind == BackendKind.Ollama)
            {
                // Ollama 的日志位置因安装方式而异，常见几处都试一下
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (string cand in new[]
                {
                    Path.Combine(local, "Ollama", "server.log"),
                    Path.Combine(local, "Ollama", "ollama.log"),
                })
                {
                    if (File.Exists(cand)) { t.LogFile = cand; break; }
                }
            }
        }

        // ───────────── 取整机吞吐（目前只有 vLLM 这一类能算） ─────────────

        private static readonly Regex RePromCounter = new Regex(
            @"^(?<k>[a-zA-Z_:][\w:]*)\{(?<labels>[^}]*)\}\s+(?<v>[\d.eE+-]+)\s*$|^(?<k2>[a-zA-Z_:][\w:]*)\s+(?<v2>[\d.eE+-]+)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// 从 /metrics 里取累计生成 token 数。返回 -1 表示这个后端没有可用指标。
        /// 调用方对两次采样做差即可得到吞吐。
        /// </summary>
        public static double GetGeneratedTokens(BackendTarget t, int timeoutMs)
        {
            if (t == null || !t.SupportsMetrics) return -1;
            string body;
            if (!TryGet(t, "/metrics", timeoutMs, out body)) return -1;

            // vLLM: vllm:generation_tokens_total
            // 兼容其它命名：generation_tokens_total / tokens_generated_total
            foreach (string key in new[] { "vllm:generation_tokens_total", "generation_tokens_total", "tokens_generated_total", "vllm:request_generation_tokens_sum" })
            {
                Match m = Regex.Match(body, "^" + Regex.Escape(key) + @"(?:\{[^}]*\})?\s+(?<v>[\d.eE+-]+)",
                    RegexOptions.Multiline);
                if (m.Success)
                {
                    double v;
                    if (double.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out v))
                        return v;
                }
            }
            return -1;
        }

        // ───────────── 小工具 ─────────────

        private static bool TryGet(BackendTarget t, string path, int timeoutMs, out string body)
        {
            body = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + t.Host + ":" + t.Port + path);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Method = "GET";
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    body = sr.ReadToEnd();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups["v"].Value : "";
        }
    }
}
