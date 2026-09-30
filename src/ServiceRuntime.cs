using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace LlamaMonitor
{
    // Read the owner of the actual listening socket, including explicit --port targets.
    internal static class LocalProcessResolver
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            bool order, int family, int tableClass, uint reserved);

        public static int Find(string host, int port)
        {
            if (port < 1 || port > 65535) return 0;
            IPAddress address = null;
            bool localhost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (!localhost && (!IPAddress.TryParse(host, out address) || !IPAddress.IsLoopback(address))) return 0;
            var ids = new HashSet<int>();
            try
            {
                if (localhost || host.IndexOf(':') < 0) ReadTable(2, port, address, ids);
                if (localhost || host.IndexOf(':') >= 0) ReadTable(23, port, address, ids);
            }
            catch { return 0; }
            if (ids.Count != 1) return 0;
            foreach (int id in ids) return id;
            return 0;
        }

        private static void ReadTable(int family, int port, IPAddress requested, HashSet<int> ids)
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (size < 4 || size > 16 * 1024 * 1024) return;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) return;
                int count = Marshal.ReadInt32(buffer);
                int rowSize = family == 2 ? 24 : 56;
                if (count < 0 || count > (size - 4) / rowSize) return;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * rowSize);
                    int rawPort = Marshal.ReadInt32(row, family == 2 ? 8 : 20);
                    int rowPort = ((rawPort & 255) << 8) | ((rawPort >> 8) & 255);
                    if (rowPort != port) continue;
                    bool matches;
                    if (family == 2)
                    {
                        uint local = unchecked((uint)Marshal.ReadInt32(row, 4));
                        var localAddress = new IPAddress(BitConverter.GetBytes(local));
                        matches = local == 0 || (requested == null ? IPAddress.IsLoopback(localAddress) : localAddress.Equals(requested));
                    }
                    else
                    {
                        var bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, 16);
                        var local = new IPAddress(bytes);
                        matches = local.Equals(IPAddress.IPv6Any) || (requested == null ? IPAddress.IsLoopback(local) : local.Equals(requested));
                    }
                    int pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    if (matches && pid > 0) ids.Add(pid);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static void Attach(BackendTarget target)
        {
            int pid = Find(target.Host, target.Port);
            if (pid <= 0) return;
            target.Pid = pid;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    target.Executable = process.MainModule == null ? "" : process.MainModule.FileName;
            }
            catch { }
        }
    }

    internal sealed class ServiceRuntime
    {
        private string _address = "";
        private int _pid;
        private DateTime? _processStartUtc;
        private TimeSpan _elapsedBase;
        private long _elapsedTicks;
        private DateTime _lastResolveUtc = DateTime.MinValue;
        private bool _online;

        public bool Observe(BackendTarget target, bool online, DateTime nowUtc)
        {
            string address = target == null ? "" : target.Address;
            int pid = target == null ? 0 : target.Pid;
            DateTime? started = null;
            if (online && target != null)
            {
                if (address != _address || _pid == 0 || (nowUtc - _lastResolveUtc).TotalSeconds >= 5)
                {
                    int owner = LocalProcessResolver.Find(target.Host, target.Port);
                    pid = target.Pid = owner;
                    _lastResolveUtc = nowUtc;
                }
                else pid = _pid;
                try
                {
                    if (pid > 0)
                        using (var process = Process.GetProcessById(pid))
                            started = process.StartTime.ToUniversalTime();
                }
                catch { pid = 0; }
            }
            return Update(address, online, pid, started, nowUtc);
        }

        // Kept separate so restart, fallback, and clock edge cases can be tested without a server.
        internal bool Update(string address, bool online, int pid, DateTime? startedUtc, DateTime nowUtc)
        {
            bool changed = address != _address || (online && (pid != _pid || startedUtc != _processStartUtc));
            if (online && (changed || !_online))
            {
                _elapsedBase = startedUtc.HasValue ? nowUtc - startedUtc.Value : TimeSpan.Zero;
                if (_elapsedBase < TimeSpan.Zero) _elapsedBase = TimeSpan.Zero;
                _elapsedTicks = Stopwatch.GetTimestamp();
            }
            _address = address;
            if (online) { _pid = pid; _processStartUtc = startedUtc; }
            _online = online;
            return changed;
        }

        public string Display(DateTime nowUtc)
        {
            if (!_online) return UiText.F("ServiceUptime", "—");
            TimeSpan elapsed = _elapsedBase + TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - _elapsedTicks) / (double)Stopwatch.Frequency);
            return UiText.F(_processStartUtc.HasValue ? "ServiceUptime" : "ObservedUptime",
                FormatElapsed(elapsed));
        }

        internal static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            string time = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                elapsed.Hours, elapsed.Minutes, elapsed.Seconds);
            return elapsed.Days > 0 ? elapsed.Days.ToString(CultureInfo.InvariantCulture)
                + (UiText.English ? "d " : "天 ") + time : time;
        }
    }
}
