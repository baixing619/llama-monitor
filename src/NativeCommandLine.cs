// 读取任意进程的命令行，不依赖 CIM / WMI。
//
// 为什么不用 Get-CimInstance：
//   1. 某些受限环境（沙箱、服务账户）下 CIM 会被拒绝；
//   2. 跨进程读 CommandLine 在部分权限配置下直接抛异常；
//   3. 每次刷新都可能要查，WMI 开销偏大。
//
// 主路径：NtQueryInformationProcess(ProcessCommandLineInformation, 60)，
//         Windows 8.1+ 支持，一次调用拿到完整命令行。
// 兜底：  走 PEB（兼容更老系统）。两条路都只读，失败返回 null，
//         调用方必须容忍 null 并退回端口探测。

using System;
using System.Runtime.InteropServices;

namespace LlamaMonitor
{
    internal static class NativeCommandLine
    {
        private const int ProcessBasicInformationClass = 0;
        private const int ProcessCommandLineInformation = 60;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;          // 字节数，不含结尾 NUL
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2a;
            public IntPtr Reserved2b;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr handle, int kind, IntPtr buffer, uint size, out uint needed);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr handle, IntPtr baseAddr, IntPtr buffer, IntPtr size, out IntPtr read);

        /// <summary>取进程命令行；任何失败都返回 null，不抛异常。</summary>
        public static string Get(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                string text = ViaCommandLineInformation(handle);
                if (!string.IsNullOrEmpty(text)) return text;
                return ViaPeb(handle);
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static string ViaCommandLineInformation(IntPtr handle)
        {
            uint needed;
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out needed);
            if (needed < 16 || needed > 1024 * 1024) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out needed) != 0)
                    return null;

                var text = (UnicodeString)Marshal.PtrToStructure(buffer, typeof(UnicodeString));
                if (text.Length == 0 || text.Buffer == IntPtr.Zero || text.Length % 2 != 0)
                    return null;

                long start = text.Buffer.ToInt64();
                long end = buffer.ToInt64() + needed;
                if (start < buffer.ToInt64() || start + text.Length > end)
                    return null;

                return Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ViaPeb(IntPtr handle)
        {
            int pbiSize = Marshal.SizeOf(typeof(ProcessBasicInformation));
            IntPtr pbi = Marshal.AllocHGlobal(pbiSize);
            try
            {
                uint needed;
                if (NtQueryInformationProcess(handle, ProcessBasicInformationClass, pbi, (uint)pbiSize, out needed) != 0)
                    return null;

                var info = (ProcessBasicInformation)Marshal.PtrToStructure(pbi, typeof(ProcessBasicInformation));
                if (info.PebBaseAddress == IntPtr.Zero) return null;

                // 仅 x64 布局；32 位进程偏移不同，读不到就返回 null 走端口探测
                if (IntPtr.Size != 8) return null;

                IntPtr processParameters = ReadPointer(handle, Offset(info.PebBaseAddress, 0x20));
                if (processParameters == IntPtr.Zero) return null;

                // RTL_USER_PROCESS_PARAMETERS.CommandLine 在 x64 下偏移 0x70
                var cmd = ReadStruct<UnicodeString>(handle, Offset(processParameters, 0x70));
                if (cmd.Length == 0 || cmd.Length > 32768 || cmd.Buffer == IntPtr.Zero) return null;

                return ReadUnicode(handle, cmd.Buffer, cmd.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(pbi);
            }
        }

        private static IntPtr Offset(IntPtr p, int offset)
        {
            return new IntPtr(p.ToInt64() + offset);
        }

        private static IntPtr ReadPointer(IntPtr handle, IntPtr addr)
        {
            IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                IntPtr read;
                if (!ReadProcessMemory(handle, addr, buf, (IntPtr)IntPtr.Size, out read)) return IntPtr.Zero;
                return Marshal.ReadIntPtr(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>把远程内存里的结构体读进本地分配的缓冲区并转换，缓冲区由本方法释放。</summary>
        private static T ReadStruct<T>(IntPtr handle, IntPtr addr) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                IntPtr read;
                if (!ReadProcessMemory(handle, addr, buf, (IntPtr)size, out read))
                    return default(T);
                return (T)Marshal.PtrToStructure(buf, typeof(T));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static string ReadUnicode(IntPtr handle, IntPtr addr, int charCount)
        {
            int bytes = charCount * 2;
            IntPtr buf = Marshal.AllocHGlobal(bytes);
            try
            {
                IntPtr read;
                if (!ReadProcessMemory(handle, addr, buf, (IntPtr)bytes, out read)) return null;
                return Marshal.PtrToStringUni(buf, charCount);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}
