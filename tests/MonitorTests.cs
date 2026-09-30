using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LlamaMonitor
{
    internal static class MonitorTests
    {
        private static int _checks;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                DpiFix.SetPerMonitorAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string artifacts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts");
                Directory.CreateDirectory(artifacts);
                TestMetrics();
                TestRuntime();
                TestLogScroll();
                TestForms(artifacts);
                Console.WriteLine("PASS: " + _checks + " checks. Rendered UI fixtures: " + artifacts);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void Check(bool condition, string message)
        {
            _checks++;
            if (!condition) throw new Exception("FAIL: " + message);
        }

        private static Dictionary<string, object> Object(string json)
        { return (Dictionary<string, object>)Json.DeserializeObject(json); }

        private static void TestMetrics()
        {
            var row = SlotMetrics.Read(Object("{\"id\":0,\"id_task\":0,\"is_processing\":true,\"n_prompt_tokens\":100,\"n_ctx\":200,\"next_token\":[{\"n_decoded\":20}]}"));
            Check(row.NPast == 100 && row.NDecoded == 20 && row.ContextText == "100/200 (50.0%)", "current array format must not double count generation");
            Check(row.Task == 0 && row.PromptProcessed == -1, "task zero is valid; missing counters remain unknown");
            row = SlotMetrics.Read(Object("{\"id\":1,\"is_processing\":false,\"n_prompt_tokens\":131894,\"n_ctx\":245760,\"next_token\":{\"n_decoded\":0}}"));
            Check(row.NPast == 131894 && row.NDecoded == 0, "idle retained tokens and legacy object format");
            row = SlotMetrics.Read(Object("{\"n_past\":80,\"n_prompt_tokens\":100,\"n_decoded\":15,\"n_ctx\":200}"));
            Check(row.NPast == 80 && row.NDecoded == 15, "explicit retained count and top-level decoded");
            row = SlotMetrics.Read(Object("{\"n_ctx\":200,\"next_token\":[],\"n_decoded\":null}"));
            Check(row.NPast == -1 && row.NDecoded == -1 && row.ContextText == "—", "older schemas must not invent usage");
            row = SlotMetrics.Read(Object("{\"n_prompt_tokens\":\"invalid\",\"n_ctx\":-1,\"n_decoded\":true}"));
            Check(row.NPast == -1 && row.NCtx == 0 && row.NDecoded == -1, "malformed counters remain unknown");
            row = SlotMetrics.Read(Object("{\"n_prompt_tokens\":0,\"n_ctx\":200,\"n_decoded\":0}"));
            Check(row.ContextText == "0/200 (0.0%)" && SlotMetrics.Count(row.NDecoded) == "0", "known zero is distinct from missing");
        }

        private static void TestRuntime()
        {
            UiText.Configure("en");
            DateTime now = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
            var runtime = new ServiceRuntime();
            Check(runtime.Update("local:8000", true, 10, now.AddHours(-2), now), "initial process identity");
            Check(runtime.Display(now).Contains("Service uptime 02:00:00"), "process age rather than monitor age");
            Check(!runtime.Update("local:8000", true, 10, now.AddHours(-2), now.AddSeconds(1)), "same process preserves identity");
            Check(runtime.Update("local:8000", true, 10, now, now), "PID reuse with a new start time resets session");
            Check(runtime.Display(now).Contains("00:00:00"), "restart resets elapsed display");
            runtime.Update("local:8000", false, 0, null, now);
            Check(runtime.Display(now).Contains("—"), "offline has no live uptime");
            runtime.Update("remote:8000", true, 0, null, now);
            Check(runtime.Display(now).StartsWith("Observed online"), "unknown process uses labeled observed time");
            Check(ServiceRuntime.FormatElapsed(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(3661)) == "2d 01:01:01", "multi-day duration");
            Check(ServiceRuntime.FormatElapsed(TimeSpan.FromSeconds(-1)) == "00:00:00", "future start times are clamped");
            var remote = new BackendTarget { Host = "198.51.100.10", Port = 8000, Pid = Process.GetCurrentProcess().Id };
            runtime.Observe(remote, true, now);
            Check(runtime.Display(now).StartsWith("Observed online"), "an unverified candidate PID is not service uptime");
            using (var listener = new FixtureServer())
            {
                Check(LocalProcessResolver.Find("127.0.0.1", listener.Port) == Process.GetCurrentProcess().Id, "actual IPv4 listener ownership");
                Check(LocalProcessResolver.Find("127.0.0.2", listener.Port) == 0, "another loopback address does not match");
                Check(LocalProcessResolver.Find("198.51.100.10", listener.Port) == 0, "remote endpoints are not attributed to local processes");
            }
            if (Socket.OSSupportsIPv6)
            {
                var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
                try
                {
                    listener.Start();
                    Check(LocalProcessResolver.Find("::1", ((IPEndPoint)listener.LocalEndpoint).Port) == Process.GetCurrentProcess().Id, "actual IPv6 listener ownership");
                }
                finally { listener.Stop(); }
            }
        }

        private static int FirstLine(TextBox box)
        { return SendMessage(box.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero).ToInt32(); }

        private static void TestLogScroll()
        {
            using (var host = new Form())
            using (var box = new LogTailTextBox { Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Vertical, Size = new Size(400, 100) })
            {
                host.Controls.Add(box);
                IntPtr handle = box.Handle;
                string text = string.Join("\r\n", Enumerable.Range(0, 100).Select(i => "Log line " + i).ToArray()) + "\r\n";
                box.ReplaceLogText(text, 0, true);
                Check(FirstLine(box) > 80, "cold-start tail follows latest before visibility");
                box.ReplaceLogText(text + "new line\r\n", 0, false);
                Check(FirstLine(box) > 80, "bottom follows append");
                SendMessage(handle, 0xB6, IntPtr.Zero, new IntPtr(-60));
                int top = FirstLine(box);
                box.Select(box.GetFirstCharIndexFromLine(top + 1), 4);
                box.ReplaceLogText(text + "new line\r\nnewer line\r\n", 0, false);
                Check(FirstLine(box) == top && box.SelectionLength == 4, "reading position and selection survive append");
                int cut = box.GetFirstCharIndexFromLine(10);
                string selected = box.SelectedText;
                box.ReplaceLogText(box.Text.Substring(cut), cut, false);
                Check(FirstLine(box) == top - 10 && box.SelectedText == selected, "front trim keeps the same retained text in view");
                box.JumpToLatest();
                Check(box.SelectionStart == box.TextLength && box.SelectionLength == 0 && FirstLine(box) > 65, "Latest button restores tail following");
            }
        }

        private static object Field(object obj, string name)
        { return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj); }
        private static object Call(object obj, string name, params object[] args)
        { return obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args); }
        private static IEnumerable<Control> Descendants(Control parent)
        { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        private static void Render(Form form, string path)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            form.PerformLayout();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }
        }

        private static void TestForms(string artifacts)
        {
            using (var server = new FixtureServer())
            {
                string log = Path.Combine(artifacts, "fixture.log");
                foreach (string language in new[] { "en", "zh" })
                {
                    File.WriteAllText(log, string.Join("\r\n", Enumerable.Range(0, 120).Select(i => "Fixture log line " + i).ToArray()));
                    UiText.Configure(language);
                    var config = MonitorConfig.Parse(new[] { "--port", server.Port.ToString(), "--lang", language, "--log-file", log, "--interval", "60000", "--no-topmost" });
                    using (var form = new MonitorForm(config))
                    {
                        form.ClientSize = new Size(1180, 720);
                        var rows = (List<SlotRow>)Field(form, "_lastSlots");
                        Check(rows.Count == 2 && rows[0].NPast == 100 && rows[0].NDecoded == 20, "full view consumes fixture slots " + language);
                        var grid = (DataGridView)Field(form, "_gridSlot");
                        Check((string)grid.Rows[0].Cells[1].Value == "0", "task zero is shown " + language);
                        Check(((Label)Field(form, "_lblRuntime")).Text.Contains(language == "en" ? "Service uptime" : "连续运行"), "localized runtime label " + language);
                        Check(((Button)Field(form, "_btnLatest")).Text == (language == "en" ? "Latest" : "到底部"), "localized latest action " + language);
                        Call(form, "ReadLogIncremental");
                        Call(form, "RenderTail", DateTime.Now.AddSeconds(1));
                        var tail = (LogTailTextBox)Field(form, "_txtTail");
                        Check(tail.Text.Contains("Fixture log line 119"), "full log refresh renders newest lines " + language);
                        Render(form, Path.Combine(artifacts, "full-" + language + ".png"));
                        Check(new[] { "_btnPause", "_btnLog", "_btnWeb", "_btnLanguage", "_btnMini", "_btnLatest" }.All(name => { var button = (Button)Field(form, name); return TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.ClientSize.Width; }), "full buttons fit both languages and display DPI " + language);
                        var latestButton = (Button)Field(form, "_btnLatest");
                        Check(latestButton.ClientSize.Height >= TextRenderer.MeasureText(latestButton.Text, latestButton.Font).Height + 6, "latest button text is not vertically clipped " + language);

                        Call(form, "TogglePause");
                        int requests = server.RequestCount;
                        Call(form, "ToggleLanguage");
                        Check(server.RequestCount == requests && object.ReferenceEquals(rows, Field(form, "_lastSlots")), "language switch preserves paused snapshot without polling " + language);
                        Call(form, "ToggleLanguage");
                        Call(form, "TogglePause");

                        // Task rollover and counter rollback must not create a spurious speed spike.
                        DateTime time = DateTime.Now;
                        Call(form, "BuildSlotRow", Object("{\"id\":9,\"id_task\":1,\"is_processing\":true,\"n_decoded\":10}"), time);
                        var rate = (SlotRow)Call(form, "BuildSlotRow", Object("{\"id\":9,\"id_task\":1,\"is_processing\":true,\"n_decoded\":20}"), time.AddSeconds(1));
                        Check(rate.TpsInstant == 10, "same-task delta speed " + language);
                        rate = (SlotRow)Call(form, "BuildSlotRow", Object("{\"id\":9,\"id_task\":2,\"is_processing\":true,\"n_decoded\":100}"), time.AddSeconds(2));
                        Check(rate.TpsInstant == 0 && rate.TpsWindow == 0, "task switch clears speed history " + language);
                        rate = (SlotRow)Call(form, "BuildSlotRow", Object("{\"id\":9,\"id_task\":2,\"is_processing\":true,\"n_decoded\":1}"), time.AddSeconds(3));
                        Check(rate.TpsInstant == 0, "counter rollback clears history " + language);
                        Call(form, "BuildSlotRow", Object("{\"id\":9,\"id_task\":2,\"is_processing\":true}"), time.AddSeconds(4));
                        rate = (SlotRow)Call(form, "BuildSlotRow", Object("{\"id\":9,\"id_task\":2,\"is_processing\":true,\"n_decoded\":100}"), time.AddSeconds(5));
                        Check(rate.TpsInstant == 0 && rate.TpsWindow == 0, "missing-to-known counter establishes a fresh speed baseline " + language);

                        using (var mini = new MiniMonitorForm(false))
                        {
                            mini.UpdateSnapshot(new MiniSnapshot { Online = true, SupportsSlots = true, Service = "llama.cpp · fixture", Uptime = "01:23:45", Gpu = "GPU 0 25% | 150 W", Slots = rows });
                            Check(Descendants(mini).Any(c => c.Text.Contains(language == "en" ? "Token usage" : "Token 占用")), "localized mini usage " + language);
                            Render(mini, Path.Combine(artifacts, "mini-" + language + ".png"));
                            Check(Descendants(mini).OfType<Button>().All(button => TextRenderer.MeasureText(button.Text, button.Font).Width + 8 <= button.ClientSize.Width), "mini buttons fit the display DPI " + language);
                            mini.Width = mini.MinimumSize.Width;
                            Render(mini, Path.Combine(artifacts, "mini-narrow-" + language + ".png"));
                            mini.UpdateSnapshot(new MiniSnapshot { Online = false, SupportsSlots = true, Slots = rows });
                            Check(!Descendants(mini).Any(c => c.Text.Contains("100/200")), "offline mini removes stale counts " + language);
                        }
                        Call(form, "ObserveService", false);
                        Check(grid.Rows.Count == 0 && (List<SlotRow>)Field(form, "_lastSlots") == null, "disconnect clears full-view slots " + language);
                        Check((bool)Field(form, "_propsMissing"), "disconnect invalidates service metadata " + language);

                        File.WriteAllText(log, "");
                        Call(form, "ReadLogIncremental");
                        Call(form, "RenderTail", DateTime.Now);
                        Check(tail.TextLength == 0, "truncate-to-empty clears the visible log " + language);
                        File.WriteAllText(log, "replacement line\r\n");
                        Call(form, "ReadLogIncremental");
                        Call(form, "RenderTail", DateTime.Now.AddSeconds(2));
                        Check(tail.Text.Contains("replacement line"), "logging resumes after truncation " + language);
                        string emptyLog = Path.Combine(artifacts, "empty.log");
                        File.WriteAllText(emptyLog, "");
                        Call(form, "SwitchLogFile", emptyLog);
                        Call(form, "RenderTail", DateTime.Now);
                        Check(tail.TextLength == 0, "switch-to-empty clears the previous log " + language);

                        Call(form, "ShowMini");
                        var sharedMini = (MiniMonitorForm)Field(form, "_mini");
                        sharedMini.StartPosition = FormStartPosition.Manual;
                        sharedMini.Location = new Point(-30000, -30000);
                        Check(sharedMini.Visible && !form.Visible, "mini replaces full window " + language);
                        ((Button)Field(sharedMini, "_fullButton")).PerformClick();
                        Check(form.Visible && !sharedMini.Visible, "Full restores existing main window " + language);
                        Call(form, "ShowMini");
                        ((Button)Field(sharedMini, "_pauseButton")).PerformClick();
                        Check((bool)Field(form, "_paused") && ((Button)Field(form, "_btnPause")).Text == UiText.T("Resume"), "mini pause shares main state " + language);
                        sharedMini.Close();
                        Check(form.IsDisposed && sharedMini.IsDisposed, "closing mini exits both windows " + language);
                    }
                }
            }
        }

        private sealed class FixtureServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread _thread;
            private volatile bool _stopped;
            private int _requestCount;
            public int RequestCount { get { return Interlocked.CompareExchange(ref _requestCount, 0, 0); } }
            public int Port { get; private set; }
            public FixtureServer()
            {
                _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(Run) { IsBackground = true }; _thread.Start();
            }
            private void Run()
            {
                while (!_stopped)
                {
                    try
                    {
                        using (var client = _listener.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                        {
                            client.ReceiveTimeout = 2000;
                            string request = reader.ReadLine() ?? "";
                            Interlocked.Increment(ref _requestCount);
                            string header;
                            do { header = reader.ReadLine(); } while (!string.IsNullOrEmpty(header));
                            string path = request.Split(' ').Length > 1 ? request.Split(' ')[1] : "";
                            string body = path == "/health" ? "{\"status\":\"ok\"}"
                                : path == "/props" ? "{\"build_info\":\"fixture\",\"model_path\":\"fixture-model.gguf\",\"total_slots\":2}"
                                : path == "/slots" ? "[{\"id\":0,\"id_task\":0,\"is_processing\":true,\"n_prompt_tokens\":100,\"n_ctx\":200,\"n_prompt_tokens_processed\":10,\"n_prompt_tokens_cache\":70,\"next_token\":[{\"n_decoded\":20}]},{\"id\":1,\"is_processing\":false,\"n_prompt_tokens\":131894,\"n_ctx\":245760,\"n_prompt_tokens_processed\":0,\"n_prompt_tokens_cache\":0,\"next_token\":{\"n_decoded\":0}}]"
                                : null;
                            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "{}");
                            string response = "HTTP/1.1 " + (body == null ? "404 Not Found" : "200 OK") + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
                            byte[] head = Encoding.ASCII.GetBytes(response); stream.Write(head, 0, head.Length); stream.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch { if (_stopped) return; }
                }
            }
            public void Dispose() { _stopped = true; _listener.Stop(); _thread.Join(2000); }
        }
    }
}
