using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AIUsageTray
{
    /// <summary>Starts a process in a private kill-on-close job and owns its redirected streams.</summary>
    internal sealed class OwnedProcess : IDisposable
    {
        private readonly object gate = new object();
        private IntPtr job;
        private bool stopped;
        private bool disposed;

        internal Process Process { get; private set; }
        internal StreamWriter StandardInput { get; private set; }
        internal StreamReader StandardOutput { get; private set; }
        internal StreamReader StandardError { get; private set; }

        private OwnedProcess() { }

        internal static OwnedProcess Start(ProcessStartInfo launch, CancellationToken cancellation)
        {
            if (launch == null) throw new ArgumentNullException("launch");
            if (String.IsNullOrEmpty(launch.FileName)) throw new ArgumentException("FileName is required.", "launch");
            if (!Path.IsPathRooted(launch.FileName)) throw new ArgumentException("FileName must be an absolute path.", "launch");
            cancellation.ThrowIfCancellationRequested();

            IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
            IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
            IntPtr errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
            IntPtr attrList = IntPtr.Zero;
            IntPtr handleArray = IntPtr.Zero;
            IntPtr processHandle = IntPtr.Zero, threadHandle = IntPtr.Zero;
            IntPtr jobHandle = IntPtr.Zero;
            Process process = null;
            FileStream stdin = null, stdout = null, stderr = null;
            StreamWriter inputWriter = null;
            StreamReader outputReader = null, errorReader = null;
            bool assigned = false;
            try
            {
                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                sa.bInheritHandle = true;
                if (!CreatePipe(out inRead, out inWrite, ref sa, 0) ||
                    !CreatePipe(out outRead, out outWrite, ref sa, 0) ||
                    !CreatePipe(out errRead, out errWrite, ref sa, 0)) ThrowLastError();
                if (!SetHandleInformation(inWrite, HANDLE_FLAG_INHERIT, 0) ||
                    !SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0) ||
                    !SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0)) ThrowLastError();

                IntPtr[] inherited = new IntPtr[] { inRead, outWrite, errWrite };
                IntPtr bytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attrList = Marshal.AllocHGlobal(bytes);
                if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref bytes)) ThrowLastError();
                handleArray = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
                for (int i = 0; i < inherited.Length; i++) Marshal.WriteIntPtr(handleArray, i * IntPtr.Size, inherited[i]);
                if (!UpdateProcThreadAttribute(attrList, 0, new IntPtr(PROC_THREAD_ATTRIBUTE_HANDLE_LIST.ToUInt32()),
                    handleArray, (IntPtr)(IntPtr.Size * inherited.Length), IntPtr.Zero, IntPtr.Zero)) ThrowLastError();

                STARTUPINFOEX si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                si.StartupInfo.dwFlags = (int)STARTF_USESTDHANDLES;
                si.StartupInfo.hStdInput = inRead;
                si.StartupInfo.hStdOutput = outWrite;
                si.StartupInfo.hStdError = errWrite;
                si.lpAttributeList = attrList;
                PROCESS_INFORMATION pi;
                string command = QuoteArgument(launch.FileName) + (String.IsNullOrEmpty(launch.Arguments) ? "" : " " + launch.Arguments);
                string directory = String.IsNullOrEmpty(launch.WorkingDirectory) ? Environment.CurrentDirectory : launch.WorkingDirectory;
                string env = BuildEnvironment(launch);
                uint flags = CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT;
                if (!CreateProcess(launch.FileName, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                    flags, env, directory, ref si, out pi)) ThrowLastError();
                processHandle = pi.hProcess;
                threadHandle = pi.hThread;

                // The child has copied the handles. Parent copies must close before any reads can finish.
                Close(ref inRead); Close(ref outWrite); Close(ref errWrite);
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero) ThrowLastError();
                JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int limitSize = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr limitPtr = Marshal.AllocHGlobal(limitSize);
                try
                {
                    Marshal.StructureToPtr(limits, limitPtr, false);
                    if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, limitPtr, (uint)limitSize)) ThrowLastError();
                }
                finally { Marshal.FreeHGlobal(limitPtr); }
                if (!AssignProcessToJobObject(jobHandle, processHandle)) ThrowLastError();
                assigned = true;
                process = Process.GetProcessById((int)pi.dwProcessId);
                // Open Process.Handle while the root is still suspended.
                IntPtr ignored = process.Handle;
                stdin = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 4096, false); inWrite = IntPtr.Zero;
                stdout = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 1, false); outRead = IntPtr.Zero;
                stderr = new FileStream(new SafeFileHandle(errRead, true), FileAccess.Read, 1, false); errRead = IntPtr.Zero;
                inputWriter = new StreamWriter(stdin, new UTF8Encoding(false), 1024); stdin = null;
                outputReader = new StreamReader(new PollingPipeStream(stdout, cancellation), Encoding.UTF8, true); stdout = null;
                errorReader = new StreamReader(new PollingPipeStream(stderr, cancellation), Encoding.UTF8, true); stderr = null;
                cancellation.ThrowIfCancellationRequested();
                if (ResumeThread(threadHandle) == UInt32.MaxValue) ThrowLastError();
                Close(ref threadHandle);
                Close(ref processHandle);
                OwnedProcess result = new OwnedProcess();
                result.job = jobHandle; jobHandle = IntPtr.Zero;
                result.Process = process;
                result.StandardInput = inputWriter; inputWriter = null;
                result.StandardOutput = outputReader; outputReader = null;
                result.StandardError = errorReader; errorReader = null;
                return result;
            }
            catch
            {
                if (assigned && jobHandle != IntPtr.Zero) { Close(ref jobHandle); }
                else if (processHandle != IntPtr.Zero) { TerminateProcess(processHandle, 1); }
                if (process != null) try { process.Dispose(); } catch { }
                if (inputWriter != null) try { inputWriter.Dispose(); } catch { }
                if (outputReader != null) try { outputReader.Dispose(); } catch { }
                if (errorReader != null) try { errorReader.Dispose(); } catch { }
                if (stdin != null) try { stdin.Dispose(); } catch { }
                if (stdout != null) try { stdout.Dispose(); } catch { }
                if (stderr != null) try { stderr.Dispose(); } catch { }
                throw;
            }
            finally
            {
                Close(ref inRead); Close(ref inWrite); Close(ref outRead); Close(ref outWrite); Close(ref errRead); Close(ref errWrite);
                Close(ref threadHandle); Close(ref processHandle); Close(ref jobHandle);
                if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
                if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            }
        }

        internal void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                Close(ref job);
            }
            try { if (Process != null) Process.WaitForExit(1500); } catch { }
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Stop();
            try { if (StandardInput != null) StandardInput.Dispose(); } catch { }
            try { if (StandardOutput != null) StandardOutput.Dispose(); } catch { }
            try { if (StandardError != null) StandardError.Dispose(); } catch { }
            try { if (Process != null) Process.Dispose(); } catch { }
        }

        private static string BuildEnvironment(ProcessStartInfo launch)
        {
            StringBuilder b = new StringBuilder();
            System.Collections.Specialized.StringDictionary vars = launch.EnvironmentVariables;
            string[] keys = new string[vars.Count];
            vars.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys)
            {
                string value = vars[key] ?? "";
                b.Append(key).Append('=').Append(value).Append('\0');
            }
            b.Append('\0');
            return b.ToString();
        }

        private static string QuoteArgument(string value)
        {
            StringBuilder b = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                b.Append('\\', slashes).Append(c); slashes = 0;
            }
            b.Append('\\', slashes * 2).Append('"');
            return b.ToString();
        }

        private sealed class PollingPipeStream : Stream
        {
            private readonly FileStream pipe;
            private readonly CancellationToken cancellation;
            internal PollingPipeStream(FileStream pipe, CancellationToken cancellation) { this.pipe = pipe; this.cancellation = cancellation; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0) throw new ArgumentOutOfRangeException("offset");
                if (count < 0) throw new ArgumentOutOfRangeException("count");
                if (offset > buffer.Length - count) throw new ArgumentException("Invalid offset and count.");
                if (count == 0) return 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    uint available;
                    if (!PeekNamedPipe(pipe.SafeFileHandle, IntPtr.Zero, 0, IntPtr.Zero, out available, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA) return 0;
                        throw new IOException("Could not read process output pipe.", new System.ComponentModel.Win32Exception(error));
                    }
                    if (available != 0)
                    {
                        int n = (int)Math.Min((uint)count, available);
                        return pipe.Read(buffer, offset, n);
                    }
                    Thread.Sleep(20);
                }
            }
            protected override void Dispose(bool disposing) { if (disposing) pipe.Dispose(); base.Dispose(disposing); }
            public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { } public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); } public override void SetLength(long v) { throw new NotSupportedException(); } public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }

        private static void ThrowLastError() { throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        private static void Close(ref IntPtr h) { if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h); h = IntPtr.Zero; }

        private const uint CREATE_SUSPENDED = 0x4, CREATE_NO_WINDOW = 0x08000000, EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x400;
        private const uint HANDLE_FLAG_INHERIT = 1, STARTF_USESTDHANDLES = 0x100, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9, ERROR_BROKEN_PIPE = 109, ERROR_NO_DATA = 232;
        private static readonly UIntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new UIntPtr(0x00020002);

        [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES sa, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr procAttrs, IntPtr threadAttrs, bool inherit, uint flags, string environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PeekNamedPipe(SafeFileHandle pipe, IntPtr buffer, uint size, IntPtr read, out uint available, IntPtr left);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
