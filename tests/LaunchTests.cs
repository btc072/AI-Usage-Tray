using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class LaunchTests
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    private static int Main(string[] args)
    {
        string binary = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
        ProcessStartInfo start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.EnvironmentVariables["PATH"] = Path.Combine(root, "bin") + ";" + Environment.GetEnvironmentVariable("PATH");
        start.EnvironmentVariables["XDG_DATA_HOME"] = root;
        start.EnvironmentVariables["CODEX_HOME"] = root;
        start.EnvironmentVariables["TRAY_TEST_MODE"] = "success";
        start.EnvironmentVariables["TRAY_TEST_PIDS"] = Path.Combine(root, "launch-pids.txt");
        using (Process process = Process.Start(start))
        {
            try
            {
                IntPtr window = IntPtr.Zero;
                Stopwatch watch = Stopwatch.StartNew();
                while (window == IntPtr.Zero && !process.HasExited && watch.ElapsedMilliseconds < 5000)
                {
                    EnumWindows(delegate(IntPtr candidate, IntPtr unused) {
                        uint id; GetWindowThreadProcessId(candidate, out id);
                        StringBuilder title = new StringBuilder(200); GetWindowText(candidate, title, title.Capacity);
                        if (id == process.Id && title.ToString() == "AI Usage Tray message window") window = candidate;
                        return true;
                    }, IntPtr.Zero);
                    Thread.Sleep(20);
                }
                if (window == IntPtr.Zero) throw new Exception("own application window was not created");
                Thread.Sleep(500);
                if (process.HasExited) throw new Exception("application exited during startup");
                if (!PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero) || !process.WaitForExit(5000))
                    throw new Exception("application did not exit gracefully");
                if (process.ExitCode != 0) throw new Exception("application exit code was " + process.ExitCode);
                Console.WriteLine("PASS: final EXE launches and exits gracefully (owned PID only)");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL: " + error.Message); return 1; }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(1000); } }
        }
    }
}
