// 自动发现 llama-server：进程 → 命令行参数 → 端口 → HTTP 探活。
//
// 设计目标：换一台机器、换一个端口、换一个 llama.cpp 构建，都能自己找到服务，
// 不需要用户手填任何路径。任何一步失败都不抛异常，而是继续往下退：
//   进程找不到        → 退回按常见端口探测
//   命令行读不到      → 退回按常见端口探测
//   日志参数里没有    → 退回按进程所在目录 / 常见位置找
//   端口不通          → 继续试下一个候选

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
    /// <summary>一个已定位的 llama-server 实例。</summary>
    internal sealed class ServerTarget
    {
        public int Port;
        public string Host = "127.0.0.1";
        public int Pid;
        public string Executable = "";
        public string CommandLine = "";
        public string ModelPath = "";
        public string LogFile = "";
        public long CtxSize;
        public int Parallel;
        public bool FromProcess;      // true = 从进程命令行解析出来的（最可信）
        public bool Alive;            // /health 是否返回 ok
        public string BuildInfo = "";
        public int TotalSlots;
        public string Discovered;     // 人类可读的来源说明，显示在界面上

        public string Label
        {
            get
            {
                string model = string.IsNullOrEmpty(ModelPath) ? "?" : Path.GetFileNameWithoutExtension(ModelPath);
                string where = FromProcess && Pid > 0 ? ("PID " + Pid) : "端口探测";
                return Host + ":" + Port + "   " + model + "   (" + where + ")";
            }
        }
    }

    internal static class ServerDiscovery
    {
        /// <summary>没有可用线索时的常见端口，按流行度排序。</summary>
        private static readonly int[] CommonPorts = { 8080, 1234, 8000, 1235, 5000, 11434, 8081, 7860 };

        private static readonly Regex ReArg = new Regex(
            "\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"|(?<v>[^\\s]+)", RegexOptions.Compiled);

        /// <summary>把命令行拆成参数数组，去掉 argv[0]（可执行文件本身）。</summary>
        public static string[] SplitArgs(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return new string[0];
            var list = new List<string>();
            foreach (Match m in ReArg.Matches(commandLine))
                list.Add(m.Groups["v"].Value);
            if (list.Count > 0) list.RemoveAt(0);   // 去掉 exe 路径
            return list.ToArray();
        }

        /// <summary>从参数数组里取 --flag value；找不到返回 null。也支持 --flag=value。</summary>
        public static string GetArg(string[] args, params string[] names)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                foreach (string name in names)
                {
                    if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                        return (i + 1 < args.Length) ? args[i + 1] : null;

                    string prefix = name + "=";
                    if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return a.Substring(prefix.Length);
                }
            }
            return null;
        }

        /// <summary>枚举所有 llama-server 进程，解析出实例信息。</summary>
        public static List<ServerTarget> FromProcesses()
        {
            var result = new List<ServerTarget>();
            Process[] procs;
            try { procs = Process.GetProcessesByName("llama-server"); }
            catch { return result; }

            foreach (var p in procs)
            {
                try
                {
                    string exe = null;
                    try { exe = p.MainModule != null ? p.MainModule.FileName : null; } catch { }

                    string cmd = NativeCommandLine.Get(p.Id);
                    var t = new ServerTarget
                    {
                        Pid = p.Id,
                        Executable = exe ?? "",
                        CommandLine = cmd ?? "",
                        FromProcess = true
                    };

                    string[] args = SplitArgs(cmd);

                    string portText = GetArg(args, "--port", "-p");
                    int port;
                    if (portText != null && int.TryParse(portText, out port) && port > 0 && port <= 65535)
                        t.Port = port;
                    else
                        t.Port = 8080;    // llama-server 未指定端口时的官方默认值

                    string host = GetArg(args, "--host");
                    if (!string.IsNullOrEmpty(host) && host != "0.0.0.0" && host != "::")
                        t.Host = host;

                    t.ModelPath = GetArg(args, "--model", "-m", "--model-url") ?? "";
                    t.LogFile = GetArg(args, "--log-file") ?? "";
                    t.Parallel = ParseInt(GetArg(args, "--parallel", "-np"), 1);

                    // 有些启动器用 --alias 给模型起短名，优先展示它
                    string alias = GetArg(args, "--alias", "-a");
                    if (!string.IsNullOrEmpty(alias)) t.ModelPath = alias;

                    result.Add(t);
                }
                catch
                {
                    // 单个进程读失败不影响其它
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            return result
                .GroupBy(x => x.Port)
                .Select(g => g.First())
                .OrderBy(x => x.Port)
                .ToList();
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return (s != null && int.TryParse(s, out v)) ? v : fallback;
        }

        /// <summary>探测一个端口上是否有活得 llama-server。</summary>
        public static bool Probe(ServerTarget t, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + t.Host + ":" + t.Port + "/health");
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    t.Alive = body.IndexOf("\"ok\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    return t.Alive;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 主入口：优先用显式指定的端口；否则找进程；再否则试常见端口。
        /// 返回第一个探活成功的实例；全失败则返回 null。
        /// </summary>
        public static List<ServerTarget> Discover(int explicitPort, int timeoutMs)
        {
            var candidates = new List<ServerTarget>();

            if (explicitPort > 0)
            {
                candidates.Add(new ServerTarget { Port = explicitPort, FromProcess = false, Discovered = "命令行指定" });
            }
            else
            {
                var fromProc = FromProcesses();
                foreach (var t in fromProc)
                {
                    t.Discovered = "由进程 PID " + t.Pid + " 的命令行解析";
                    candidates.Add(t);
                }

                foreach (int p in CommonPorts)
                {
                    if (candidates.Any(c => c.Port == p)) continue;
                    candidates.Add(new ServerTarget { Port = p, FromProcess = false, Discovered = "常见端口探测" });
                }
            }

            // 先探活，再补 /props 元数据
            var alive = new List<ServerTarget>();
            foreach (var t in candidates)
            {
                if (Probe(t, timeoutMs))
                {
                    if (string.IsNullOrEmpty(t.Discovered))
                        t.Discovered = t.FromProcess ? ("PID " + t.Pid) : "端口探测";
                    FillProps(t, timeoutMs);
                    alive.Add(t);
                }
            }
            return alive;
        }

        /// <summary>用 /props 补齐模型名、构建号、槽位数。</summary>
        private static void FillProps(ServerTarget t, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + t.Host + ":" + t.Port + "/props");
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    body = sr.ReadToEnd();

                t.BuildInfo = JsonString(body, "build_info");
                t.TotalSlots = JsonInt(body, "total_slots");

                string mp = JsonString(body, "model_path");
                if (!string.IsNullOrEmpty(mp) && (string.IsNullOrEmpty(t.ModelPath) || t.ModelPath.IndexOf('.') < 0))
                    t.ModelPath = mp;
            }
            catch
            {
                // 元数据拿不到不影响使用
            }
        }

        private static readonly Regex ReJsonStr = new Regex("\"(?<k>\\w+)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        private static readonly Regex ReJsonNum = new Regex("\"(?<k>\\w+)\"\\s*:\\s*(?<v>-?\\d+)", RegexOptions.Compiled);

        private static string JsonString(string json, string key)
        {
            foreach (Match m in ReJsonStr.Matches(json))
                if (string.Equals(m.Groups["k"].Value, key, StringComparison.OrdinalIgnoreCase))
                    return m.Groups["v"].Value;
            return "";
        }

        private static int JsonInt(string json, string key)
        {
            foreach (Match m in ReJsonNum.Matches(json))
                if (string.Equals(m.Groups["k"].Value, key, StringComparison.OrdinalIgnoreCase))
                {
                    int v;
                    if (int.TryParse(m.Groups["v"].Value, out v)) return v;
                }
            return 0;
        }

        /// <summary>
        /// 找日志文件：优先进程命令行里的 --log-file；
        /// 否则在几个常见位置找最新的 llama-server*.log。
        /// </summary>
        public static string ResolveLogFile(ServerTarget t)
        {
            if (t != null && !string.IsNullOrEmpty(t.LogFile) && File.Exists(t.LogFile))
                return t.LogFile;

            var dirs = new List<string>();
            if (t != null && !string.IsNullOrEmpty(t.Executable))
            {
                try { dirs.Add(Path.GetDirectoryName(t.Executable)); } catch { }
            }
            if (t != null && !string.IsNullOrEmpty(t.LogFile))
            {
                try { dirs.Add(Path.GetDirectoryName(t.LogFile)); } catch { }
            }
            string cwd = Environment.CurrentDirectory;
            dirs.Add(cwd);
            dirs.Add(Path.Combine(cwd, "logs"));
            dirs.Add(Path.Combine(cwd, "data", "logs"));
            try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "llama.cpp")); } catch { }

            foreach (string d in dirs.Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                string f = NewestLogIn(d);
                if (!string.IsNullOrEmpty(f)) return f;
            }
            return "";
        }

        private static string NewestLogIn(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return "";
                var files = new DirectoryInfo(dir)
                    .GetFiles("llama-server*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                if (files.Count > 0) return files[0].FullName;

                // 有些构建把日志写成 llama-*.log 或 *.log
                files = new DirectoryInfo(dir)
                    .GetFiles("llama*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                return files.Count > 0 ? files[0].FullName : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
